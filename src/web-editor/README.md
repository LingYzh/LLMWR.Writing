# web-editor

WP16 CodeMirror TXT/MD renderer.

- Source: `src/`
- Bundler: esbuild 0.28.1 (`scripts/build.mjs`)
- Tests: `node:test` (`pnpm --dir src/web-editor run test`)
- Generated asset: `app/editor.bundle.js` (not committed; `build.ps1` always builds it)

The bundle is loaded from the WP15 virtual origin `https://app.llmw.invalid/` under the existing CSP. CodeMirror styles use nonce `llmw-editor` (`style-src 'self' 'nonce-llmw-editor'`). There is no `unsafe-inline` / `unsafe-eval` and no CDN.
