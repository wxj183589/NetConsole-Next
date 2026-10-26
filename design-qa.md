# P2B5 Product Design QA

final result: passed

## Comparison target

- Source visual truth: `D:\下载\ChatGPT 图像 2026年10月24日 02_08_27-1.png` (1680×943) and `D:\下载\NetConsole Next 运行总览界面合集.png` (1536×1024).
- Implementation: `web/artifacts/p2b5-network-planning-browser/planning-light-1680x943.png` (1680×943 CSS px, deviceScaleFactor 1).
- Additional implementation evidence: light 1920×1080, dark 1440×900, dark 1366×768, domains/VLAN/diff/operations and Admin no-scope state.
- State: isolated v14 fixture, Superadmin, Network Planning route, device `p2b4-device-a`, light theme for the primary comparison.

The source images are the established NetConsole Next visual language; their page content is device/terminal/dashboard rather than the new P2B5 route. The comparison therefore checks shared shell, density, tokens, typography, tables, state colors and responsive behavior, while treating the network-planning information architecture as an intentional P2B5 extension.

## Findings

- No actionable P0/P1/P2 differences found in the captured P2B5 states.
- Shared dark navigation shell, blue active state, light canvas, compact table headers, rounded cards, status pills and primary action treatment are consistent with the reference system.
- The planning screen keeps explicit source, revision, scope, history and authorization states visible rather than imitating the reference with decorative or fake rows.
- P3 follow-up: with a larger real dataset, review table density and pagination at 1366×768; the isolated fixture intentionally contains only a few rows.

## Required fidelity surfaces

- Fonts/typography: existing product font tokens and monospace values for IP/source identity; hierarchy and compact labels remain readable at all captured viewports.
- Spacing/layout rhythm: shell, toolbar, tabs, two-column cards and table rows remain within the viewport at 1920×1080, 1680×943, 1440×900 and 1366×768.
- Colors/tokens: existing light/dark variables, blue active/primary, green success, warning/conflict pills and neutral canvas are reused.
- Image/asset fidelity: no new decorative image is required by the planning screen; existing product icons/component icon library is used, with no placeholder art.
- Copy/content: all visible business data in the evidence is isolated fixture data; unavailable gates explicitly say that the UI will not fabricate data.

## Interaction and diagnostics

The browser evidence tests navigation, filters/device selection, address create/update/history, VLAN create, review, import Preview/Dry-Run/Confirm, diff, operation history, theme/viewport changes and Admin denied-by-default behavior. `browser-evidence.json` reports zero console errors, page errors, failed requests and unexpected HTTP responses; expected auth/scope 401/403 responses are recorded separately.

## Comparison history

The first browser pass found a stale published asset copy, v14 being rejected by the v13 reader, a locator ambiguity, and numeric form normalization. These were fixed by running the service from its published content root, accepting v13-compatible reads on v14 while preserving strict migration validation, tightening locators, and normalizing numeric form values. The final same-size 1680×943 capture and responsive captures passed without an actionable P0/P1/P2 finding.
