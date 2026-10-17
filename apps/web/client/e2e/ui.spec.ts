import { type Page } from "@playwright/test";
import { expect, test } from "./fixtures";

// The workbench itself: model and option choices reach the server, markdown and code behave, sources are kept,
// the phone layout can reach its history, and the theme sticks.

const composer = (p: Page) => p.getByLabel("消息");
const ask = async (p: Page, text: string) => {
  await composer(p).fill(text);
  await p.getByRole("button", { name: "发送" }).click();
};

test.beforeEach(async ({ page }) => {
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
});

test("the chosen model and search mode are what the server is asked for, and the reply says which model answered", async ({ page }) => {
  const bodies: Record<string, unknown>[] = [];
  page.on("request", request => {
    if (request.method() === "POST" && request.url().endsWith("/api/v2/runs")) bodies.push(request.postDataJSON());
  });

  await page.locator(".model-picker .chip").click();
  await page.getByRole("option", { name: /Mock Detailed/ }).click();
  await page.getByRole("button", { name: /^联网/ }).click();
  await page.getByRole("radio", { name: "关闭" }).click();
  await page.keyboard.press("Escape");

  await ask(page, "hello");
  await expect(page.locator(".bubble.model")).toContainText("mock-detailed received");
  expect(bodies).toHaveLength(1);
  expect(bodies[0]).toMatchObject({ providerId: "mock", modelName: "mock-detailed", searchMode: "off" });
  await expect(page.locator(".msg.model .msg-head strong")).toHaveText("Mock Detailed");
});

test("markdown tables and code render, a long block collapses and expands, and copy gives the exact code", async ({ page, context }) => {
  await context.grantPermissions(["clipboard-read", "clipboard-write"]);
  await ask(page, "/md 汇总");
  await expect(page.locator(".bubble.model")).toContainText("结束。", { timeout: 15_000 });

  await expect(page.locator(".bubble.model table")).toBeVisible();
  await expect(page.locator(".bubble.model th").first()).toHaveText("季度");
  await expect(page.locator(".code-lang").first()).toContainText("python");

  const long = page.locator("figure.code").nth(1);
  await expect(long).toHaveAttribute("data-collapsed", "true");
  await long.getByRole("button", { name: "展开全部 36 行" }).click();
  await expect(long).not.toHaveAttribute("data-collapsed", "true");
  await expect(long.locator(".gutter")).toContainText("36");

  await page.locator("figure.code").first().getByRole("button", { name: "复制" }).click();
  await expect(page.locator("figure.code").first().getByText("已复制")).toBeVisible();
  expect(await page.evaluate(() => navigator.clipboard.readText())).toBe('df.groupby(df["日期"].dt.quarter)["金额"].sum()');
});

test("thinking and sources are shown, and still there after a reload", async ({ page }) => {
  await ask(page, "/think 对照两个来源");
  await expect(page.locator(".citations")).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("status").filter({ hasText: /正在/ })).toHaveCount(0);

  const check = async () => {
    await expect(page.locator(".thinking-head")).toContainText("思考过程");
    await page.locator(".thinking-head").click();
    await expect(page.locator(".thinking-body")).toContainText("核对两个来源");
    await expect(page.locator(".citations a")).toHaveCount(2);
    await expect(page.locator(".citations a").first()).toHaveAttribute("href", "https://example.com/one");
    await expect(page.locator(".citations a").first()).toHaveAttribute("rel", /noopener/);
  };
  await check();
  await page.reload();
  await check();
});

test("on a phone the history is reachable from the menu and opens another conversation", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await ask(page, "first question");
  await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });
  await page.getByRole("button", { name: "新对话" }).first().click({ force: true }).catch(() => {});

  await page.getByRole("button", { name: "会话列表" }).click();
  await expect(page.getByRole("navigation", { name: "会话" })).toBeVisible();
  await page.getByRole("button", { name: "新对话" }).click();
  await expect(composer(page)).toHaveValue("");
  await expect(page.locator(".bubble.user")).toHaveCount(0);

  await page.getByRole("button", { name: "会话列表" }).click();
  await page.locator(".rail .item", { hasText: "first question" }).click();
  await expect(page.locator(".bubble.user")).toHaveText("first question");
  await expect(page).toHaveURL(/\/app\/c\//);
});

test("the theme can be chosen and is remembered", async ({ page }) => {
  const html = page.locator("html");
  await page.getByRole("button", { name: /^主题/ }).click(); // system -> light
  await expect(html).toHaveAttribute("data-theme", "light");
  await page.getByRole("button", { name: /^主题/ }).click(); // light -> dark
  await expect(html).toHaveAttribute("data-theme", "dark");
  await page.reload();
  await expect(html).toHaveAttribute("data-theme", "dark");
});

test("a model without thinking shows no thinking control, and the model list is keyboard reachable", async ({ page }) => {
  await expect(page.getByRole("button", { name: /^思考/ })).toBeVisible(); // Mock Fast has levels
  await page.locator(".model-picker .chip").focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("listbox", { name: "模型" })).toBeVisible();
  await page.getByRole("option", { name: /Mock Detailed/ }).focus();
  await page.keyboard.press("Enter");
  await expect(page.getByRole("listbox", { name: "模型" })).toHaveCount(0);
  await expect(page.getByRole("button", { name: /^思考/ })).toHaveCount(0); // Mock Detailed has none
  await expect(page.locator(".model-picker .chip")).toBeFocused();
});
