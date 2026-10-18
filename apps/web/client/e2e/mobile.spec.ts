import { type Page } from "@playwright/test";
import { expect, test } from "./fixtures";

// Phone-sized behaviour of the sheets: full width, and never reopened by a refresh.

const composer = (p: Page) => p.getByLabel("消息");

test.use({ viewport: { width: 390, height: 844 } });

test.beforeEach(async ({ page }) => {
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
});

test("the conversation actions sheet spans the screen width", async ({ page }) => {
  await composer(page).fill("sheet width");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.locator(".bubble.model").last()).toContainText("received", { timeout: 15_000 });

  await page.getByRole("button", { name: "会话操作", exact: true }).click();
  const sheet = page.getByRole("dialog", { name: "会话操作" });
  await expect(sheet).toBeVisible();
  const box = (await sheet.boundingBox())!;
  expect(box.x).toBeLessThanOrEqual(1);
  expect(Math.abs(box.width - 390)).toBeLessThanOrEqual(2);
  expect(box.x + box.width).toBeLessThanOrEqual(391);

  // The same from the conversation list.
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "会话列表" }).click();
  await page.locator(".rail .row").first().getByRole("button", { name: /^会话操作/ }).click();
  const listSheet = page.getByRole("dialog", { name: "会话操作" });
  const listBox = (await listSheet.boundingBox())!;
  expect(listBox.x).toBeLessThanOrEqual(1);
  expect(Math.abs(listBox.width - 390)).toBeLessThanOrEqual(2);
});

test("the settings sheet does not come back after a refresh", async ({ page }) => {
  await page.getByRole("button", { name: "对话设置" }).click();
  await expect(page.getByRole("dialog", { name: "对话设置" })).toBeVisible();
  await page.reload();
  await expect(composer(page)).toBeVisible();
  await expect(page.getByRole("dialog")).toHaveCount(0);
});

test("a desktop window still remembers an open inspector", async ({ page }) => {
  await page.setViewportSize({ width: 1280, height: 800 });
  await page.reload();
  await page.getByRole("button", { name: "对话设置" }).click();
  await expect(page.getByRole("complementary", { name: "检视器" })).toBeVisible();
  await page.reload();
  await expect(page.getByRole("complementary", { name: "检视器" })).toBeVisible();
});
