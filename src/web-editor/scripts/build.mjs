import * as esbuild from "esbuild";
import { fileURLToPath } from "node:url";
import path from "node:path";

const editorRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");

await esbuild.build({
  absWorkingDir: editorRoot,
  entryPoints: ["src/editor.js"],
  bundle: true,
  format: "iife",
  outfile: "app/editor.bundle.js",
  platform: "browser",
  target: ["es2020"],
  minify: false,
  sourcemap: false,
  legalComments: "none",
  logLevel: "info"
});
