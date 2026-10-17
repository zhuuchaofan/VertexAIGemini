import { type Page } from "@playwright/test";
import { expect, test, userHeader } from "./fixtures";

// Settings end to end: the three layers (my defaults, this conversation, the copy each run keeps), what each model
// shows, and what the server records about a run.

const composer = (p: Page) => p.getByLabel("消息");
const ask = async (p: Page, text: string) => {
  await composer(p).fill(text);
  await p.getByRole("button", { name: "发送" }).click();
};
const openSettings = async (p: Page) => {
  await expect(composer(p)).toBeVisible(); // the page must be up before asking whether the panel is already open
  if ((await p.getByRole("complementary", { name: "对话设置" }).count()) === 0) await p.getByRole("button", { name: "对话设置" }).click();
  return p.getByRole("complementary", { name: "对话设置" });
};

test.beforeEach(async ({ page }) => {
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
});

async function runIdOf(page: Page, user: string): Promise<string> {
  const id = page.url().split("/").pop();
  const detail = await page.request.get(`/api/v2/conversations/${id}`, { headers: userHeader(user) });
  const runs = (await detail.json()).runs as { runId: string }[];
  return runs[runs.length - 1]!.runId;
}

test("settings changed for a conversation are sent, remembered across a reload, and recorded on the run", async ({ page, user }) => {
  const bodies: Record<string, unknown>[] = [];
  page.on("request", request => {
    if (request.method() === "POST" && request.url().endsWith("/api/v2/runs")) bodies.push(request.postDataJSON());
  });

  const panel = await openSettings(page);
  await panel.getByRole("radio", { name: "高" }).click();
  await panel.getByRole("radio", { name: "关闭" }).click();
  await ask(page, "hello");
  await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });
  expect(bodies[0]).toMatchObject({ thinkingEnabled: true, thinkingLevel: "high", searchMode: "off", modelName: "mock-fast" });

  await page.reload();
  const again = await openSettings(page);
  await expect(again.getByRole("radio", { name: "高" })).toHaveAttribute("aria-checked", "true");
  await expect(again.getByRole("radio", { name: "关闭" })).toHaveAttribute("aria-checked", "true");

  // What the server kept for the run: what was asked for, and what the model really applied.
  const run = await (await page.request.get(`/api/v2/runs/${await runIdOf(page, user)}`, { headers: userHeader(user) })).json();
  expect(run.requested).toMatchObject({ thinkingLevel: "high", searchMode: "off" });
  expect(run.effective).toMatchObject({ modelName: "mock-fast", thinkingLevel: "high", searchMode: "off", maxOutputTokens: 8192 });
  expect(run.usage).toMatchObject({ source: "provider" });
  expect(run.usage.inputTokens).toBeGreaterThan(0);
  expect(run.finishReason).toBe("STOP");
  expect(run.timing.firstOutputAt).toBeTruthy();
  expect(run.providerCalls).toEqual({ generate: 1, summarize: 0, countTokens: 0 });
});

test("a change on an existing conversation is stored without sending anything", async ({ page }) => {
  await ask(page, "first");
  await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });
  const url = page.url();

  const panel = await openSettings(page);
  await panel.getByRole("radio", { name: "强制查证" }).click();
  await expect(panel.getByRole("radio", { name: "强制查证" })).toHaveAttribute("aria-checked", "true");
  await page.waitForTimeout(900); // the change is kept shortly after the person stops adjusting

  await page.goto(url);
  const again = await openSettings(page);
  await expect(again.getByRole("radio", { name: "强制查证" })).toHaveAttribute("aria-checked", "true");
});

