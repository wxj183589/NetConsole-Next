"""Static contract checks for the first G2.3 Web terminal slice."""

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
INDEX = ROOT / "src" / "NetConsole.Next.Service" / "wwwroot" / "index.html"
SCRIPT = ROOT / "src" / "NetConsole.Next.Service" / "wwwroot" / "terminal.js"
PROJECT = ROOT / "src" / "NetConsole.Next.Service" / "NetConsole.Next.Service.csproj"
PROGRAM = ROOT / "src" / "NetConsole.Next.Service" / "Program.cs"
WEB_PACKAGE = ROOT / "web" / "package.json"
WEB_LOCK = ROOT / "web" / "package-lock.json"


def test_web_terminal_assets_are_published():
    assert INDEX.is_file() and SCRIPT.is_file()
    project = PROJECT.read_text(encoding="utf-8")
    assert "wwwroot\\**\\*" in project
    assert "CopyToPublishDirectory=\"PreserveNewest\"" in project
    html = INDEX.read_text(encoding="utf-8")
    assert "xterm.css" in html and "terminal.bundle.js" in html
    assert "cdn.jsdelivr.net" not in html
    package = WEB_PACKAGE.read_text(encoding="utf-8")
    assert '"@xterm/xterm": "5.5.0"' in package
    assert '"@xterm/addon-fit": "0.10.0"' in package
    assert '"@xterm/addon-search": "0.15.0"' in package
    assert WEB_LOCK.is_file()
    assert (ROOT / "src" / "NetConsole.Next.Service" / "wwwroot" / "terminal.bundle.js").is_file()
    assert (ROOT / "src" / "NetConsole.Next.Service" / "wwwroot" / "xterm.css").is_file()
    assert 'id="device-list"' in html and 'id="terminals"' in html


def test_web_terminal_uses_authorized_session_flow():
    script = SCRIPT.read_text(encoding="utf-8")
    program = PROGRAM.read_text(encoding="utf-8")
    assert "/api/system/terminal/devices" in script
    assert "/api/system/terminal/sessions" in script
    assert "/ticket" in script and "/stream?ticket=" in script
    assert 'mode:"RESTRICTED_READONLY"' in script
    assert 'from "@xterm/xterm"' in script
    assert 'from "@xterm/addon-fit"' in script
    assert 'from "@xterm/addon-search"' in script
    assert "new TerminalDeviceView" in program
    assert "UseStaticFiles" in program


def test_web_terminal_does_not_send_raw_key_bytes():
    script = SCRIPT.read_text(encoding="utf-8")
    assert 'type:"input",text:command' in script
    assert 'type:"control",signal:"CTRL_C"' in script
    assert 'socket.send(data)' not in script


if __name__ == "__main__":
    for check in (test_web_terminal_assets_are_published,
                  test_web_terminal_uses_authorized_session_flow,
                  test_web_terminal_does_not_send_raw_key_bytes):
        check()
    print("terminal UI contract: PASS (3 checks)")
