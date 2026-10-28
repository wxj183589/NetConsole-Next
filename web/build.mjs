import { build } from "esbuild";
import { copyFileSync, mkdirSync } from "node:fs";
import { dirname, join, resolve } from "node:path";

const webRoot = resolve(import.meta.dirname);
const serviceRoot = resolve(webRoot, "..", "src", "NetConsole.Next.Service", "wwwroot");
const bundle = join(serviceRoot, "terminal.bundle.js");
const css = join(serviceRoot, "xterm.css");

mkdirSync(dirname(bundle), { recursive: true });
await build({
  entryPoints: [join(serviceRoot, "terminal.js")],
  bundle: true,
  minify: true,
  format: "iife",
  platform: "browser",
  target: ["es2020"],
  nodePaths: [join(webRoot, "node_modules")],
  outfile: bundle,
  legalComments: "eof"
});
copyFileSync(join(webRoot, "node_modules", "@xterm", "xterm", "css", "xterm.css"), css);
console.log(`Built ${bundle} and ${css}`);
