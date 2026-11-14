import type { QuotaMeter, QuotaStatus } from "../../api/types";

export type QuotaLevel = "ok" | "warn" | "full";

export interface QuotaLine {
  key: "requests" | "tokens" | "searches" | "attachments";
  label: string;
  used: string;
  limit: string;
  ratio: number;
}

export interface QuotaSummary {
  /** The fullest of the allowances, 0-100: the one that will run out first. */
  percent: number;
  level: QuotaLevel;
  /** Which allowance is the fullest. */
  fullest: QuotaLine;
  lines: QuotaLine[];
  resetsAt: Date;
}

const ratio = (meter: QuotaMeter) => (meter.limit > 0 ? meter.used / meter.limit : 0);

export function formatCount(value: number): string {
  if (value >= 10_000) return `${(value / 1000).toFixed(value >= 100_000 ? 0 : 1).replace(/\.0$/, "")}K`;
  return String(value);
}

export function formatBytes(value: number): string {
  if (value >= 1024 * 1024) return `${(value / (1024 * 1024)).toFixed(1).replace(/\.0$/, "")} MB`;
  if (value >= 1024) return `${Math.round(value / 1024)} KB`;
  return `${value} B`;
}

/** What to show for a day's allowance. Null when nothing applies to this person (admin, or quotas are off). */
export function summarize(status: QuotaStatus | null): QuotaSummary | null {
  if (!status || !status.limited) return null;
  const lines: QuotaLine[] = [
    { key: "requests", label: "对话", used: String(status.requests.used), limit: String(status.requests.limit), ratio: ratio(status.requests) },
    { key: "tokens", label: "内容量", used: formatCount(status.tokens.used), limit: formatCount(status.tokens.limit), ratio: ratio(status.tokens) },
    { key: "searches", label: "联网", used: String(status.searches.used), limit: String(status.searches.limit), ratio: ratio(status.searches) },
    { key: "attachments", label: "附件", used: formatBytes(status.attachmentBytes.used), limit: formatBytes(status.attachmentBytes.limit), ratio: ratio(status.attachmentBytes) }
  ];
  const fullest = lines.reduce((best, line) => (line.ratio > best.ratio ? line : best));
  const percent = Math.min(100, Math.round(fullest.ratio * 100));
  return {
    percent,
    level: fullest.ratio >= 1 ? "full" : fullest.ratio >= 0.8 ? "warn" : "ok",
    fullest,
    lines,
    resetsAt: new Date(status.resetsAtUtc)
  };
}

/** "今天 08:00" or "明天 08:00": when the allowance resets, in the viewer's own time. */
export function describeReset(resetsAt: Date, now: Date = new Date()): string {
  const clock = resetsAt.toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false });
  const sameDay = resetsAt.toDateString() === now.toDateString();
  return `${sameDay ? "今天" : "明天"} ${clock}`;
}
