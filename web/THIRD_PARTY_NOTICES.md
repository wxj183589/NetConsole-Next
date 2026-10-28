# Third-party notices

The Web PR-1 UI uses the following packages. Versions are locked in `package-lock.json`.

- Vue 3, Vue Router, Pinia, Vite, Vitest and Vue Test Utils: MIT.
- Element Plus and `@element-plus/icons-vue`: MIT.
- ECharts: Apache-2.0.
- `@xterm/xterm`, `@xterm/addon-fit`, `@xterm/addon-search`: MIT.
- Playwright and esbuild: Apache-2.0 / MIT components as distributed by their packages.

Art Design Pro was inspected as a visual reference only. No Art Design Pro source, route, store, or asset is included in this repository. Old NetConsole source was inspected as a functional reference only and is not bundled into the Next Web build.

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
