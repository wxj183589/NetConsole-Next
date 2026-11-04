#!/usr/bin/env python3
"""R2 device-management Site claims contract against an isolated local service."""
import json
import os
import shutil
import sqlite3
import subprocess
import tempfile
import time
import urllib.error
import urllib.request
from http.cookiejar import CookieJar
from pathlib import Path

from p2b3_read_api_e2e import free_port, start_service, stop_service, migrate_v13
from p2b5_site_scope_e2e import migrate_site_scope


class Client:
    def __init__(self, base):
        self.base = base
        self.jar = CookieJar()
        self.opener = urllib.request.build_opener(urllib.request.HTTPCookieProcessor(self.jar))
        self.csrf = ""

    def request(self, path, method="GET", payload=None, csrf=True):
        data = None if payload is None else json.dumps(payload).encode()
        headers = {"Content-Type": "application/json"} if data is not None else {}
        if method not in ("GET", "HEAD"):
            headers["Origin"] = self.base
            if csrf and self.csrf:
                headers["X-NC-CSRF"] = self.csrf
        request = urllib.request.Request(self.base + path, data=data, method=method, headers=headers)
        try:
            with self.opener.open(request, timeout=12) as response:
                body = response.read()
                return response.status, json.loads(body) if body else None
        except urllib.error.HTTPError as error:
            body = error.read().decode(errors="replace")
            try:
                body = json.loads(body)
            except json.JSONDecodeError:
                pass
            return error.code, body

    def login(self, username, password="admin"):
        status, result = self.request("/api/auth/login", "POST", {"username": username, "password": password}, csrf=False)
        assert status == 200, (status, result)
        self.csrf = result["csrfToken"]


def assert_status(result, status):
    assert result[0] == status, result
    return result[1]


