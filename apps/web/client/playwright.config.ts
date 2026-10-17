import { defineConfig } from "@playwright/test";

// Run through `npm run e2e`, which first starts a Firestore emulator, the API (mock model, fake dev user) and the
// real Node proxy that serves the built client, so the browser talks to the same topology as production.
export default defineConfig({
  testDir: "./e2e",
  timeout: 60_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [["list"]],
  use: {
    baseURL: process.env.E2E_BASE_URL ?? "http://127.0.0.1:5091",
    launchOptions: process.env.PLAYWRIGHT_CHROMIUM_PATH ? { executablePath: process.env.PLAYWRIGHT_CHROMIUM_PATH } : {},
    trace: "retain-on-failure"
  }
});
