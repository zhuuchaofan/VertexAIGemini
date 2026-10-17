import react from "@vitejs/plugin-react";
import { defineConfig } from "vitest/config";

// The app is served under /app/ by the Node proxy (see apps/web/server.mjs). In development Vite proxies
// /api to the API so the browser only ever talks to one origin, exactly like production.
const backend = process.env.BACKEND_URL ?? "http://localhost:8080";

export default defineConfig({
  base: "/app/",
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { "/api": { target: backend, changeOrigin: true } }
  },
  build: { outDir: "dist", sourcemap: true },
  test: {
    environment: "jsdom",
    setupFiles: ["./src/test/setup.ts"],
    include: ["src/**/*.test.{ts,tsx}"],
    css: false
  }
});
