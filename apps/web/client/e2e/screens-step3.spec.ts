import { expect, type Browser, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { test } from "./fixtures";

// Screenshots for the step 3 report. Off unless E2E_SHOTS names a step.
const step = process.env.E2E_SHOTS;
test.skip(!step, "set E2E_SHOTS=<step name> to capture screenshots");
const out = `../../../docs/design/screenshots/impl/${step}`;

const composer = (p: Page) => p.getByLabel("消息");
const sizes = { desktop: { width: 1280, height: 800 }, mobile: { width: 390, height: 844 } } as const;

async function person(browser: Browser, size: keyof typeof sizes, scheme: "light" | "dark" = "light") {
  mkdirSync(out, { recursive: true });
  const context = await browser.newContext({
    viewport: sizes[size], colorScheme: scheme,
    extraHTTPHeaders: { "x-forwarded-for": `10.6.${Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250) + 1}` }
  });
  await context.addInitScript(name => localStorage.setItem("e2e:user", name), `s3-${size}-${scheme}-${Date.now().toString(36)}`);
  const page = await context.newPage();
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
  return { page, context };
}
const shot = (page: Page, name: string) => page.screenshot({ path: `${out}/${name}.png` });
const ask = async (p: Page, text: string) => {
  await composer(p).fill(text);
  await p.getByRole("button", { name: "发送" }).click();
};

test("daily loop on desktop", async ({ browser }) => {
  const { page, context } = await person(browser, "desktop");
  await ask(page, "销售数据季度汇总");
  await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });
  await page.getByRole("button", { name: "新对话" }).click();
  await ask(page, "合同条款审阅");
  await expect(page.locator(".bubble.model").last()).toContainText("received", { timeout: 15_000 });

  await page.locator(".rail .row", { hasText: "销售数据季度汇总" }).getByRole("button", { name: /^会话操作/ }).click();
  await shot(page, "desktop-light-1-conversation-menu");
  await page.getByRole("menuitem", { name: "删除…" }).click();
  await shot(page, "desktop-light-2-delete-confirm");
  await page.getByRole("button", { name: "取消" }).click();
  await page.getByRole("menuitem", { name: "重命名" }).click();
  await page.getByLabel("会话名称").fill("销售数据季度汇总（修订）");
  await shot(page, "desktop-light-3-rename");
  await page.keyboard.press("Enter");

  await composer(page).fill("再核对一下第四季度的数据");
  await page.getByTestId("file-input").setInputFiles({ name: "q4.csv", mimeType: "text/csv", buffer: Buffer.from("a,b\n1,2\n") });
  await expect(page.getByText("✓ 草稿已自动保存在本机")).toBeVisible({ timeout: 5000 });
  await shot(page, "desktop-light-4-draft-saved");
  await context.close();
});

for (const size of ["desktop", "mobile"] as const) {
  test(`failures and retry ${size}`, async ({ browser }) => {
    const { page, context } = await person(browser, size);
    await ask(page, "按产品线拆分这些订单");
    await expect(page.locator(".bubble.model").last()).toContainText("received", { timeout: 15_000 });
    await ask(page, "/fail 列出异常订单");
    await expect(page.locator('[data-run-status="failed"]')).toBeVisible({ timeout: 15_000 });
    await shot(page, `${size}-light-5-failed-keeps-text`);

    await ask(page, `/flaky ${Date.now()}`);
    await expect(page.getByRole("button", { name: "重试" })).toBeVisible({ timeout: 15_000 });
    await shot(page, `${size}-light-6-failed-with-retry`);
    await page.getByRole("button", { name: "重试" }).click();
    await expect(page.locator(".note.earlier")).toBeVisible({ timeout: 15_000 });
    await expect(page.locator(".bubble.model").last()).toContainText("这次成功了。");
    await shot(page, `${size}-light-7-after-retry`);
    await context.close();
  });
}

test("another provider", async ({ browser }) => {
  const { page, context } = await person(browser, "desktop");
  await page.locator(".model-picker .chip").click();
  await page.getByRole("option", { name: /Deepseek Fake/ }).click();
  await ask(page, "what is two plus two");
  await expect(page.locator(".bubble.model")).toContainText("fake provider says", { timeout: 15_000 });
  await page.locator(".thinking-head").click();
  await shot(page, "desktop-light-8-other-provider");
  await context.close();
});

test("conversation menu on a phone", async ({ browser }) => {
  const { page, context } = await person(browser, "mobile", "dark");
  await ask(page, "hello");
  await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });
  await page.getByRole("button", { name: "会话操作", exact: true }).click();
  await page.waitForTimeout(300);
  await shot(page, "mobile-dark-8-conversation-menu");
  await context.close();
});
