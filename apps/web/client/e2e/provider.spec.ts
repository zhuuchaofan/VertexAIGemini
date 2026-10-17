import { type Page } from "@playwright/test";
import { expect, test, userHeader } from "./fixtures";

// A provider other than the built-in mock, through the same chain: a fake OpenAI-compatible upstream on :5099.

const composer = (p: Page) => p.getByLabel("消息");
// Node's own fetch, not the browser context: the fake upstream is local and must not go through any proxy.
const upstream = async () => (await (await fetch("http://127.0.0.1:5099/__state")).json()) as { requests: number; lastBody: Record<string, unknown> | null; closed: number };

test.beforeEach(async ({ page }) => {
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
  await page.locator(".model-picker .chip").click();
  await page.getByRole("option", { name: /Deepseek Fake/ }).click();
});

test("another provider's reply streams, keeps its reasoning apart, and is recorded with the provider's own usage", async ({ page, user }) => {
  await expect(page.getByRole("button", { name: /^思考/ })).toContainText("高"); // its own thinking options, not Gemini's
  await page.getByRole("button", { name: /^思考/ }).click();
  await page.getByRole("radio", { name: "最大" }).click();
  await page.keyboard.press("Escape");

  await composer(page).fill("what is two plus two");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.locator(".bubble.model")).toContainText("fake provider says: what is two plus two", { timeout: 15_000 });
  await expect(page.locator(".msg.model .msg-head strong")).toHaveText("Deepseek Fake");
  await expect(page.locator(".thinking-head")).toContainText("思考过程");
  await page.locator(".thinking-head").click();
  await expect(page.locator(".thinking-body")).toContainText("checking the question.");

  // What the upstream was asked.
  const seen = await upstream();
  expect(seen.lastBody).toMatchObject({ model: "deepseek-fake", stream: true, reasoning_effort: "max", max_tokens: 8192 });

  // What the server kept.
  const id = page.url().split("/").pop();
  const detail = await (await page.request.get(`/api/v2/conversations/${id}`, { headers: userHeader(user) })).json();
  const run = detail.runs[0];
  expect(run.requested).toMatchObject({ providerId: "deepseek", modelName: "deepseek-fake", thinkingLevel: "max" });
  expect(run.effective).toMatchObject({ providerId: "deepseek", thinkingLevel: "max", maxOutputTokens: 8192 });
  expect(run.usage).toMatchObject({ source: "provider", inputTokens: 12, outputTokens: 7, thoughtsTokens: 3, totalTokens: 19 });
  expect(run.finishReason).toBe("stop");

  await page.reload();
  await expect(page.locator(".bubble.model")).toContainText("fake provider says");
  await expect(page.locator(".msg.model .msg-head strong")).toHaveText("Deepseek Fake");
});

test("stopping a run on another provider closes the upstream request and keeps what was written", async ({ page }) => {
  const before = await upstream();
  await composer(page).fill("slow please");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.locator(".bubble.model.streaming")).toContainText("word3", { timeout: 15_000 });
  await page.getByRole("button", { name: "停止" }).click();
  await expect(page.locator('[data-run-status="stopped"]')).toContainText("已保存已生成的");
  await expect.poll(async () => (await upstream()).closed, { timeout: 5000 }).toBeGreaterThan(before.closed);

  await page.reload();
  await expect(page.locator(".bubble.model").first()).toContainText("word1 word2");
});

test("a file attached to a message goes to the other provider as text, with the question", async ({ page }) => {
  await page.getByTestId("file-input").setInputFiles({ name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("remember the milk") });
  await composer(page).fill("summarize");
  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.locator(".bubble.model")).toContainText("fake provider says", { timeout: 15_000 });
  const body = JSON.stringify((await upstream()).lastBody);
  expect(body).toContain("remember the milk");
  expect(body).toContain("notes.txt");
});
