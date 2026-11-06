#!/usr/bin/env python3
"""Isolated R3 dual-AC read API contract check using an offline fixture."""
import json
import os
from pathlib import Path
import signal
import sys
import subprocess
import tempfile
import time
import urllib.error
import urllib.request


def request(base, path, token=None):
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    req = urllib.request.Request(base + path, headers=headers)
    try:
        with urllib.request.urlopen(req, timeout=8) as response:
            return response.status, json.loads(response.read())
    except urllib.error.HTTPError as error:
        body = error.read().decode(errors="replace")
        try:
            body = json.loads(body)
        except json.JSONDecodeError:
            pass
        return error.code, body


def unused_port():
    import socket
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        return sock.getsockname()[1]


def verify_explicit_group_evidence(base, token, fixture, document):
    """Exercise source-key, Domain and source filtering through real HTTP routes."""
    document = json.loads(json.dumps(document))
    # AC1 has only MAC evidence; AC2 supplies the complete AP identity. Selecting
    # AC1 must preserve the canonical resource locator while narrowing its data.
    document["observations"][0]["serialEvidence"] = None
    fixture.write_text(json.dumps(document), encoding="utf-8")
    status, resources = request(base, "/api/system/wireless/ap-resources", token)
    assert status == 200 and resources["total"] == 1, (status, resources)
    resource = resources["items"][0]
    document["groupEvidence"] = [
        dict(sourceAcDeviceId=item["sourceAcDeviceId"], sourceSiteId=item["sourceSiteId"],
             sourceGroupKey="vendor-group-17", displayName="default-group",
             sourceObservationIds=[item["id"]], capturedMs=item["capturedMs"],
             sourceRevision=item["sourceRevision"], rawManifestRef=item["rawManifestRef"],
             parserRevision=item["parserRevision"])
        for item in resource["observations"]
    ]
    fixture.write_text(json.dumps(document), encoding="utf-8")
    endpoint = "/api/system/wireless/ac-domains/domain-ac1-ac2/ap-groups"
    status, local = request(base, endpoint, token)
    assert status == 200 and local["total"] == 2, (status, local)
    assert {item["groupIdentityKey"] for item in local["items"]} == {
        "AC|ac-device-1|vendor-group-17", "AC|ac-device-2|vendor-group-17"}
    assert all(item["groupIdentityStatus"] == "CONFIRMED" for item in local["items"])

    for item in document["groupEvidence"]:
        item.update(sourceAcDomainId="domain-ac1-ac2", domainMemberDeviceIds=["ac-device-1", "ac-device-2"],
                    domainSharedEvidenceRefs=["fixture:explicit-domain-shared"])
    fixture.write_text(json.dumps(document), encoding="utf-8")
    status, shared = request(base, endpoint, token)
    assert status == 200 and shared["total"] == 1, (status, shared)
    group = shared["items"][0]
    assert group["groupIdentityKey"] == "DOMAIN|domain-ac1-ac2|vendor-group-17"
    assert group["groupIdentityStatus"] == "CONFIRMED"
    assert group["sourceAcDeviceIds"] == ["ac-device-1", "ac-device-2"]
    for source in group["sourceAcDeviceIds"]:
        status, scoped_list = request(base, endpoint + f"?sourceAcId={source}", token)
        assert status == 200 and scoped_list["total"] == 1, (status, scoped_list)
        scoped_group = scoped_list["items"][0]
        assert scoped_group["id"] == group["id"]
        assert scoped_group["sourceAcDeviceIds"] == [source]
        assert scoped_group["memberResourceIds"] == [resource["id"]]
        detail_path = f"/api/system/wireless/ap-groups/{group['id']}?sourceAcId={source}"
        status, detail = request(base, detail_path, token)
        assert status == 200 and detail["total"] == 1, (status, detail)
        assert {item["sourceAcDeviceId"] for item in detail["observations"]} == {source}
        assert {item["sourceAcDeviceId"] for item in detail["group"]["sourceGroupObservations"]} == {source}
        member = detail["members"][0]
        assert member["id"] == resource["id"]
        assert member["sourceObservationIds"] == scoped_group["sourceObservationIds"]
        assert {item["sourceAcDeviceId"] for item in member["observations"]} == {source}
        assert {item["normalizedState"] for item in member["observations"]} == {
            "RUNNING_MASTER" if source == "ac-device-1" else "RUNNING_BACKUP"}
        if source == "ac-device-1":
            assert member["serialEvidence"] is None
        status, members = request(base, f"/api/system/wireless/ap-groups/{group['id']}/members?sourceAcId={source}", token)
        assert status == 200 and members["total"] == 1
        assert members["items"][0]["id"] == resource["id"]
        status, canonical = request(base, f"/api/system/wireless/ap-resources/{member['id']}", token)
        assert status == 200, (status, canonical)

    for item in document["groupEvidence"]:
        item["displayName"] = "renamed-group"
        item["capturedMs"] += 1000
        item["sourceRevision"] += ":renamed"
    fixture.write_text(json.dumps(document), encoding="utf-8")
    status, renamed = request(base, endpoint, token)
    assert status == 200 and renamed["total"] == 1
    renamed = renamed["items"][0]
    assert renamed["id"] == group["id"] and renamed["membershipRevision"] == group["membershipRevision"]
    assert renamed["sourceRevision"] != group["sourceRevision"] and renamed["displayName"] == "renamed-group"
    for item in document["groupEvidence"]:
        item["domainMemberDeviceIds"] = ["ac-device-1"]
    fixture.write_text(json.dumps(document), encoding="utf-8")
    status, conflict = request(base, endpoint, token)
    assert status == 200 and conflict["total"] == 2
    assert all(item["groupScope"] == "AC" and item["groupIdentityStatus"] == "CONFLICT" for item in conflict["items"])
    print("R3_AP_GROUP_EXPLICIT_SOURCE_AND_DOMAIN=PASS")
    print("R3_AP_GROUP_SCOPED_CANONICAL_MEMBERS=PASS")
    print("R3_AP_GROUP_RENAME_AND_DOMAIN_CONFLICT=PASS")


