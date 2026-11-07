"""Authorized SW simulator: complete backup, comparison, preview and native gate.

Uses existing server-side credentials. Persists metadata only, never configuration.
"""
import json
import urllib.request
from datetime import datetime, timezone
from pathlib import Path
import sys

from r2_site_claims_e2e import Client
from r69_config_writer_probe import canonical_hash

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools"))
from workspace_paths import workspace_path


class SlowClient(Client):
    def request(self, path, method="GET", payload=None, csrf=True):
        data = None if payload is None else json.dumps(payload).encode()
        headers = {"Content-Type": "application/json"}
        if method != "GET":
            headers.update({"Origin": self.base, "X-NC-CSRF": self.csrf})
        with self.opener.open(urllib.request.Request(self.base + path, data=data, method=method,
                                                   headers=headers), timeout=180) as response:
            return response.status, json.load(response)


def main():
    client = SlowClient("http://127.0.0.1:18771")
    client.login("superadmin", "superadmin")
    profile = "c18dcb225dda4d059ce9799a71a28b2b"
    root = workspace_path("test-data", "simulator-live-20261105")
    before = client.request("/api/system/simulator-config/backups/" + profile, "POST")[1]
    assert before["integrity"] == "COMPLETE_HASH_VERIFIED" and before["restorable"], before["integrity"]
    before_hash = canonical_hash(root, profile, before)
    compare = client.request(f'/api/system/simulator-config/compare?beforeId={before["id"]}&afterId={before["id"]}')[1]
    assert not compare["originalConfigurationChanged"] and not compare["contexts"]
    preview = client.request(f'/api/system/simulator-config/backups/{before["id"]}/restore-preview', "POST")[1]
    assert preview["compatible"] and not preview["writeAllowed"]
    assert "FULL_DEVICE_RESTORE_NOT_IMPLEMENTED" in preview["blockers"]
    operation = client.request("/api/system/simulator-config/checkpoints/verify", "POST", {
        "profileId": profile, "expectedBackupId": before["id"],
        "interfaceName": "GigabitEthernet1/0/48", "description": "R70 checkpoint verification"})[1]
    after = client.request("/api/system/simulator-config/backups/" + profile, "POST")[1]
    after_hash = canonical_hash(root, profile, after)
    report = {"evidence": "SIMULATOR", "capturedUtc": datetime.now(timezone.utc).isoformat(),
              "before": before, "after": after, "canonicalBefore": before_hash, "canonicalAfter": after_hash,
              "selfComparisonUnchanged": True, "previewCompatible": preview["compatible"],
              "previewWriteAllowed": preview["writeAllowed"], "previewBlockers": preview["blockers"],
              "operation": operation, "configurationUnchanged": before_hash == after_hash}
    path = workspace_path("reports", "r70-configuration-http-" + datetime.now().strftime("%Y%m%d-%H%M%S") + ".json")
    path.write_text(json.dumps(report, indent=2), encoding="utf-8")
    assert before_hash == after_hash, "Running configuration changed"
    assert operation["error"] == "NATIVE_CHECKPOINT_NOT_CREATED", operation["error"]
    assert operation["beforeBackupId"]
    assert all(not operation[key] for key in ("configWriteSucceeded", "configReadbackVerified", "serviceVerified", "rollbackVerified"))
    print(json.dumps({"status": "PASS_NATIVE_RESTORE_BLOCKED", "evidence": str(path),
                      "operationId": operation["id"], "configurationUnchanged": True}))


if __name__ == "__main__":
    main()
