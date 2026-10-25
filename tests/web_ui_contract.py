"""Static boundary checks for the NetConsole Next Web PR-1 slice."""
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
WEB = ROOT / "web"
SERVICE = ROOT / "src" / "NetConsole.Next.Service"


def test_next_owns_the_frontend_scaffold():
    package = (WEB / "package.json").read_text(encoding="utf-8")
    assert '"vue"' in package and '"element-plus"' in package and '"pinia"' in package
    assert (WEB / "src" / "components" / "AppShell.vue").is_file()
    assert (WEB / "src" / "views" / "TerminalView.vue").is_file()
    assert "art-design-pro" not in package.lower()


def test_unsupported_pages_are_explicitly_disabled():
    app = (WEB / "src" / "components" / "AppShell.vue").read_text(encoding="utf-8")
    disabled = (WEB / "src" / "views" / "DisabledView.vue").read_text(encoding="utf-8")
    assert "开发中" in app
    assert "不提供模拟数据" in disabled


def test_auth_does_not_expose_local_token_to_vue():
    client = (WEB / "src" / "api" / "client.ts").read_text(encoding="utf-8")
    assert "credentials: 'include'" in client
    assert "Authorization" not in client
    service = (SERVICE / "Program.cs").read_text(encoding="utf-8")
    assert "netconsole_session" in service and "X-NC-CSRF" in service
    assert "NETCONSOLE_NEXT_WEB_LAN" in (SERVICE / "RuntimeSettings.cs").read_text(encoding="utf-8")


if __name__ == "__main__":
    for check in (test_next_owns_the_frontend_scaffold,
                  test_unsupported_pages_are_explicitly_disabled,
                  test_auth_does_not_expose_local_token_to_vue):
        check()
    print("web UI contract: PASS (3 checks)")

