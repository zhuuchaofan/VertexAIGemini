import { expect, test } from "./fixtures";

// Installability: a valid manifest with real icons, and a service worker that caches the shell and never /api.

test("the manifest is linked, scoped to the app, and its icons exist", async ({ page, request }) => {
  await page.goto("/app/");
  const href = await page.locator('link[rel="manifest"]').getAttribute("href");
  expect(href).toBe("/app/manifest.webmanifest");

  const response = await request.get(href!);
  expect(response.status()).toBe(200);
  expect(response.headers()["content-type"]).toContain("application/manifest+json");
  const manifest = await response.json();
  expect(manifest).toMatchObject({ start_url: "/app/", scope: "/app/", display: "standalone" });
  expect(manifest.name).toBeTruthy();

  const sizes = new Set<string>();
  for (const icon of manifest.icons as { src: string; sizes: string; type: string; purpose: string }[]) {
    const image = await request.get(icon.src);
    expect(image.status(), icon.src).toBe(200);
    expect(image.headers()["content-type"]).toBe(icon.type);
    sizes.add(`${icon.sizes}:${icon.purpose}`);
  }
  expect(sizes).toContain("192x192:any");
  expect(sizes).toContain("512x512:any");
  expect(sizes).toContain("512x512:maskable");
  expect((await request.get("/app/icons/apple-touch-icon.png")).status()).toBe(200);
});

test("the service worker takes over /app/ but leaves /api alone", async ({ page }) => {
  await page.goto("/app/");
  await page.waitForFunction(async () => (await navigator.serviceWorker.getRegistration("/app/"))?.active?.state === "activated", undefined, { timeout: 15_000 });
  const scope = await page.evaluate(async () => (await navigator.serviceWorker.getRegistration("/app/"))?.scope);
  expect(scope).toMatch(/\/app\/$/);

  // Once it controls the page, a reload still works and API calls reach the server, not a cache.
  await page.reload();
  await expect(page.getByLabel("消息")).toBeVisible();
  const api = await page.evaluate(async () => {
    const response = await fetch("/api/conversations");
    return { status: response.status, type: response.headers.get("content-type") ?? "" };
  });
  expect([200, 401]).toContain(api.status); // the server's own answer, never the cached app shell
  expect(api.type).not.toContain("text/html");
  const cached = await page.evaluate(async () => {
    const urls: string[] = [];
    for (const name of await caches.keys()) for (const request of await (await caches.open(name)).keys()) urls.push(new URL(request.url).pathname);
    return urls;
  });
  expect(cached.length).toBeGreaterThan(0);
  expect(cached.every(path => path.startsWith("/app/"))).toBe(true);
});
