import type { ConversationDetail, RunSnapshot, StoredMessage } from "../../api/types";

export interface ThreadItem {
  key: string;
  user?: StoredMessage;
  reply?: StoredMessage;
  /** Every try at this question, oldest first. More than one only after a retry. */
  attempts: RunSnapshot[];
  /** The run whose reply is shown, or the last try when there is no reply. */
  finalRun?: RunSnapshot;
}

/**
 * Pairs each stored question with its reply and with every run that tried to answer it. A run that ended in anything
 * but "completed" stays visible under its turn, so a stopped, interrupted or failed try is never silently missing.
 */
export function buildThread(detail: ConversationDetail | null): ThreadItem[] {
  if (!detail) return [];
  const items: ThreadItem[] = [];
  let current: ThreadItem | null = null;

  const attemptsOf = (messageId: string) =>
    detail.runs.filter(run => run.userMessageId === messageId).sort((a, b) => a.attempt - b.attempt);

  for (const message of detail.messages) {
    if (message.role === "user") {
      current = { key: message.id, user: message, attempts: attemptsOf(message.id) };
      items.push(current);
    } else if (current && !current.reply) {
      current.reply = message;
    } else {
      items.push({ key: message.id, reply: message, attempts: [] });
    }
  }

  for (const item of items) {
    const replyRun = item.reply ? item.attempts.find(run => run.assistantMessageId === item.reply?.id) : undefined;
    item.finalRun = replyRun ?? item.attempts[item.attempts.length - 1];
  }

  return items;
}

/** A failed or interrupted try with no reply, never retried, on the newest turn, whose attachments all arrived. */
export function canRetry(item: ThreadItem, isLatest: boolean, busy: boolean): boolean {
  const run = item.finalRun;
  if (!run || !isLatest || busy || item.reply) return false;
  if (run.status === "running" || run.status === "completed" || run.hasAssistantMessage || run.retriedByRunId) return false;
  return (item.user?.attachments ?? []).every(attachment => attachment.state === "stored");
}

/** The note shown for a run that did not complete, or null when there is nothing to say. */
export function runNote(run: RunSnapshot | undefined): { tone: "info" | "warn" | "err"; title: string; detail: string } | null {
  if (!run || run.status === "completed" || run.status === "running") return null;
  const saved = run.hasAssistantMessage ? `已保存已生成的 ${run.outputChars} 字。` : "没有产生回复。";
  const retried = run.retriedByRunId ? "已重试。" : "";

  switch (run.status) {
    case "stopped":
      return { tone: "info", title: "已停止", detail: saved };
    case "interrupted":
      return {
        tone: "warn",
        title: run.statusReason === "server_lost" ? "服务端中断" : "连接中断",
        detail: `${saved}${run.hasAssistantMessage || run.retriedByRunId ? retried : "直接发送新消息即可继续。"}`
      };
    default:
      return { tone: "err", title: run.error?.message ?? "生成失败", detail: `你的消息已保存，${saved}${retried}` };
  }
}
