import { describe, expect, it } from "vitest";
import type { ConversationDetail, RunSnapshot, StoredMessage } from "../../api/types";
import { buildThread, canRetry, runNote } from "./thread";
import { snapshot } from "./testkit";

const message = (id: string, role: "user" | "model", runId: string | null, extra: Partial<StoredMessage> = {}): StoredMessage => ({
  id, role, content: `${role} ${id}`, thinkingContent: null, runId, status: null, createdAt: "2026-10-17T10:00:00Z", ...extra
});
const run = (runId: string, userMessageId: string, overrides: Partial<RunSnapshot> = {}) =>
  snapshot({ runId, userMessageId, assistantMessageId: `a-${runId}`, ...overrides });
const detail = (messages: StoredMessage[], runs: RunSnapshot[]): ConversationDetail =>
  ({ id: "c", title: "t", providerId: "p", modelName: "m", activeRunId: null, messages, runs });

describe("thread", () => {
  it("keeps every try at a question under that question, and shows the reply of the run that wrote it", () => {
    const failed = run("r1", "u1", { status: "failed", attempt: 1, retriedByRunId: "r2", error: { code: "x", message: "上游不可用", retryable: true } });
    const retried = run("r2", "u1", { status: "completed", attempt: 2, retryOfRunId: "r1", hasAssistantMessage: true, assistantMessageId: "a1" });
    const [item] = buildThread(detail([message("u1", "user", "r1"), message("a1", "model", "r2")], [retried, failed]));
    expect(item?.attempts.map(a => a.runId)).toEqual(["r1", "r2"]); // oldest first whatever the order they arrived in
    expect(item?.finalRun?.runId).toBe("r2");
    expect(item?.reply?.id).toBe("a1");
    expect(runNote(failed)?.detail).toContain("已重试");
  });

  it("uses the last try when there is no reply", () => {
    const first = run("r1", "u1", { status: "failed", attempt: 1, retriedByRunId: "r2" });
    const second = run("r2", "u1", { status: "failed", attempt: 2, retryOfRunId: "r1" });
    const [item] = buildThread(detail([message("u1", "user", "r1")], [first, second]));
    expect(item?.finalRun?.runId).toBe("r2");
    expect(item?.reply).toBeUndefined();
  });

  it("offers a retry only for a failed, unanswered, never-retried newest turn whose attachments arrived", () => {
    const failed = run("r1", "u1", { status: "failed" });
    const base = buildThread(detail([message("u1", "user", "r1")], [failed]))[0]!;
    expect(canRetry(base, true, false)).toBe(true);
    expect(canRetry(base, false, false)).toBe(false); // not the newest turn
    expect(canRetry(base, true, true)).toBe(false); // something is running
    expect(canRetry({ ...base, finalRun: run("r1", "u1", { status: "failed", retriedByRunId: "r2" }) }, true, false)).toBe(false);
    expect(canRetry({ ...base, finalRun: run("r1", "u1", { status: "failed", hasAssistantMessage: true }) }, true, false)).toBe(false);
    expect(canRetry({ ...base, finalRun: run("r1", "u1", { status: "running" }) }, true, false)).toBe(false);

    const withBrokenFile = buildThread(detail(
      [message("u1", "user", "r1", { attachments: [{ index: 0, fileName: "a.txt", mimeType: "text/plain", sizeBytes: 1, state: "failed" }] })],
      [failed]
    ))[0]!;
    expect(canRetry(withBrokenFile, true, false)).toBe(false);
    const withGoodFile = buildThread(detail(
      [message("u1", "user", "r1", { attachments: [{ index: 0, fileName: "a.txt", mimeType: "text/plain", sizeBytes: 1, state: "stored" }] })],
      [failed]
    ))[0]!;
    expect(canRetry(withGoodFile, true, false)).toBe(true);
  });

  it("explains each way a run can end without saying more than is known", () => {
    expect(runNote(run("r", "u", { status: "completed" }))).toBeNull();
    expect(runNote(run("r", "u", { status: "stopped", hasAssistantMessage: true, outputChars: 71 }))).toMatchObject({ title: "已停止", detail: "已保存已生成的 71 字。" });
    expect(runNote(run("r", "u", { status: "interrupted", statusReason: "connection" }))?.detail).toContain("没有产生回复");
    expect(runNote(run("r", "u", { status: "interrupted", statusReason: "server_lost" }))?.title).toBe("服务端中断");
    expect(runNote(run("r", "u", { status: "failed", error: { code: "quota", message: "今日请求次数已用尽，请明天再试", retryable: false } }))).toMatchObject({
      tone: "err", title: "今日请求次数已用尽，请明天再试", detail: "你的消息已保存，没有产生回复。"
    });
  });
});