test("my defaults start new conversations without touching existing ones, and a finished run does not move", async ({ page, user }) => {
  await ask(page, "old conversation");
  await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });
  const oldUrl = page.url();
  const runUrl = `/api/v2/runs/${await runIdOf(page, user)}`;
  const before = await (await page.request.get(runUrl, { headers: userHeader(user) })).json();

  const panel = await openSettings(page);
  await panel.getByRole("radio", { name: "我的默认" }).click();
  await panel.getByLabel("模型").selectOption({ label: "Mock Detailed" });
  await panel.getByRole("radio", { name: "关闭" }).click();
  await panel.getByRole("button", { name: "保存我的默认" }).click();
  await expect(panel.getByRole("status")).toContainText("已保存");

  // A new conversation starts from the defaults...
  await page.getByRole("button", { name: "新对话" }).click();
  await expect(page.locator(".model-picker .chip")).toContainText("Mock Detailed");
  await expect(page.getByRole("button", { name: /^联网/ })).toContainText("关闭");

  // ...the old one keeps its own, and so does the run it already made.
  await page.goto(oldUrl);
  await expect(page.locator(".model-picker .chip")).toContainText("Mock Fast");
  await expect(page.getByRole("button", { name: /^联网/ })).toContainText("自动");
  const after = await (await page.request.get(runUrl, { headers: userHeader(user) })).json();
  expect(after.requested).toEqual(before.requested);
  expect(after.effective).toEqual(before.effective);

  // And the defaults are still there after a reload.
  await page.goto("/app/");
  await expect(page.locator(".model-picker .chip")).toContainText("Mock Detailed");
});

test("only what the model supports is shown, and a bad number is explained and never sent", async ({ page, user }) => {
  const bodies: Record<string, unknown>[] = [];
  page.on("request", request => {
    if (request.method() === "POST" && request.url().endsWith("/api/v2/runs")) bodies.push(request.postDataJSON());
  });
  const panel = await openSettings(page);
  await panel.getByText("高级").click();

  // Mock Fast ignores sampling.
  await expect(panel.getByLabel("温度")).toHaveCount(0);
  await expect(panel.getByText(/忽略温度和 Top-P/)).toBeVisible();

  await panel.getByLabel("模型").selectOption({ label: "Mock Detailed" });
  await expect(panel.getByRole("radiogroup", { name: "思考级别" })).toHaveCount(0);
  await panel.getByLabel("温度").fill("5");
  await expect(panel.getByRole("alert")).toContainText("0 至 2");
  await panel.getByLabel("温度").fill("0.4");
  await panel.getByLabel("最大输出 token").fill("2048");

  await ask(page, "hello");
  await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });
  expect(bodies[0]).toMatchObject({ modelName: "mock-detailed", advanced: { temperature: 0.4, maxOutputTokens: 2048 } });
  expect(bodies[0]).not.toHaveProperty("thinkingLevel");

  const run = await (await page.request.get(`/api/v2/runs/${await runIdOf(page, user)}`, { headers: userHeader(user) })).json();
  expect(run.effective).toMatchObject({ modelName: "mock-detailed", temperature: 0.4, maxOutputTokens: 2048 });

  // Back to a model that ignores sampling: the value is dropped from what would be sent.
  await panel.getByLabel("模型").selectOption({ label: "Mock Fast" });
  await expect(panel.getByLabel("温度")).toHaveCount(0);
});

test("a budget model gets a switch and a budget, and sends them", async ({ page }) => {
  const bodies: Record<string, unknown>[] = [];
  page.on("request", request => {
    if (request.method() === "POST" && request.url().endsWith("/api/v2/runs")) bodies.push(request.postDataJSON());
  });
  const panel = await openSettings(page);
  await panel.getByLabel("模型").selectOption({ label: "Mock Budget" });
  await expect(panel.getByLabel("思考预算（token）")).toHaveValue("2000");
  await panel.getByLabel("思考预算（token）").fill("700");
  await ask(page, "hello");
  await expect(page.locator(".bubble.model")).toContainText("received", { timeout: 15_000 });
  expect(bodies[0]).toMatchObject({ modelName: "mock-budget", thinkingEnabled: true, thinkingBudget: 700 });

  const toggle = panel.getByRole("checkbox", { name: "开启思考" });
  await toggle.uncheck();
  await ask(page, "again");
  await expect(page.locator(".bubble.model").last()).toContainText("received", { timeout: 15_000 });
  expect(bodies[1]).toMatchObject({ thinkingEnabled: false, thinkingBudget: null });
});

test("on a phone settings open as a sheet and apply", async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole("button", { name: "对话设置" }).click();
  const sheet = page.getByRole("dialog", { name: "对话设置" });
  await expect(sheet).toBeVisible();
  await sheet.getByRole("radio", { name: "强制查证" }).click();
  await sheet.getByRole("button", { name: "完成" }).click();
  await expect(sheet).toHaveCount(0);
  await expect(page.getByRole("button", { name: /^联网/ })).toContainText("强制查证");
  await expect(page.getByRole("button", { name: "对话设置" })).toBeFocused();
});
