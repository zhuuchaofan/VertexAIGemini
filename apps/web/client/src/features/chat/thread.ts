import type { ConversationDetail, RunSnapshot, StoredMessage } from "../../api/types";

export interface ThreadItem {
  key: string;
  user?: StoredMessage;
  reply?: StoredMessage;
  /** A finished run that did not end cleanly, shown under the turn it belongs to. */
  run?: RunSnapshot;
}

/**
 * Pairs each stored question with its reply and with the run that produced it. A run that ended in anything but
 * "completed" is attached to its turn so a stopped, interrupted or failed run is never silently missing.
 */
export function buildThread(detail: ConversationDetail | null): ThreadItem[] {
  if (!detail) return [];
  const runs = new Map(detail.runs.map(run => [run.runId, run]));
  const items: ThreadItem[] = [];
  let current: ThreadItem | null = null;

  for (const message of detail.messages) {
    if (message.role === "user") {
      current = { key: message.id, user: message, run: message.runId ? runs.get(message.runId) : undefined };
      items.push(current);
    } else if (current && !current.reply) {
      current.reply = message;
    } else {
      items.push({ key: message.id, reply: message, run: message.runId ? runs.get(message.runId) : undefined });
    }
  }

  return items;
}

/** The note shown for a run that did not complete, or null when there is nothing to say. */
export function runNote(run: RunSnapshot | undefined): { tone: "info" | "warn" | "err"; title: string; detail: string } | null {
  if (!run || run.status === "completed" || run.status === "running") return null;
  const saved = run.hasAssistantMessage ? `已保存已生成的 ${run.outputChars} 字。` : "没有产生回复。";

  switch (run.status) {
    case "stopped":
      return { tone: "info", title: "已停止", detail: saved };
    case "interrupted":
      return {
        tone: "warn",
        title: run.statusReason === "server_lost" ? "服务端中断" : "连接中断",
        detail: `${saved}直接发送新消息即可继续。`
      };
    default:
      return { tone: "err", title: run.error?.message ?? "生成失败", detail: `你的消息已保存，${saved}` };
  }
}