def main():
    repo = Path(__file__).resolve().parents[1]
    dll = Path(os.environ.get("R3_SERVICE_DLL", repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"))
    if not dll.is_file():
        raise RuntimeError("Run dotnet build -c Release before this isolated API test")
    sys.path.insert(0, str(repo))
    from tools.workspace_paths import workspace_path
    test_root = workspace_path("test-data")
    test_root.mkdir(parents=True, exist_ok=True)
    source_fixture = repo / "tests" / "NetConsole.Next.Service.Tests" / "fixtures" / "r3_dual_ac_wireless.json"
    with tempfile.TemporaryDirectory(prefix="netconsole-next-r3-dual-ac-", dir=test_root) as temp:
        fixture_document = json.loads(source_fixture.read_text(encoding="utf-8"))
        fixture = Path(temp) / "wireless-fixture.json"
        fixture.write_text(json.dumps(fixture_document), encoding="utf-8")
        port = unused_port()
        env = dict(os.environ, NETCONSOLE_NEXT_DATA_ROOT=str(Path(temp) / "data"),
                   NETCONSOLE_NEXT_WEB_PORT=str(port), NETCONSOLE_NEXT_SYSLOG_PORT="0",
                   NETCONSOLE_NEXT_WIRELESS_FIXTURE_PATH=str(fixture),
                   NETCONSOLE_NEXT_H3C_ENABLED="1")
        creationflags = subprocess.CREATE_NEW_PROCESS_GROUP if os.name == "nt" else 0
        service = subprocess.Popen(["dotnet", str(dll)], cwd=repo, env=env, stdout=subprocess.PIPE,
                                   stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace",
                                   creationflags=creationflags)
        base = f"http://127.0.0.1:{port}"
        try:
            for _ in range(120):
                if service.poll() is not None:
                    raise RuntimeError("service exited before health became ready")
                try:
                    status, _ = request(base, "/api/system/health")
                except (urllib.error.URLError, ConnectionError, TimeoutError):
                    time.sleep(0.1)
                    continue
                if status == 200:
                    break
                time.sleep(0.1)
            else:
                raise AssertionError("service did not become healthy")
            token = (Path(temp) / "data" / "local-api-token").read_text().strip()
            status, domains = request(base, "/api/system/wireless/ac-domains?page=1&pageSize=10", token)
            assert status == 200 and domains["total"] == 1, (status, domains)
            domain = domains["items"][0]
            assert domain["domainKind"] == "ONE_PLUS_ONE" and domain["topologyStatus"] == "CONFIRMED"
            assert set(domain["memberDeviceIds"]) == {"ac-device-1", "ac-device-2"}

            status, resources = request(base, "/api/system/wireless/ap-resources?page=1&pageSize=10", token)
            assert status == 200 and resources["total"] == 1, (status, resources)
            resource = resources["items"][0]
            assert resource["identityStatus"] == "CONFIRMED"
            assert len(resource["observations"]) == 2
            assert {item["sourceAcDeviceId"] for item in resource["observations"]} == {"ac-device-1", "ac-device-2"}

            status, topology = request(base, "/api/system/wireless/ac-domains/domain-ac1-ac2/topology", token)
            assert status == 200 and topology["confirmed"] is True
            status, associations = request(base, "/api/system/wireless/ac-domains/domain-ac1-ac2/ap-associations", token)
            assert status == 200 and associations["total"] == 2
            assert {item["sourceAcDeviceId"] for item in associations["items"]} == {"ac-device-1", "ac-device-2"}
            status, ac1_associations = request(base, "/api/system/wireless/ac-domains/domain-ac1-ac2/ap-associations?sourceAcId=ac-device-1", token)
            assert status == 200 and ac1_associations["total"] == 1
            assert ac1_associations["items"][0]["rawManifestRef"].startswith("fixture:ac1:")
            status, manifests = request(base, "/api/system/wireless/ac-domains/domain-ac1-ac2/collection-manifests?page=1&pageSize=10", token)
            assert status == 200 and manifests["total"] == 1 and manifests["items"][0]["observationCount"] == 2, (status, manifests)
            manifest_id = manifests["items"][0]["id"]
            status, manifest_detail = request(base, f"/api/system/wireless/collection-manifests/{manifest_id}", token)
            assert status == 200 and manifest_detail["manifest"]["id"] == manifest_id
            assert len(manifest_detail["items"]) == 2
            status, ac1_manifest_detail = request(base, f"/api/system/wireless/collection-manifests/{manifest_id}?sourceAcId=ac-device-1", token)
            assert status == 200 and len(ac1_manifest_detail["items"]) == 1
            assert ac1_manifest_detail["manifest"]["sourceAcDeviceIds"] == ["ac-device-1"]
            status, ac1_manifests = request(base, "/api/system/wireless/ac-domains/domain-ac1-ac2/collection-manifests?sourceAcId=ac-device-1", token)
            assert status == 200 and ac1_manifests["total"] == 1 and ac1_manifests["items"][0]["observationCount"] == 1
            status, _ = request(base, "/api/system/wireless/collection-manifests/not-visible", token)
            assert status == 404
            status, groups = request(base, "/api/system/wireless/ac-domains/domain-ac1-ac2/ap-groups", token)
            assert status == 200 and groups["total"] == 2, (status, groups)
            for group in groups["items"]:
                # The fixture, like current H3C facts, supplies names without a vendor group key.
                assert group["groupScope"] == "AC"
                assert group["groupIdentityStatus"] == "CANDIDATE"
                assert group["sourceGroupKey"] is None and group["groupIdentityKey"] is None
                assert group["membershipRevision"] and group["sourceObservationIds"]
                assert group["sourceAcDeviceIds"] == [group["sourceAcDeviceId"]]
            group_id = groups["items"][0]["id"]
            status, group_detail = request(base, f"/api/system/wireless/ap-groups/{group_id}", token)
            assert status == 200 and group_detail["group"]["id"] == group_id
            assert group_detail["provenance"]["sourceAcDeviceId"] == group_detail["group"]["sourceAcDeviceId"]
            assert group_detail["provenance"]["groupIdentityKey"] is None
            assert group_detail["provenance"]["groupIdentityStatus"] == "CANDIDATE"
            expected_observations = set(group_detail["group"]["sourceObservationIds"])
            assert {item["id"] for item in group_detail["observations"]} == expected_observations
            for member in group_detail["members"]:
                assert member["observations"]
                assert {item["id"] for item in member["observations"]} <= expected_observations
                assert {item["sourceAcDeviceId"] for item in member["observations"]} == {group_detail["group"]["sourceAcDeviceId"]}
            assert group_detail["total"] == len(group_detail["members"])
            status, group_members = request(base, f"/api/system/wireless/ap-groups/{group_id}/members", token)
            assert status == 200 and group_members["total"] == len(group_detail["members"])
            for member in group_members["items"]:
                assert {item["id"] for item in member["observations"]} <= expected_observations
            source_ac = group_detail["group"]["sourceAcDeviceId"]
            other_ac = next(item for item in domain["memberDeviceIds"] if item != source_ac)
            status, scoped_detail = request(base, f"/api/system/wireless/ap-groups/{group_id}?sourceAcId={source_ac}", token)
            assert status == 200 and scoped_detail["provenance"]["sourceAcDeviceIds"] == [source_ac]
            status, _ = request(base, f"/api/system/wireless/ap-groups/{group_id}?sourceAcId={other_ac}", token)
            assert status == 404
            status, _ = request(base, f"/api/system/wireless/ap-groups/{group_id}/members?sourceAcId={other_ac}", token)
            assert status == 404
            status, ac1_groups = request(base, "/api/system/wireless/ac-domains/domain-ac1-ac2/ap-groups?sourceAcId=ac-device-1", token)
            assert status == 200 and ac1_groups["total"] == 1
            assert ac1_groups["items"][0]["sourceAcDeviceIds"] == ["ac-device-1"]
            status, _ = request(base, "/api/system/wireless/ap-groups/not-visible", token)
            assert status == 404
            resource_id = resource["id"]
            status, filtered = request(base, "/api/system/wireless/ap-resources?sourceAcId=missing-ac&page=1&pageSize=10", token)
            assert status == 200 and filtered["total"] == 0 and filtered["items"] == [], (status, filtered)
            status, _ = request(base, "/api/system/wireless/ap-resources?mac=not-a-mac", token)
            assert status == 400
            status, _ = request(base, f"/api/system/wireless/ap-resources/{resource_id}/runtime-state?sourceAcId=missing-ac", token)
            assert status == 404
            status, hidden_observations = request(base, f"/api/system/wireless/ap-resources/{resource_id}/observations?sourceAcId=missing-ac", token)
            assert status == 200 and hidden_observations["total"] == 0 and hidden_observations["items"] == [], (status, hidden_observations)
            status, _ = request(base, "/api/system/wireless/ap-resources/not-visible", token)
            assert status == 404
            status, _ = request(base, "/api/system/wireless/ap-resources")
            assert status == 401
            verify_explicit_group_evidence(base, token, fixture, fixture_document)
            print("R3_DUAL_AC_API_FIXTURE=PASS")
            print("R3_DUAL_AC_IDENTITY_DEDUP=PASS")
            print("R3_DUAL_AC_OBSERVATION_PROVENANCE=PASS")
            print("R3_DUAL_AC_TOPOLOGY_EVIDENCE=PASS")
            print("R3_DUAL_AC_ASSOCIATION_PROVENANCE=PASS")
            print("R3_DUAL_AC_COLLECTION_MANIFEST=PASS")
            print("R3_DUAL_AC_COLLECTION_MANIFEST_SCOPE=PASS")
            print("R3_DUAL_AC_GROUP_SCOPE=PASS")
            print("R3_DUAL_AC_GROUP_DETAIL_PROVENANCE=PASS")
            print("R3_AP_GROUP_NAME_ONLY_CANDIDATE=PASS")
            print("R3_AP_GROUP_MEMBER_OBSERVATION_SCOPE=PASS")
            print("R3_WIRELESS_FILTER_SCOPE=PASS")
            print("R3_WIRELESS_AUTH_GUARD=PASS")
        finally:
            if service.poll() is None:
                if os.name == "nt":
                    service.send_signal(signal.CTRL_BREAK_EVENT)
                else:
                    service.terminate()
                try:
                    service.wait(timeout=20)
                except subprocess.TimeoutExpired:
                    service.kill(); service.wait(timeout=10)
            if service.returncode and service.stdout:
                print(service.stdout.read()[-4000:])


if __name__ == "__main__":
    main()
