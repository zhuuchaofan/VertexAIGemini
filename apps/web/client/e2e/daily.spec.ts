import { type Page } from "@playwright/test";
import { expect, test } from "./fixtures";

// The day-to-day loop through the real stack: rename, export, delete, drafts, failures kept in place, and retry.

const composer = (p: Page) => p.getByLabel("消息");
const ask = async (p: Page, text: string) => {
  await composer(p).fill(text);
  await p.getByRole("button", { name: "发送" }).click();
};
const answered = async (p: Page, text: string) => {
  await ask(p, text);
  await expect(p.locator(".bubble.model").last()).toContainText("received", { timeout: 15_000 });
};
const row = (p: Page, title: string) => p.locator(".rail .row", { hasText: title });

test.beforeEach(async ({ page }) => {
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
});

test("a conversation is renamed from the list or from the header, Escape changes nothing, and it sticks", async ({ page }) => {
  await answered(page, "first topic");
  await row(page, "first topic").getByRole("button", { name: /^会话操作/ }).click();
  await page.getByRole("menuitem", { name: "重命名" }).click();
  const field = page.getByLabel("会话名称");
  await expect(field).toBeFocused();
  await field.fill("Quarterly numbers");
  await page.keyboard.press("Enter");
  await expect(row(page, "Quarterly numbers")).toBeVisible();

  // Escape puts the old name back.
  await row(page, "Quarterly numbers").getByRole("button", { name: /^会话操作/ }).click();
  await page.getByRole("menuitem", { name: "重命名" }).click();
  await page.getByLabel("会话名称").fill("never saved");
  await page.keyboard.press("Escape");
  await expect(row(page, "Quarterly numbers")).toBeVisible();
  await expect(row(page, "never saved")).toHaveCount(0);

  // From the header, with the list following.
  await page.getByRole("button", { name: "会话操作", exact: true }).click();
  await page.getByRole("menuitem", { name: "重命名" }).click();
  await page.getByLabel("会话名称").fill("Renamed in the header");
  await page.keyboard.press("Enter");
  await expect(page.locator(".bar h1")).toHaveText("Renamed in the header");
  await expect(row(page, "Renamed in the header")).toBeVisible();

  await page.reload();
  await expect(page.locator(".bar h1")).toHaveText("Renamed in the header");
});

test("export downloads the conversation as Markdown or JSON", async ({ page }) => {
  await answered(page, "export me");
  await page.getByRole("button", { name: "会话操作", exact: true }).click();
  const [markdown] = await Promise.all([page.waitForEvent("download"), page.getByRole("menuitem", { name: "导出为 Markdown" }).click()]);
  expect(markdown.suggestedFilename()).toMatch(/\.md$/);
  const text = await (async () => {
    const stream = await markdown.createReadStream();
    const chunks: Buffer[] = [];
    for await (const chunk of stream) chunks.push(chunk as Buffer);
    return Buffer.concat(chunks).toString();
  })();
  expect(text).toContain("export me");
  expect(text).toContain("**助手**");

  await page.getByRole("button", { name: "会话操作", exact: true }).click();
  const [json] = await Promise.all([page.waitForEvent("download"), page.getByRole("menuitem", { name: "导出为 JSON" }).click()]);
  expect(json.suggestedFilename()).toMatch(/\.json$/);
  const stream = await json.createReadStream();
  const chunks: Buffer[] = [];
  for await (const chunk of stream) chunks.push(chunk as Buffer);
  expect(JSON.parse(Buffer.concat(chunks).toString())).toBeTruthy();
});

test("delete asks first, can be cancelled, and then removes the conversation and leaves it", async ({ page }) => {
  await answered(page, "to keep");
  await page.getByRole("button", { name: "新对话" }).click();
  await answered(page, "to delete");
  const url = page.url();

  await row(page, "to delete").getByRole("button", { name: /^会话操作/ }).click();
  await page.getByRole("menuitem", { name: "删除…" }).click();
  await expect(page.getByText("删除这个会话？")).toBeVisible();
  await page.getByRole("button", { name: "取消" }).click();
  await expect(row(page, "to delete")).toBeVisible(); // nothing was deleted, and the menu is back for another go
  await page.getByRole("menuitem", { name: "删除…" }).click();
  await page.getByRole("button", { name: "删除", exact: true }).click();
  await expect(row(page, "to delete")).toHaveCount(0);
  await expect(row(page, "to keep")).toBeVisible();
  await expect(page).toHaveURL(/\/app\/$/); // it left the deleted conversation
  await page.goto(url);
  await expect(page.locator(".bubble.user")).toHaveCount(0);
});

