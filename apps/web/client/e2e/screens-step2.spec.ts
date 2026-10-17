import { expect, type Browser, type Page } from "@playwright/test";
import { mkdirSync } from "node:fs";
import { test } from "./fixtures";

// Screenshots for the step 2 report: settings and attachments in the real pages. Off unless E2E_SHOTS names a step.
const step = process.env.E2E_SHOTS;
test.skip(!step, "set E2E_SHOTS=<step name> to capture screenshots");
const out = `../../../docs/design/screenshots/impl/${step}`;

const composer = (p: Page) => p.getByLabel("消息");
const PNG = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64");
const sizes = { desktop: { width: 1280, height: 800 }, mobile: { width: 390, height: 844 } } as const;

async function person(browser: Browser, size: keyof typeof sizes, scheme: "light" | "dark") {
  mkdirSync(out, { recursive: true });
  const context = await browser.newContext({
    viewport: sizes[size], colorScheme: scheme,
    extraHTTPHeaders: { "x-forwarded-for": `10.7.${Math.floor(Math.random() * 250)}.${Math.floor(Math.random() * 250) + 1}` }
  });
  await context.addInitScript(name => localStorage.setItem("e2e:user", name), `s2-${size}-${scheme}-${Date.now().toString(36)}`);
  const page = await context.newPage();
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
  return { page, context };
}

const shot = (page: Page, name: string) => page.screenshot({ path: `${out}/${name}.png` });

for (const scheme of ["light", "dark"] as const) {
  test(`settings desktop ${scheme}`, async ({ browser }) => {
    const { page, context } = await person(browser, "desktop", scheme);
    await composer(page).fill("按季度汇总销售数据");
    await page.getByRole("button", { name: "发送" }).click();
    await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });

    await page.getByRole("button", { name: "对话设置" }).click();
    const panel = page.getByRole("complementary", { name: "对话设置" });
    await panel.getByRole("radio", { name: "高" }).click();
    await shot(page, `desktop-${scheme}-1-settings-session`);

    await panel.getByText("高级").click();
    await panel.getByLabel("模型").selectOption({ label: "Mock Detailed" });
    await panel.getByLabel("温度").fill("0.4");
    await panel.getByLabel("最大输出 token").fill("2048");
    await shot(page, `desktop-${scheme}-2-settings-advanced`);

    await panel.getByRole("radio", { name: "我的默认" }).click();
    await shot(page, `desktop-${scheme}-3-settings-defaults`);
    await context.close();
  });
}

test("attachments desktop and mobile", async ({ browser }) => {
  for (const size of ["desktop", "mobile"] as const) {
    const { page, context } = await person(browser, size, "light");
    const input = page.getByTestId("file-input");
    await input.setInputFiles([
      { name: "chart.png", mimeType: "image/png", buffer: PNG },
      { name: "销售数据.csv", mimeType: "text/csv", buffer: Buffer.from("季度,销售额\nQ1,412.6\n") },
      { name: "合同草案.pdf", mimeType: "application/pdf", buffer: Buffer.alloc(300 * 1024) },
      { name: "年度报告.pdf", mimeType: "application/pdf", buffer: Buffer.alloc(600 * 1024) }
    ]);
    await composer(page).fill("再把第 6 条的原文贴出来对比一下。");
    await expect(page.locator(".rejections")).toBeVisible();
    await shot(page, `${size}-light-4-attachments-pending`);

    await page.getByRole("button", { name: "发送" }).click();
    await expect(page.locator(".bubble.model")).toContainText("Attachments: 3", { timeout: 15_000 });
    await page.reload();
    await expect(page.getByRole("button", { name: "查看图片 chart.webp" }).locator("img")).toHaveJSProperty("complete", true);
    await shot(page, `${size}-light-5-attachments-history`);
    await context.close();
  }
});

test("settings sheet on a phone", async ({ browser }) => {
  const { page, context } = await person(browser, "mobile", "light");
  await page.getByRole("button", { name: "对话设置" }).click();
  await expect(page.getByRole("dialog", { name: "对话设置" })).toBeVisible();
  await page.waitForTimeout(300);
  await shot(page, "mobile-light-6-settings-sheet");
  await page.getByRole("dialog", { name: "对话设置" }).getByRole("radio", { name: "我的默认" }).click();
  await shot(page, "mobile-light-7-settings-sheet-defaults");
  await context.close();
});
