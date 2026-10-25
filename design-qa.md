# Design QA

## Result

`passed` for the reference UI and isolated real-browser acceptance.

## Evidence reviewed

- `web/artifacts/ui-acceptance/overview-light.png`
- `web/artifacts/ui-acceptance/overview-dark.png`
- `web/artifacts/ui-acceptance/devices-light.png`
- `web/artifacts/ui-acceptance/terminal-dark.png`
- `web/artifacts/ui-acceptance/tasks.png`
- `web/artifacts/ui-acceptance/tools.png`
- `web/artifacts/ui-acceptance/files.png`
- `web/artifacts/ui-acceptance/settings.png`
- `web/artifacts/ui-acceptance/browser-evidence.json`

## Checks

- Navigation and header are consistent across the captured routes.
- Light and dark themes preserve contrast and semantic status colors.
- Dashboard has the reference information hierarchy while keeping true empty states.
- Device management uses compact filters/table/pagination and does not render the unavailable 782 records.
- Terminal page has the requested three-column workstation and bottom history area, with a dark terminal surface.
- Captured pages show no visible `undefined`, `null`, `[object Object]`, or dangling address colon.
- Real Chrome run reported zero console errors, page errors, failed requests, bad responses, and post-login auth/missing responses.

## Known differences

The reference images contain populated device, alert, RSSI and terminal-output examples. The isolated preview Catalog contains zero H3C profiles and terminal collection is disabled, so the acceptance captures truthful empty states. This is a data-availability difference, not a visual fixture substituted for runtime data.
