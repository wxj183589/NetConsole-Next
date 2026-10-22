# Third-party notices

The offline terminal bundle includes these npm packages, all pinned in
`package-lock.json`:

| Package | Version | License | Purpose |
| --- | --- | --- | --- |
| `@xterm/xterm` | 5.5.0 | MIT | Terminal emulator |
| `@xterm/addon-fit` | 0.10.0 | MIT | Terminal resize fitting |
| `@xterm/addon-search` | 0.15.0 | MIT | Terminal search |
| `esbuild` | 0.25.12 | MIT | Build-time bundling only |
| `playwright` | 1.64.1 | Apache-2.0 | Browser E2E test dependency only |

The xterm runtime and addons are distributed under their respective MIT
licenses. The generated `wwwroot/terminal.bundle.js` retains esbuild legal
comments at the end of the bundle. The package metadata and lockfile are the
version and integrity authority; review the package licenses before changing
versions.
