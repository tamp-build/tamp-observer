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
  server: {
    proxy: {
      "/api": { target: apiTarget, changeOrigin: true },
      "/openapi": { target: apiTarget, changeOrigin: true },
      "/health": { target: apiTarget, changeOrigin: true },
    },
  },
});
