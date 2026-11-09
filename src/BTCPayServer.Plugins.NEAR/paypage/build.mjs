// Builds the pay page script into the plugin's embedded resources: one self-contained IIFE for current browsers.
import { build } from "esbuild";
import { statSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const here = dirname(fileURLToPath(import.meta.url));
const outfile = resolve(here, "..", "Resources", "nearpay", "app.js");

// `import { z } from "zod"` (only @meteorwallet/sdk does it) goes to the slim module; zod's own subpaths, which that
// module imports, are left alone. An `alias` entry would rewrite the subpaths too.
const zodSlim = {
  name: "zod-slim",
  setup(plugin) {
    plugin.onResolve({ filter: /^zod$/ }, () => ({ path: resolve(here, "src", "zod-slim.js") }));
  },
};

export const options = {
  absWorkingDir: here,
  entryPoints: [resolve(here, "src", "app.js")],
  outfile,
  bundle: true,
  minify: true,
  format: "iife",
  platform: "browser",
  target: "es2020",
  // Node's `util`, named by a JSON-schema helper the dependency tree drags in but the browser never runs.
  alias: { util: "./src/util-shim.js" },
  plugins: [zodSlim],
  // The wallet modules reach for Node's Buffer and process as globals; the shims fill those in.
  inject: [resolve(here, "src", "shims.js")],
  define: {
    "process.env.NODE_ENV": '"production"',
    global: "globalThis",
  },
  legalComments: "eof",
  metafile: true,
  logLevel: "info",
};

const result = await build(options);

const size = statSync(outfile).size;
console.log(`${outfile}: ${(size / 1024).toFixed(0)} KB`);
if (process.argv.includes("--analyze")) {
  const { analyzeMetafile } = await import("esbuild");
  console.log(await analyzeMetafile(result.metafile, { verbose: false }));
}
