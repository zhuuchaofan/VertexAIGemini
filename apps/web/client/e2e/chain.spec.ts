import { expect, test, type Page } from "@playwright/test";

// The whole chain for real: browser -> Node proxy -> API (run service, Firestore emulator) -> mock model.
// "/slow" makes the mock model stream a numbered line every 100 ms until it is cancelled.

const composer = (page: Page) => page.getByLabel("消息");
const send = (page: Page) => page.getByRole("button", { name: "发送" });
const status = (page: Page) => page.getByRole("status").filter({ hasText: /正在|连接/ });
const notes = (page: Page, status: string) => page.locator(`[data-run-status="${status}"]`);

async function ask(page: Page, text: string) {
  await composer(page).fill(text);
  await send(page).click();
}

// Every test is a different caller as far as the rate limiter is concerned, as if they were different people.
test.beforeEach(async ({ page }, info) => {
  await page.context().setExtraHTTPHeaders({ "x-forwarded-for": `10.0.${info.workerIndex}.${info.testId.length}${Math.floor(Math.random() * 250)}` });
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
});

test("a message is answered, stored, and still there after a reload", async ({ page }) => {
  await ask(page, "hello");
  await expect(page.locator(".bubble.model")).toContainText('received "hello"');
  await expect(page).toHaveURL(/\/app\/c\/[0-9a-f-]{36}$/);

  await page.reload();
  await expect(page.locator(".bubble.user")).toHaveText("hello");
  await expect(page.locator(".bubble.model")).toContainText('received "hello"');
});

test("stop ends the generation, keeps what was written, and says so", async ({ page }) => {
  await ask(page, "/slow go");
  await expect(page.locator(".bubble.model")).toContainText("line 3", { timeout: 15_000 });
  await expect(status(page)).toContainText("正在生成");

  await page.getByRole("button", { name: "停止" }).click();

  await expect(notes(page, "stopped")).toContainText("已停止");
  await expect(notes(page, "stopped")).toContainText("已保存已生成的");
  const kept = await page.locator(".bubble.model").first().innerText();
  expect(kept).toContain("line 1");
  await expect(send(page)).toBeDisabled(); // empty composer, not busy
  await composer(page).fill("next question");
  await expect(send(page)).toBeEnabled();

  // The stop is durable, not just a UI state.
  await page.reload();
  await expect(notes(page, "stopped")).toContainText("已停止");
  await expect(page.locator(".bubble.model").first()).toContainText("line 1");
});

test("closing the tab mid-stream leaves an interrupted run with its text, visible from a new tab", async ({ page, context }) => {
  await ask(page, "/slow go");
  await expect(page.locator(".bubble.model")).toContainText("line 3", { timeout: 15_000 });
  await expect(page).toHaveURL(/\/app\/c\/[0-9a-f-]{36}$/);
  const conversationUrl = page.url();

  await page.close();

  const reopened = await context.newPage();
  await reopened.goto(conversationUrl);
  await expect(notes(reopened, "interrupted")).toContainText("连接中断", { timeout: 30_000 });
  await expect(reopened.locator(".bubble.model").first()).toContainText("line 1");
  await expect(composer(reopened)).toBeEnabled();
});

test("reloading during a generation settles the run instead of losing it", async ({ page }) => {
  await ask(page, "/slow go");
  await expect(page.locator(".bubble.model")).toContainText("line 2", { timeout: 15_000 });

  await page.reload();

  await expect(notes(page, "interrupted")).toContainText("连接中断", { timeout: 30_000 });
  await expect(page.locator(".bubble.model").first()).toContainText("line 1");
  await expect(page.locator(".bubble.user")).toHaveText("/slow go"); // shown once, not duplicated
});

test("a double click sends one request and creates one run", async ({ page }) => {
  const posts: string[] = [];
  page.on("request", request => {
    if (request.method() === "POST" && request.url().endsWith("/api/v2/runs")) posts.push(request.url());
  });

  await composer(page).fill("/slow go");
  await send(page).dblclick();
  await expect(page.locator(".bubble.model")).toContainText("line 1", { timeout: 15_000 });
  await page.getByRole("button", { name: "停止" }).click();
  await expect(notes(page, "stopped")).toBeVisible();

  expect(posts.length).toBe(1);
  await expect(page.locator(".bubble.user")).toHaveCount(1);
});

test("a second window cannot start a run in a conversation that is busy", async ({ browser, page }) => {
  // Make a conversation first, so it has an address the second window can open.
  await ask(page, "hello");
  await expect(page).toHaveURL(/\/app\/c\/[0-9a-f-]{36}$/);
  await expect(page.locator(".bubble.model")).toContainText('received "hello"');
  const conversationUrl = page.url();

  await ask(page, "/slow go");
  await expect(page.locator(".bubble.model").last()).toContainText("line 2", { timeout: 15_000 });

  const second = await browser.newPage();
  await second.goto(conversationUrl);
  await ask(second, "second window");
  await expect(second.getByRole("alert")).toContainText("正在生成");
  await expect(composer(second)).toHaveValue("second window"); // nothing was lost

  await page.getByRole("button", { name: "停止" }).click();
  await expect(notes(page, "stopped")).toBeVisible();
  await second.close();
});
