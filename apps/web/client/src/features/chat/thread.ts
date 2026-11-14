import type { ConversationDetail, RunSnapshot, StoredMessage } from "../../api/types";

export interface ReplyVersion {
  message: StoredMessage;
  /** The run that wrote it; absent for replies from before runs existed. */
  run?: RunSnapshot;
}

export interface ThreadItem {
  key: string;
  user?: StoredMessage;
  /** The newest reply, the one shown by default. */
  reply?: StoredMessage;
  /** Every reply to this question, oldest first. More than one only after a regenerate. */
  replies: ReplyVersion[];
  /** Every try at this question, oldest first. More than one only after a retry or a regenerate. */
  attempts: RunSnapshot[];
  /** The run whose reply is shown, or the last try when there is no reply. */
  finalRun?: RunSnapshot;
}

/**
 * Pairs each stored question with its replies and with every run that tried to answer it. A reply belongs to the
 * question its run answered, so a regenerated answer joins its question instead of standing alone. A run that ended in
 * anything but "completed" stays visible under its turn, so a stopped, interrupted or failed try is never silently
 * missing.
 */
export function buildThread(detail: ConversationDetail | null): ThreadItem[] {
  if (!detail) return [];
  const items: ThreadItem[] = [];
  const byUserMessage = new Map<string, ThreadItem>();
  let current: ThreadItem | null = null;

  const attemptsOf = (messageId: string) =>
    detail.runs.filter(run => run.userMessageId === messageId).sort((a, b) => a.attempt - b.attempt);
  const runOfReply = new Map(detail.runs.map(run => [run.assistantMessageId, run] as const));

  for (const message of detail.messages) {
    if (message.role === "user") {
      current = { key: message.id, user: message, replies: [], attempts: attemptsOf(message.id) };
      byUserMessage.set(message.id, current);
      items.push(current);
      continue;
    }

    const run = runOfReply.get(message.id);
    const owner = run ? byUserMessage.get(run.userMessageId) : undefined;
    if (owner) owner.replies.push({ message, run });
    else if (current && current.replies.length === 0) current.replies.push({ message, run });
    else items.push({ key: message.id, replies: [{ message, run }], attempts: [] });
  }

  for (const item of items) {
    const newest = item.replies[item.replies.length - 1];
    item.reply = newest?.message;
    const replyRun = newest ? newest.run ?? item.attempts.find(run => run.assistantMessageId === newest.message.id) : undefined;
    item.finalRun = replyRun ?? item.attempts[item.attempts.length - 1];
  }

  return items;
}

/** A failed or interrupted try with no reply, never retried, on the newest turn, whose attachments all arrived. */
export function canRetry(item: ThreadItem, isLatest: boolean, busy: boolean): boolean {
  const run = item.finalRun;
  if (!run || !isLatest || busy || item.reply) return false;
  if (run.status === "running" || run.status === "completed" || run.hasAssistantMessage || run.retriedByRunId) return false;
  return attachmentsArrived(item);
}

/**
 * The run to answer again when the newest turn already has a reply, or null when it cannot be regenerated: not the
 * newest turn, something is generating, or its last try is still running or was already followed by another.
 */
export function regenerateSource(item: ThreadItem, isLatest: boolean, busy: boolean): string | null {
  const last = item.attempts[item.attempts.length - 1];
  if (!isLatest || busy || !item.reply || !last) return null;
  if (last.status === "running" || last.retriedByRunId) return null;
  return attachmentsArrived(item) ? last.runId : null;
}

function attachmentsArrived(item: ThreadItem): boolean {
  return (item.user?.attachments ?? []).every(attachment => attachment.state === "stored");
}

/** The note shown for a run that did not complete, or null when there is nothing to say. */
export interface RunNote {
  tone: "info" | "warn" | "err";
  title: string;
  detail: string;
  /** What to do about a failure. */
  hint?: string;
  /** Administrators only: the provider's own words and what to change. */
  admin?: { detail?: string; hint?: string };
}

export function runNote(run: RunSnapshot | undefined): RunNote | null {
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
      return {
        tone: "err",
        title: run.error?.message ?? "生成失败",
        detail: `你的消息已保存，${saved}${retried}`,
        hint: run.error?.hint ?? undefined,
        admin: run.error?.detail || run.error?.adminHint ? { detail: run.error.detail ?? undefined, hint: run.error.adminHint ?? undefined } : undefined
      };
  }
}