test("a conversation cannot be deleted while it is generating", async ({ page }) => {
  await answered(page, "hello"); // an existing conversation has the actions menu; a brand new one is not saved yet
  await ask(page, "/slow go");
  await expect(page.locator(".bubble.model.streaming")).toContainText("line 2", { timeout: 15_000 });
  await page.getByRole("button", { name: "会话操作", exact: true }).click();
  await expect(page.getByRole("menuitem", { name: "删除…" })).toBeDisabled();
  await expect(page.getByText("正在生成回复，结束后才能删除")).toBeVisible();
  await page.keyboard.press("Escape");
  await page.getByRole("button", { name: "停止" }).click();
  await expect(page.locator('[data-run-status="stopped"]')).toBeVisible();
});

test("a draft survives a reload and a switch, is tagged in the list, and is gone once sent", async ({ page }) => {
  await answered(page, "existing topic");
  await composer(page).fill("half-written follow up");
  await page.getByTestId("file-input").setInputFiles({ name: "draft.txt", mimeType: "text/plain", buffer: Buffer.from("draft body") });
  await expect(page.getByText("✓ 草稿已自动保存在本机")).toBeVisible({ timeout: 5000 });
  await expect(row(page, "existing topic").getByText("草稿")).toBeVisible();

  await page.reload();
  await expect(composer(page)).toHaveValue("half-written follow up");
  await expect(page.locator(".pending-chip", { hasText: "draft.txt" })).toBeVisible();

  // Another conversation has its own (empty) draft; coming back finds this one again.
  await page.getByRole("button", { name: "新对话" }).click();
  await expect(composer(page)).toHaveValue("");
  await row(page, "existing topic").locator(".item").click();
  await expect(composer(page)).toHaveValue("half-written follow up");

  await page.getByRole("button", { name: "发送" }).click();
  await expect(page.locator(".bubble.model").last()).toContainText("Attachments: 1", { timeout: 15_000 });
  await expect(page.getByText("✓ 草稿已自动保存在本机")).toHaveCount(0);
  await page.reload();
  await expect(composer(page)).toHaveValue("");
  await expect(row(page, "existing topic").getByText("草稿")).toHaveCount(0);
});

test("a failure after some text keeps that text, says what happened, and leaves the history alone", async ({ page }) => {
  await answered(page, "before");
  await ask(page, "/fail partway");
  const failed = page.locator('[data-run-status="failed"]');
  await expect(failed).toBeVisible({ timeout: 15_000 });
  await expect(page.locator(".bubble.model").last()).toContainText("这段内容在失败前已经生成。");
  await expect(failed).toContainText("已保存已生成的");
  await expect(page.getByRole("button", { name: "重试" })).toHaveCount(0); // there is a reply: continue by sending, not retrying
  await expect(page.locator(".bubble.model").first()).toContainText("received"); // the earlier answer is untouched

  await page.reload();
  await expect(page.locator(".bubble.model").last()).toContainText("这段内容在失败前已经生成。");
  await expect(page.locator('[data-run-status="failed"]')).toBeVisible();
});

test("a failed turn with no reply can be retried once: one question, the new reply, the first failure kept as a note", async ({ page }) => {
  const message = `/flaky ${crypto.randomUUID()}`;
  await ask(page, message);
  const failed = page.locator('[data-run-status="failed"]').first();
  await expect(failed).toBeVisible({ timeout: 15_000 });
  await expect(failed).toContainText("没有产生回复");
  await expect(page.locator(".bubble.user")).toHaveCount(1);

  await page.getByRole("button", { name: "重试" }).click();
  await expect(page.locator(".bubble.model").last()).toContainText("这次成功了。", { timeout: 15_000 });
  await expect(page.locator(".bubble.user")).toHaveCount(1); // the question was not copied
  await expect(page.locator(".note.earlier")).toContainText("第 1 次");
  await expect(page.getByRole("button", { name: "重试" })).toHaveCount(0);

  await page.reload();
  await expect(page.locator(".bubble.user")).toHaveCount(1);
  await expect(page.locator(".bubble.model").last()).toContainText("这次成功了。");
  await expect(page.locator(".note.earlier")).toContainText("第 1 次");
});

test("a failure with nothing generated offers a retry and does not cover earlier answers", async ({ page }) => {
  await answered(page, "earlier");
  await ask(page, "/failfirst now");
  await expect(page.locator('[data-run-status="failed"]')).toBeVisible({ timeout: 15_000 });
  await expect(page.getByRole("button", { name: "重试" })).toBeVisible();
  await expect(page.locator(".bubble.model").first()).toContainText("received");
});
