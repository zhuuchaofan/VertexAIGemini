import { expect, test, type Browser, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";

// Screenshots of the real running pages for the progress reports. Off by default so the normal suite stays fast:
//   E2E_SHOTS=step1 npm run e2e -- e2e/screens.spec.ts
const step = process.env.E2E_SHOTS;
test.skip(!step, "set E2E_SHOTS=<step name> to capture screenshots");
const out = `../../../docs/design/screenshots/impl/${step}`;

const composer = (p: Page) => p.getByLabel("消息");
async function ask(p: Page, text: string) {
  await composer(p).fill(text);
  await p.getByRole("button", { name: "发送" }).click();
}

type Scheme = "light" | "dark";
const sizes = { desktop: { width: 1280, height: 800 }, mobile: { width: 390, height: 844 } } as const;

async function open(browser: Browser, size: keyof typeof sizes, scheme: Scheme, tag: string) {
  mkdirSync(out, { recursive: true });
  const context = await browser.newContext({
    viewport: sizes[size],
    colorScheme: scheme,
    deviceScaleFactor: 1,
    extraHTTPHeaders: { "x-forwarded-for": `10.8.${Math.floor(Math.random() * 250)}.${tag.length}` }
  });
  const page = await context.newPage();
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
  return { page, context };
}

const shot = (page: Page, name: string) => page.screenshot({ path: `${out}/${name}.png` });

for (const size of ["desktop", "mobile"] as const) {
  for (const scheme of ["light", "dark"] as const) {
    test(`chat ${size} ${scheme}`, async ({ browser }) => {
      const { page, context } = await open(browser, size, scheme, `${size}${scheme}`);
      await shot(page, `${size}-${scheme}-0-empty`);

      await ask(page, "/md 按季度汇总");
      await expect(page.locator(".bubble.model").last()).toContainText("结束。", { timeout: 15_000 });
      await expect(page.getByRole("button", { name: "停止" })).toHaveCount(0);
      await shot(page, `${size}-${scheme}-1-markdown`);

      await page.locator(".code-more").first().scrollIntoViewIfNeeded();
      await shot(page, `${size}-${scheme}-2-code-collapsed`);
      await page.locator(".code-more").first().click();
      await shot(page, `${size}-${scheme}-3-code-expanded`);

      await ask(page, "/think 对照两个来源");
      await expect(page.locator(".citations")).toBeVisible({ timeout: 15_000 });
      await expect(page.getByRole("button", { name: "停止" })).toHaveCount(0);
      await shot(page, `${size}-${scheme}-4-thinking-citations`);

      await page.locator(".thinking-head").last().click();
      await shot(page, `${size}-${scheme}-5-thinking-open`);

      await context.close();
    });
  }
}

test("pickers desktop", async ({ browser }) => {
  const { page, context } = await open(browser, "desktop", "light", "pickers");
  await ask(page, "hello");
  await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });
  await page.locator(".model-picker .chip").click();
  await shot(page, "desktop-light-6-model-picker");
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: /^联网/ }).click();
  await shot(page, "desktop-light-7-search-picker");
  await context.close();
});

test("phone sheets and drawer", async ({ browser }) => {
  const { page, context } = await open(browser, "mobile", "light", "sheets");
  await ask(page, "/md 汇总");
  await expect(page.locator(".bubble.model").last()).toContainText("结束。", { timeout: 15_000 });
  await page.locator(".title-model").click();
  await shot(page, "mobile-light-6-model-sheet");
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "会话列表" }).click();
  await page.waitForTimeout(450);
  await shot(page, "mobile-light-7-history-drawer");
  await context.close();
});

test("generating", async ({ browser }) => {
  for (const size of ["desktop", "mobile"] as const) {
    const { page, context } = await open(browser, size, "light", `gen${size}`);
    await ask(page, "/slow go");
    await expect(page.locator(".bubble.model.streaming")).toContainText("line 8", { timeout: 15_000 });
    await shot(page, `${size}-light-8-generating`);
    await page.getByRole("button", { name: "停止" }).click();
    await expect(page.locator('[data-run-status="stopped"]')).toBeVisible();
    await context.close();
  }
});
