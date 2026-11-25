import { defineConfig } from "vite";
import { svelte } from "@sveltejs/vite-plugin-svelte";

// Plain compiled JS, no WASM, small no-runtime bundle (ADR 0014): the output in dist/ is static assets the
// .NET host can serve directly, which is what locked-down/air-gapped installs need. In dev, proxy the API so
// the SPA and backend share an origin without CORS.
const apiTarget = process.env.VITE_API_TARGET ?? "http://localhost:5080";

export default defineConfig({
  plugins: [svelte()],
  build: {
    outDir: "dist",
    emptyOutDir: true,
    sourcemap: true,
  },
  // Unit tests for the SPA's pure logic (tamp-observer#19). The frontend had NO test runner at all,
  // which is why every frontend file reported 0% coverage and why #14 -- a conditional that returned
  // the same value either way -- could not have been caught by a test.
  //
  // Scoped to src/lib/**: formatting, mapping and transport shaping, which are pure functions worth
  // pinning. Component rendering is deliberately out of scope here; it needs jsdom and a different
  // conversation about what a component test should assert.
  test: {
    include: ["src/**/*.test.ts"],
    environment: "node",
    coverage: {
      // istanbul, not v8: the v8 provider did not remap through the TypeScript source maps here,
      // reporting format.ts as 100% functions / 100% branches but 0% LINES. Lines are exactly what
      // Sonar's lcov ingestion reads, so v8 would have produced a test suite that runs, passes, and
      // still shows every file at 0% coverage -- a worse outcome than having no runner, because it
      // looks like it is working (tamp-observer#19).
      provider: "v8",
      reporter: ["text", "lcov"],
      reportsDirectory: "../artifacts/test-results/frontend",
      // `all: true` (the default) makes the provider instrument every matched file, including
      // untested ones, so a single file it cannot parse aborts the whole run with
      // "Coverage must be initialized with a path or an object". Svelte runes files (*.svelte.ts)
      // are the ones that break it here, so they are excluded -- they are component state, which
      // this suite does not try to cover anyway.
      include: ["src/lib/**/*.ts"],
      exclude: ["src/lib/**/*.svelte.ts", "src/lib/**/*.test.ts", "src/lib/api/schema.d.ts"],
      // Report untested matched files as 0% rather than omitting them. Omitting them would make the
      // percentage flattering and meaningless -- 100% "of the two files that have tests".
      all: true,
    },
  },
  server: {
    proxy: {
      "/api": { target: apiTarget, changeOrigin: true },
      "/openapi": { target: apiTarget, changeOrigin: true },
      "/health": { target: apiTarget, changeOrigin: true },
    },
  },
});
