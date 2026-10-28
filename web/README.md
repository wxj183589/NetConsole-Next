# NetConsole Next terminal web assets

The terminal UI uses the MIT-licensed packages `@xterm/xterm` 5.5.0,
`@xterm/addon-fit` 0.10.0, and `@xterm/addon-search` 0.15.0. Exact versions are locked in
`package-lock.json`. `esbuild` is used only at development/CI build time.

Run:

```powershell
npm ci
npm run build
```

The build writes `terminal.bundle.js` and `xterm.css` into the Service
`wwwroot` directory. They are then copied by `dotnet publish`; the service
does not contact a CDN at runtime. The generated files are checked in so a
clean checkout can publish the offline UI without requiring Node.js; CI still
rebuilds them and verifies the generated output.

License notices are recorded in `THIRD_PARTY_NOTICES.md`.
