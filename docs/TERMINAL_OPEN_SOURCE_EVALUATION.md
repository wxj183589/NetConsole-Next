# G2.3 Terminal Open-Source Evaluation

Status: implementation reference for the first Terminal Core slice. No source code is copied from the projects below.

| Project | License | Relevant reference | Decision |
| --- | --- | --- | --- |
| [Tabby](https://github.com/Eugeny/tabby) | MIT | Multi-tab terminal UX, SSH/Telnet/serial plugins, reconnect and split views | Product interaction reference only. Its Electron/plugin runtime is not added to the headless Service. |
| [xterm.js](https://github.com/xtermjs/xterm.js) | MIT | VT/ANSI rendering, input handling, search, selection and Unicode width behavior | Planned Web UI dependency for PR B. The Service transports bytes and does not reimplement terminal rendering. |
| [Apache Guacamole](https://github.com/apache/guacamole-client) / [server](https://github.com/apache/guacamole-server) | Apache-2.0 | Protocol gateway boundary, recording and replay concepts | Architecture comparison only. Guacamole introduces a larger gateway/runtime and is not required for the local modular monolith. |
| [ttyd](https://github.com/tsl0922/ttyd) | MIT | WebSocket stream framing, bounded terminal bridge behavior | Protocol behavior reference only. The product gateway keeps device authorization and command policy in NetConsole-Next. |
| [SSH.NET](https://github.com/sshnet/SSH.NET) | MIT | SSH authentication, ShellStream, PTY and window resize | Existing approved dependency. Interactive SSH reuses the current algorithm allowlist and Host Key verification. |
| [.NET System.IO.Ports](https://github.com/dotnet/runtime/tree/main/src/libraries/System.IO.Ports) | MIT | Windows serial port enumeration and stream access | Standard runtime dependency for the controlled local COM adapter. Physical-device verification remains separate. |

## Security and compatibility decisions

- The Service remains a headless .NET modular monolith. No Electron, Java, Guacamole server or remote Agent Controller is introduced.
- SSH Host Key validation and the existing algorithm policy are reused. A Host Key failure never falls back to Telnet.
- Telnet is explicitly plaintext and only uses a registered device profile. Credentials are read from the existing provider and never enter terminal recording.
- The first terminal mode is server-enforced `RESTRICTED_READONLY`. It accepts only the existing read-only command allowlist and rejects composition characters, control characters and raw-byte input.
- Terminal output uses a bounded WebSocket queue and a bounded append-only recording. A recording write failure is visible and cannot be represented as a complete audit.
- WebSocket authorization uses a one-time short-lived ticket issued by an authenticated loopback HTTP request. The ticket is not a long-lived credential.

## Alternatives

An xterm.js Web UI is preferred for PR B because it is purpose-built for VT rendering and has a small browser integration surface. Guacamole is preferable only if future scope requires many remote protocols or a separate protocol daemon. Tabby is a useful UX benchmark but its application/plugin architecture is broader than this product's headless-service boundary. A browser Web Serial API is not sufficient for the Windows Service COM requirement and is therefore not used as the only serial path.
