import AxeBuilder from "@axe-core/playwright";
import { type Page } from "@playwright/test";
import { expect, test } from "./fixtures";

// Automated accessibility checks (axe) on the states people actually use, in both colour schemes and on a phone.
// Only serious and critical findings fail; axe cannot judge everything, so this is a floor, not a verdict.

const composer = (p: Page) => p.getByLabel("消息");

async function scan(page: Page, label: string) {
  const result = await new AxeBuilder({ page }).withTags(["wcag2a", "wcag2aa", "wcag21a", "wcag21aa"]).analyze();
  const bad = result.violations.filter(v => v.impact === "serious" || v.impact === "critical");
  const text = bad.map(v => `${v.id} (${v.impact}): ${v.nodes.slice(0, 3).map(n => n.target.join(" ")).join(" | ")}`).join("\n");
  expect(bad, `${label}\n${text}`).toEqual([]);
}

async function conversation(page: Page) {
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
  await composer(page).fill("/md summary");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.locator(".bubble.model")).toContainText("结束。", { timeout: 15_000 });
}

for (const scheme of ["light", "dark"] as const) {
  test(`desktop screens have no serious accessibility findings (${scheme})`, async ({ page }) => {
    await page.emulateMedia({ colorScheme: scheme });
    await page.goto("/app/");
    await expect(composer(page)).toBeVisible();
    await scan(page, "empty workbench");

    await conversation(page);
    await scan(page, "conversation with markdown, table and code");

    await page.getByRole("button", { name: "对话设置" }).click();
    await page.getByRole("complementary", { name: "对话设置" }).getByText("高级").click();
    await scan(page, "settings inspector");
    await page.getByRole("radio", { name: "我的默认" }).click();
    await scan(page, "settings inspector, defaults tab");
    await page.getByRole("button", { name: "关闭设置" }).click();

    await page.locator(".model-picker .chip").click();
    await scan(page, "model picker open");
    await page.keyboard.press("Escape");

    await page.getByTestId("file-input").setInputFiles([
      { name: "a.txt", mimeType: "text/plain", buffer: Buffer.from("x") },
      { name: "bad.exe", mimeType: "application/x-msdownload", buffer: Buffer.from("MZ") }
    ]);
    await expect(page.locator(".rejections")).toBeVisible();
    await scan(page, "attachments pending with a refusal");

    await page.getByRole("button", { name: "会话操作", exact: true }).click();
    await scan(page, "conversation actions menu");
  });
}

test("the phone layout has no serious accessibility findings", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await conversation(page);
  await scan(page, "phone conversation");
  await page.getByRole("button", { name: "会话列表" }).click();
  await page.waitForTimeout(400);
  await scan(page, "phone history drawer");
  await page.getByRole("button", { name: "新对话" }).click();
  await page.getByRole("button", { name: "对话设置" }).click();
  await expect(page.getByRole("dialog", { name: "对话设置" })).toBeVisible();
  await scan(page, "phone settings sheet");
});
