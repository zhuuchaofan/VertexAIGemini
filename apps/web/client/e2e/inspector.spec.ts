import { type Page } from "@playwright/test";
import AxeBuilder from "@axe-core/playwright";
import { expect, test } from "./fixtures";

// The run-details inspector against the real stack: what was asked vs applied, server times vs this window's, usage.

const composer = (p: Page) => p.getByLabel("消息");
const ask = async (p: Page, text: string) => {
  await composer(p).fill(text);
  await p.getByRole("button", { name: "发送" }).click();
};
const inspector = (p: Page) => p.getByRole("complementary", { name: "检视器" });

test.beforeEach(async ({ page }) => {
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
});

test("a reply opens its run: requested vs effective, server times, this window's times, unknown usage", async ({ page }) => {
  await ask(page, "inspect me");
  await expect(page.locator(".bubble.model").last()).toContainText("received", { timeout: 15_000 });
  await page.getByRole("button", { name: "运行详情" }).last().click();

  const panel = inspector(page);
  await expect(panel.getByRole("heading", { name: "运行详情", level: 2 })).toBeVisible();
  await expect(panel.getByText("已完成")).toBeVisible();
  await expect(panel.getByRole("columnheader", { name: "请求" })).toBeVisible();
  await expect(panel.getByRole("columnheader", { name: "实际" })).toBeVisible();
  await expect(panel.getByRole("heading", { name: "服务端时间" })).toBeVisible();
  await expect(panel.getByRole("heading", { name: "本窗口观察" })).toBeVisible();
  await expect(panel.getByText("发送到首字")).toBeVisible();
  await expect(panel.getByRole("heading", { name: /^用量/ })).toBeVisible();
  await expect(panel.locator("[data-run-id]")).toHaveAttribute("data-run-id", /.+/);

  // Tabs switch back to settings.
  await panel.getByRole("radio", { name: "设置" }).click();
  await expect(panel.getByRole("radio", { name: "此会话" })).toBeVisible();
});

test("after a reload the server's record is still there, but nothing is claimed about this window", async ({ page }) => {
  await ask(page, "reload me");
  await expect(page.locator(".bubble.model").last()).toContainText("received", { timeout: 15_000 });
  await page.reload();
  await expect(page.locator(".bubble.model").last()).toContainText("received");
  await page.getByRole("button", { name: "运行详情" }).last().click();
  const panel = inspector(page);
  await expect(panel.getByRole("heading", { name: "服务端时间" })).toBeVisible();
  await expect(panel.getByText("只有在本窗口发出的生成才有")).toBeVisible();
});

test("a failed run shows its error and the attempts link to each other", async ({ page }) => {
  await ask(page, `/flaky ${crypto.randomUUID()}`);
  const note = page.locator('[data-run-status="failed"]');
  await expect(note).toBeVisible({ timeout: 15_000 });
  await page.getByRole("button", { name: "重试" }).click();
  await expect(page.locator(".bubble.model").last()).toContainText("这次成功了。", { timeout: 15_000 });
  await page.getByRole("button", { name: "运行详情" }).last().click();
  const panel = inspector(page);
  await expect(panel.getByRole("button", { name: "查看被重试的一次" })).toBeVisible();
  await panel.getByRole("button", { name: "查看被重试的一次" }).click();
  await expect(panel.getByText("失败")).toBeVisible();
  await expect(panel.getByRole("button", { name: "查看重试后的一次" })).toBeVisible();
});

test("the inspector has no accessibility violations", async ({ page }) => {
  await ask(page, "axe check");
  await expect(page.locator(".bubble.model").last()).toContainText("received", { timeout: 15_000 });
  await page.getByRole("button", { name: "运行详情" }).last().click();
  await expect(inspector(page).getByText("已完成")).toBeVisible();
  const results = await new AxeBuilder({ page }).analyze();
  expect(results.violations).toEqual([]);
});

test("on a phone the details open in the sheet", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 800 });
  await page.goto("/app/");
  await ask(page, "phone inspect");
  await expect(page.locator(".bubble.model").last()).toContainText("received", { timeout: 15_000 });
  await page.getByRole("button", { name: "运行详情" }).last().click();
  const sheet = page.getByRole("dialog", { name: "运行详情" });
  await expect(sheet).toBeVisible();
  await expect(sheet.getByText("已完成")).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(sheet).toHaveCount(0);
});
