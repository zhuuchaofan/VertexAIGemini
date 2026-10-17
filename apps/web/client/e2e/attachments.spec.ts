import { type Page } from "@playwright/test";
import { expect, test } from "./fixtures";

// Attachments through the real stack (the e2e API has no bucket, so content is kept inline on the message; the
// bucket path is covered by the API tests with an in-memory bucket).

const composer = (p: Page) => p.getByLabel("消息");
const fileInput = (p: Page) => p.getByTestId("file-input");
const send = (p: Page) => p.getByRole("button", { name: "发送" });

// a 1x1 PNG, enough for the browser to decode and re-encode
const PNG = Buffer.from("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==", "base64");

test.beforeEach(async ({ page }) => {
  await page.goto("/app/");
  await expect(composer(page)).toBeVisible();
});

test("a file is previewed, sent with the message, shown in the history, and downloads as it was", async ({ page }) => {
  await fileInput(page).setInputFiles({ name: "notes.txt", mimeType: "text/plain", buffer: Buffer.from("alpha beta\n") });
  const chip = page.locator(".pending-chip", { hasText: "notes.txt" });
  await expect(chip).toBeVisible();
  await expect(chip).toContainText("11 B");
  await expect(page.getByLabel(/已添加 1 个/)).toBeVisible();

  await composer(page).fill("please read this");
  await send(page).click();
  await expect(page.locator(".bubble.model")).toContainText("Attachments: 1", { timeout: 15_000 });
  await expect(page.locator(".pending-chip")).toHaveCount(0); // sent: the strip is empty again

  const check = async () => {
    const sentChip = page.locator(".att-chip", { hasText: "notes.txt" });
    await expect(sentChip).toBeVisible();
    await expect(sentChip).toBeEnabled();
    const [download] = await Promise.all([page.waitForEvent("download"), sentChip.click()]);
    expect(download.suggestedFilename()).toBe("notes.txt");
    const stream = await download.createReadStream();
    const chunks: Buffer[] = [];
    for await (const chunk of stream) chunks.push(chunk as Buffer);
    expect(Buffer.concat(chunks).toString()).toBe("alpha beta\n");
  };
  await check();
  await page.reload();
  await check(); // still there after a reload, from the stored message
});

test("what cannot be attached is refused with its reason, and the rest is kept", async ({ page }) => {
  await fileInput(page).setInputFiles([
    { name: "tool.exe", mimeType: "application/x-msdownload", buffer: Buffer.from("MZ") },
    { name: "年度报告.pdf", mimeType: "application/pdf", buffer: Buffer.alloc(600 * 1024) },
    { name: "ok.csv", mimeType: "text/csv", buffer: Buffer.from("a,b\n1,2\n") }
  ]);

  const alert = page.locator(".rejections");
  await expect(alert).toContainText("tool.exe 未添加");
  await expect(alert).toContainText("不支持这种格式");
  await expect(alert).toContainText("年度报告.pdf 未添加");
  await expect(alert).toContainText("超过 512 KB");
  await expect(page.locator(".pending-chip", { hasText: "ok.csv" })).toBeVisible();
  await expect(page.locator(".pending-chip")).toHaveCount(1);

  await alert.getByRole("button", { name: "知道了" }).click();
  await expect(alert).toHaveCount(0);
});

test("at most eight, the ninth is refused by name, and a removed one frees its place", async ({ page }) => {
  const files = Array.from({ length: 9 }, (_, i) => ({ name: `f${i}.txt`, mimeType: "text/plain", buffer: Buffer.from(String(i)) }));
  await fileInput(page).setInputFiles(files);
  await expect(page.locator(".pending-chip")).toHaveCount(8);
  await expect(page.locator(".rejections")).toContainText("f8.txt 未添加");
  await expect(page.locator(".rejections")).toContainText("最多 8 个附件");
  await expect(page.getByRole("button", { name: "添加附件" })).toBeDisabled();

  await page.getByRole("button", { name: "移除 f3.txt" }).click();
  await expect(page.locator(".pending-chip")).toHaveCount(7);
  await expect(page.getByRole("button", { name: "添加附件" })).toBeEnabled();
});