def seed_fixture(root):
    now = int(time.time() * 1000)
    with sqlite3.connect(root / "catalog.db") as db:
        db.execute("PRAGMA foreign_keys=ON")
        for site_id, name in (("site-a", "Site A"), ("site-b", "Site B"), ("site-c", "Site C")):
            db.execute("INSERT INTO sites(id,name) VALUES(?,?)", (site_id, name))
        for group_id, site_id, name in (("group-a", "site-a", "Group A"), ("group-b", "site-b", "Group B")):
            db.execute(
                "INSERT INTO device_groups(id,site_id,name,description,created_ms,updated_ms) VALUES(?,?,?,?,?,?)",
                (group_id, site_id, name, "claims fixture", now, now),
            )
        for row in (
            ("device-a", "Device A", "192.0.2.11", "site-a", "group-a"),
            ("device-b", "Device B", "192.0.2.12", "site-b", "group-b"),
            ("device-c", "Device C", "192.0.2.13", "site-c", None),
        ):
            db.execute(
                """INSERT INTO device_inventory
                   (id,name,management_ip,vendor,model,device_type,site_id,group_id,description,enabled,created_ms,updated_ms)
                   VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
                (*row[:3], "H3C", "claims", "NETWORK_DEVICE", row[3], row[4], "claims fixture", 1, now, now),
            )

        admin_salt, admin_hash, iterations = db.execute(
            "SELECT password_salt,password_hash,password_iterations FROM web_users WHERE username='admin'"
        ).fetchone()
        users = {}
        for username in ("USER_A", "USER_B", "USER_AB", "USER_NONE"):
            user_id = "claims-" + username.lower()
            users[username] = user_id
            db.execute(
                """INSERT INTO web_users
                   (id,username,role,password_salt,password_hash,password_iterations,created_ms,updated_ms)
                   VALUES(?,?,?,?,?,?,?,?)""",
                (user_id, username, "ADMIN", admin_salt, admin_hash, iterations, now, now),
            )
        claims = {
            "USER_A": (("site-a", "claim-a", 1),),
            "USER_B": (("site-b", "claim-b", 1),),
            "USER_AB": (("site-a", "claim-ab-a", 1), ("site-b", "claim-ab-b", 1)),
            "USER_NONE": (),
        }
        for username, rows in claims.items():
            for site_id, source, revision in rows:
                db.execute(
                    """INSERT INTO web_user_site_memberships
                       (id,user_id,site_reference,claim_state,source_kind,source_reference,revision,lifecycle,
                        valid_from_ms,valid_to_ms,created_ms,updated_ms)
                       VALUES(?,?,?,?,?,?,?,?,?,?,?,?)""",
                    ("membership-" + source, users[username], site_id, "ACTIVE", "DIRECT", source, revision,
                     "ACTIVE", now - 1000, now + 600000, now, now),
                )
        db.commit()


def main():
    repo = Path(__file__).resolve().parents[1]
    service_dll = repo / "src" / "NetConsole.Next.Service" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.dll"
    test_dll = repo / "tests" / "NetConsole.Next.Service.Tests" / "bin" / "Release" / "net10.0" / "NetConsole.Next.Service.Tests.dll"
    if not service_dll.is_file() or not test_dll.is_file():
        raise RuntimeError("Run Release builds for service and tests first")

    temp_root = tempfile.mkdtemp(prefix="netconsole-next-r2-site-claims-")
    root = Path(temp_root) / "data"
    root.mkdir(parents=True, exist_ok=False)
    service = None
    passed = 0

    def check(condition, label):
        nonlocal passed
        assert condition, label
        passed += 1
        print(label + "=PASS")

    try:
        port = free_port()
        service, base = start_service(repo, service_dll, root, port, feature_enabled=False)
        stop_service(service)
        service = None
        migrate_v13(repo, test_dll, root)
        migrate_site_scope(repo, test_dll, root)
        seed_fixture(root)
        service, base = start_service(repo, service_dll, root, free_port(), feature_enabled=False)

        user_a = Client(base); user_a.login("USER_A")
        sites = assert_status(user_a.request("/api/system/device-sites"), 200)
        check([item["id"] for item in sites] == ["site-a"], "USER_A_AUTHORIZED_SITES")
        inventory = assert_status(user_a.request("/api/system/devices?page=1&pageSize=100"), 200)
        check(inventory["total"] == 1 and [item["id"] for item in inventory["items"]] == ["device-a"], "USER_A_INVENTORY_FILTERED")
        check(assert_status(user_a.request("/api/system/devices?siteId=site-b"), 403)["error"] == "P2B_SITE_SCOPE_DENIED", "USER_A_SITE_PARAMETER_DENIED")
        check(assert_status(user_a.request("/api/system/devices?search=device-b"), 200)["total"] == 0, "USER_A_SEARCH_NO_LEAK")
        check(user_a.request("/api/system/devices/device-b")[0] == 404, "USER_A_DETAIL_NO_LEAK")
        groups = assert_status(user_a.request("/api/system/device-groups"), 200)
        check([item["id"] for item in groups] == ["group-a"], "USER_A_GROUPS_FILTERED")
        check(assert_status(user_a.request("/api/system/device-groups?siteId=site-b"), 403)["error"] == "P2B_SITE_SCOPE_DENIED", "USER_A_GROUP_SITE_DENIED")
        check(user_a.request("/api/system/devices", "POST", {
            "name": "denied", "managementIp": "192.0.2.40", "siteId": "site-b", "enabled": True,
        })[0] == 403, "USER_A_CREATE_ROLE_DENIED")
        check(user_a.request("/api/system/devices/device-b", "PUT", {
            "id": "device-b", "name": "tamper", "managementIp": "192.0.2.12", "siteId": "site-b", "enabled": True,
        })[0] == 403, "USER_A_EDIT_ROLE_DENIED")

        user_b = Client(base); user_b.login("USER_B")
        check([item["id"] for item in assert_status(user_b.request("/api/system/device-sites"), 200)] == ["site-b"], "USER_B_AUTHORIZED_SITES")
        check(assert_status(user_b.request("/api/system/devices?pageSize=100"), 200)["items"][0]["id"] == "device-b", "USER_B_INVENTORY_FILTERED")

        user_ab = Client(base); user_ab.login("USER_AB")
        ab_sites = assert_status(user_ab.request("/api/system/device-sites"), 200)
        check([item["id"] for item in ab_sites] == ["site-a", "site-b"], "USER_AB_AUTHORIZED_SITES")
        ab_inventory = assert_status(user_ab.request("/api/system/devices?page=1&pageSize=100"), 200)
        check(ab_inventory["total"] == 2 and {item["id"] for item in ab_inventory["items"]} == {"device-a", "device-b"}, "USER_AB_AGGREGATE_FILTERED")
        check(assert_status(user_ab.request("/api/system/devices?siteId=site-c"), 403)["error"] == "P2B_SITE_SCOPE_DENIED", "USER_AB_SITE_C_DENIED")
        check(assert_status(user_ab.request("/api/system/device-groups"), 200)[0]["siteId"] == "site-a" and len(assert_status(user_ab.request("/api/system/device-groups"), 200)) == 2, "USER_AB_GROUP_AGGREGATE_FILTERED")

        user_none = Client(base); user_none.login("USER_NONE")
        denied = assert_status(user_none.request("/api/system/device-sites"), 403)
        check(denied["error"] == "P2B_SITE_SCOPE_DENIED", "USER_NONE_FAIL_CLOSED")
        check(assert_status(user_none.request("/api/system/devices/device-a"), 403)["error"] == "P2B_SITE_SCOPE_DENIED", "USER_NONE_DETAIL_DENIED")
        check(user_none.request("/api/system/devices/import/preview", "POST", {"csv": "name,managementIp\nX,192.0.2.41"})[0] == 403, "USER_NONE_CSV_DENIED")

        superadmin = Client(base); superadmin.login("superadmin", "superadmin")
        all_sites = assert_status(superadmin.request("/api/system/device-sites"), 200)
        check({item["id"] for item in all_sites} == {"local", "site-a", "site-b", "site-c"}, "SUPERADMIN_GLOBAL_SITES")
        check(assert_status(superadmin.request("/api/system/devices?pageSize=100"), 200)["total"] == 3, "SUPERADMIN_GLOBAL_INVENTORY")
        check(assert_status(superadmin.request("/api/system/devices/device-b"), 200)["item"]["id"] == "device-b", "SUPERADMIN_CROSS_SITE_DETAIL")
        check(superadmin.request("/api/system/devices", "POST", {"name": "csrf", "managementIp": "192.0.2.44", "siteId": "site-c", "enabled": True}, csrf=False)[0] == 403, "CSRF_WRITE_DENIED")
        created = assert_status(superadmin.request("/api/system/devices", "POST", {"id": "claims-created", "name": "Created", "managementIp": "192.0.2.44", "siteId": "site-c", "enabled": True}), 201)
        check(created["item"]["id"] == "claims-created", "SUPERADMIN_CREATE_AUDITED")
        updated = assert_status(superadmin.request("/api/system/devices/claims-created", "PUT", {"id": "claims-created", "name": "Created Renamed", "managementIp": "192.0.2.45", "siteId": "site-c", "enabled": True}), 200)
        check(updated["item"]["id"] == "claims-created" and updated["item"]["managementIp"] == "192.0.2.45", "STABLE_ID_EDIT")
        check(any(event["action"] == "DEVICE_UPDATE" for event in updated["auditEvents"]), "AUDIT_VISIBLE")
        response_text = json.dumps(updated).lower()
        check("password" not in response_text and "secret" not in response_text, "CREDENTIALS_NOT_EXPOSED")
        fit = assert_status(superadmin.request("/api/system/devices", "POST", {"name": "FIT", "managementIp": "192.0.2.46", "siteId": "site-c", "deviceType": "FIT_AP", "enabled": True}), 400)
        check("FIT-AP" in fit["error"], "FIT_AP_CREATE_BLOCKED")
        csv = "name,managementIp,siteId,deviceType,enabled\nCSV C,192.0.2.47,site-c,NETWORK_DEVICE,true"
        preview = assert_status(superadmin.request("/api/system/devices/import/preview", "POST", {"csv": csv}), 200)
        check(preview["conflictCount"] == 0 and preview["createCount"] == 1, "CSV_PREVIEW_AUTHORIZED")
        commit = assert_status(superadmin.request("/api/system/devices/import/commit", "POST", {"previewId": preview["previewId"], "confirm": True}), 200)
        check(commit["created"] == 1, "CSV_COMMIT_ATOMIC")
        batch = assert_status(superadmin.request("/api/system/devices/batch", "POST", {"kind": "BATCH_DISABLE", "idempotencyKey": "claims-batch", "deviceIds": ["claims-created"]}), 200)
        check(batch["state"] == "PENDING_CONFIRM", "BATCH_PREVIEW_AUTHORIZED")
        batch_done = assert_status(superadmin.request("/api/system/devices/batch/" + batch["id"] + "/confirm", "POST", {}), 200)
        check(batch_done["state"] == "SUCCEEDED", "BATCH_COMMIT_AUTHORIZED")
        print(f"R2_SITE_CLAIMS_TESTS={passed}/{passed} PASS")
    finally:
        if service is not None:
            stop_service(service)
        shutil.rmtree(temp_root, ignore_errors=True)


if __name__ == "__main__":
    main()
