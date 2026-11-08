"""Actual HTTP Long Ping control against a minimal loopback-only fixture.

Requires the exclusive Service mutex window. No worker or device is used.
This bounded lifecycle test is not long-duration or wireless acceptance.
"""
import argparse
from contextlib import closing
import json
from pathlib import Path
import sqlite3
import sys
import time
import uuid

from r72_feature_http_e2e import start, stop
from web_auth_e2e import Client

sys.path.insert(0, str(Path(__file__).resolve().parents[1]))
from tools.workspace_paths import workspace_path

RUNS = "/api/system/diagnostics/runs"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--service-dll", type=Path, required=True)
    args = parser.parse_args()
    dll = args.service_dll.resolve(strict=True)
    assert dll.is_relative_to(workspace_path("build").resolve())
    run_id = "r76-long-ping-http-" + uuid.uuid4().hex[:12]
    output = workspace_path("reports", run_id)
    output.mkdir()
    root = workspace_path("test-data", run_id)
    report = dict(status="RUNNING", evidenceLevel="SERVICE_E2E", root=str(root),
                  serviceDll=str(dll), target="127.0.0.1", protocol="ICMP",
                  deviceConnections=0, configurationWrites=0, checks=[])
    process = None

    def save():
        (output / "result.json").write_text(json.dumps(report, indent=2), encoding="utf-8")

    def expect(client, path, method="GET", payload=None, status=200, **kwargs):
        actual, body = client.request(path, method, payload, **kwargs)
        assert actual == status, (path, actual, body)
        return body

    def check(condition, label):
        assert condition, label
        report["checks"].append(label)
        save()
        print(label + "=PASS", flush=True)

    def wait(client, path, predicate, seconds=15):
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            body = expect(client, path)
            if predicate(body):
                return body
            time.sleep(.1)
        raise AssertionError(("State timeout", path, body))

    def launch():
        nonlocal process
        process, base = start(dll, root, output)
        owner, reader = Client(base), Client(base)
        owner.login("superadmin", "superadmin")
        reader.login("admin", "admin")
        return owner, reader

    def create(owner, duration):
        body = expect(owner, RUNS, "POST", dict(deviceIds=["r76-loopback"],
            kinds=["LONG_PING"], siteId="local", timeoutMs=500, intervalMs=1000,
            longPingDurationSeconds=duration, quickPingProtocol="ICMP"), 202)
        path = RUNS + "/" + body["id"]
        return path, wait(owner, path, lambda b: (b["items"][0].get("quickPing") or {}).get("sent", 0) >= 2)

    try:
        launch()
        stop(process)
        process = None
        now = int(time.time() * 1000)
        with closing(sqlite3.connect(root / "catalog.db")) as db:
            db.execute("""INSERT INTO device_inventory(id,name,management_ip,vendor,device_type,
                site_id,enabled,created_ms,updated_ms) VALUES('r76-loopback',
                'Synthetic loopback Long Ping','127.0.0.1','OTHER','SWITCH','local',1,?,?)""", (now, now))
            db.commit()
        owner, reader = launch()
        path, running = create(owner, 30)
        check(running["items"][0]["quickPing"]["received"] >= 2, "ACTUAL_LOOPBACK_ICMP_REPLIES")
        expect(reader, path + "/long-ping/pause", "POST", {}, 403)
        expect(owner, path + "/long-ping/pause", "POST", {}, 403, csrf=False)
        check(True, "READ_ROLE_AND_MISSING_CSRF_CANNOT_PAUSE")
        expect(owner, path + "/long-ping/pause", "POST", {})
        paused = wait(owner, path, lambda b: b["items"][0]["state"] == "PAUSED")
        samples = paused["items"][0]["quickPing"]
        check(paused["pausedChecks"] == 1 and paused["state"] == "RUNNING", "PAUSE_DRAINS_AND_REPORTS_PAUSED_ITEM")
        time.sleep(2.2)
        check(expect(owner, path)["items"][0]["quickPing"] == samples, "PAUSED_INTERVAL_ADDS_NO_SAMPLES_OR_LOSS")
        expect(owner, path + "/long-ping/resume", "POST", {})
        resumed = wait(owner, path, lambda b: b["items"][0]["quickPing"]["sent"] > samples["sent"])
        check(resumed["items"][0]["state"] == "RUNNING" and resumed["pausedChecks"] == 0, "RESUME_CONTINUES_EXISTING_RUN")
        expect(owner, path + "/cancel", "POST", {})
        cancelled = wait(owner, path, lambda b: b["endedMs"] is not None)
        check(cancelled["cancelledChecks"] == 1 and cancelled["items"][0]["quickPing"]["lost"] == 0, "STOP_PRESERVES_SAMPLES_WITHOUT_PHANTOM_LOSS")
        expect(owner, path + "/long-ping/resume", "POST", {}, 404)
        check(True, "TERMINAL_RUN_CANNOT_RESUME")

        window_path, window = create(owner, 10)
        expect(owner, window_path + "/long-ping/pause", "POST", {})
        window_paused = wait(owner, window_path, lambda b: b["items"][0]["state"] == "PAUSED")
        ended = wait(owner, window_path, lambda b: b["endedMs"] is not None)
        check(ended["endedMs"] - ended["startedMs"] <= 15000 and
              ended["items"][0]["quickPing"] == window_paused["items"][0]["quickPing"] and
              ended["items"][0]["outcome"] == "LONG_PING_WINDOW_ENDED",
              "PAUSE_DOES_NOT_EXTEND_OR_REFILL_ORIGINAL_WINDOW")
        for result in (cancelled, ended):
            stats = result["items"][0]["quickPing"]
            latencies = sorted(s["latencyMs"] for s in stats["samples"] if s["status"] == "REPLIED")
            import math
            check(stats["p95Ms"] == latencies[math.ceil(len(latencies) * .95) - 1],
                  "P95_MATCHES_NEAREST_RANK_" + result["id"])
        report["cancelledRun"], report["windowRun"] = cancelled, ended
        stop(process)
        process = None
        owner, _ = launch()
        check(expect(owner, path) == cancelled and expect(owner, window_path) == ended,
              "TERMINAL_HISTORY_SURVIVES_ACTUAL_SERVICE_RESTART")
        history = expect(owner, RUNS + "?limit=10")
        check(len(history) == 2 and all(r["endedMs"] is not None for r in history), "RESTART_DOES_NOT_REPLAY_PING")
        with closing(sqlite3.connect(root / "catalog.db")) as db:
            check(db.execute("PRAGMA integrity_check").fetchone()[0] == "ok", "SQLITE_INTEGRITY_OK")
        report["status"] = "PASS"
    except BaseException as error:
        report["status"], report["error"] = "FAIL", repr(error)
        raise
    finally:
        if process is not None:
            stop(process)
        save()
        print("LONG_PING_HTTP_REPORT=" + str(output / "result.json"), flush=True)


if __name__ == "__main__":
    main()