test("a picture is shrunk to WebP, previewed, sent without any text, and opens large from the history", async ({ page }) => {
  await fileInput(page).setInputFiles({ name: "chart.png", mimeType: "image/png", buffer: PNG });
  const chip = page.locator(".pending-chip", { hasText: "chart.webp" });
  await expect(chip).toBeVisible(); // re-encoded, so the name says so
  await expect(chip.locator("img")).toBeVisible();

  await expect(send(page)).toBeEnabled(); // an attachment alone is enough to send
  await send(page).click();
  await expect(page.locator(".bubble.model")).toContainText("Attachments: 1", { timeout: 15_000 });
  await expect(page.locator(".bubble.user")).toHaveCount(0); // there was no text to show

  await page.reload();
  const thumb = page.getByRole("button", { name: "查看图片 chart.webp" });
  await expect(thumb).toBeVisible();
  await expect(thumb.locator("img")).toHaveJSProperty("complete", true);
  await thumb.click();
  const viewer = page.getByRole("dialog", { name: "chart.webp" });
  await expect(viewer).toBeVisible();
  await page.keyboard.press("Escape");
  await expect(viewer).toHaveCount(0);
  await expect(thumb).toBeFocused();
});

test("pasting or dropping files attaches them, but pasted text stays text", async ({ page }) => {
  await composer(page).focus();
  await page.evaluate(() => {
    const data = new DataTransfer();
    data.items.add(new File(["pasted"], "pasted.txt", { type: "text/plain" }));
    document.querySelector("textarea")!.dispatchEvent(new ClipboardEvent("paste", { clipboardData: data, bubbles: true, cancelable: true }));
  });
  await expect(page.locator(".pending-chip", { hasText: "pasted.txt" })).toBeVisible();

  await page.evaluate(() => {
    const data = new DataTransfer();
    data.items.add(new File(["dropped"], "dropped.md", { type: "text/markdown" }));
    const box = document.querySelector(".composer-box")!;
    box.dispatchEvent(new DragEvent("dragover", { dataTransfer: data, bubbles: true, cancelable: true }));
    box.dispatchEvent(new DragEvent("drop", { dataTransfer: data, bubbles: true, cancelable: true }));
  });
  await expect(page.locator(".pending-chip", { hasText: "dropped.md" })).toBeVisible();
  await expect(page.locator(".pending-chip")).toHaveCount(2);

  await page.evaluate(() => {
    const data = new DataTransfer();
    data.setData("text/plain", "just words");
    const event = new ClipboardEvent("paste", { clipboardData: data, bubbles: true, cancelable: true });
    document.querySelector("textarea")!.dispatchEvent(event);
    (window as unknown as { pastedTextPrevented: boolean }).pastedTextPrevented = event.defaultPrevented;
  });
  expect(await page.evaluate(() => (window as unknown as { pastedTextPrevented: boolean }).pastedTextPrevented)).toBe(false);
  await expect(page.locator(".pending-chip")).toHaveCount(2);
});

test("a send that never reached the server brings its text and its attachments back", async ({ page }) => {
  await page.route("**/api/v2/runs", route => (route.request().method() === "POST" ? route.abort() : route.continue()));
  await fileInput(page).setInputFiles({ name: "keep.txt", mimeType: "text/plain", buffer: Buffer.from("keep me") });
  await composer(page).fill("do not lose this");
  await send(page).click();

  await expect(page.getByRole("alert")).toContainText("没有送达");
  await expect(composer(page)).toHaveValue("do not lose this");
  await expect(page.locator(".pending-chip", { hasText: "keep.txt" })).toBeVisible();

  await page.unroute("**/api/v2/runs");
  await send(page).click();
  await expect(page.locator(".bubble.model")).toContainText("Attachments: 1", { timeout: 15_000 });
});
