import { expect, type Browser, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { test } from "./fixtures";

// Screenshots of the run-details inspector in the real pages. Off unless E2E_SHOTS names a folder.
const step = process.env.E2E_SHOTS;
test.skip(!step, "set E2E_SHOTS=<folder name> to capture screenshots");
const out = `../../../docs/design/screenshots/impl/${step}`;
const composer = (p: Page) => p.getByLabel("消息");
const sizes = { desktop: { width: 1280, height: 800 }, mobile: { width: 390, height: 844 } } as const;

async function person(browser: Browser, size: keyof typeof sizes, scheme: "light" | "dark") {
  mkdirSync(out, { recursive: true });
  const context = await browser.newContext({
    viewport: sizes[size], colorScheme: scheme,
    extraHTTPHeaders: { "x-forwarded-for": `10.8.${Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250) + 1}` }
  });
  await context.addInitScript(name => localStorage.setItem("e2e:user", name), `d-${size}-${scheme}-${Date.now().toString(36)}`);
  const page = await context.newPage();
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
  return { page, context };
}

for (const size of ["desktop", "mobile"] as const) {
  for (const scheme of ["light", "dark"] as const) {
    test(`run details ${size} ${scheme}`, async ({ browser }) => {
      const { page, context } = await person(browser, size, scheme);
      await composer(page).fill("按季度汇总销售数据");
      await page.getByRole("button", { name: "发送" }).click();
      await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });
      await page.getByRole("button", { name: "运行详情" }).last().click();
      await expect(page.getByText("服务端时间")).toBeVisible();
      await page.screenshot({ path: `${out}/${size}-${scheme}-run-details.png` });
      await context.close();
    });
  }
}
