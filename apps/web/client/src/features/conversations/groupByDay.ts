import type { ConversationSummary } from "../../api/types";

export interface DayGroup {
  label: string;
  items: ConversationSummary[];
}

const startOfDay = (date: Date) => new Date(date.getFullYear(), date.getMonth(), date.getDate()).getTime();

/** 今天 / 昨天 / 过去 7 天 / 更早, in the order the list already has (newest first). */
export function groupByDay(items: ConversationSummary[], now: Date = new Date()): DayGroup[] {
  const today = startOfDay(now);
  const day = 86_400_000;
  const groups: DayGroup[] = [];
  const put = (label: string, item: ConversationSummary) => {
    const last = groups[groups.length - 1];
    if (last?.label === label) last.items.push(item);
    else groups.push({ label, items: [item] });
  };

  for (const item of items) {
    const at = startOfDay(new Date(item.updatedAt));
    if (at >= today) put("今天", item);
    else if (at >= today - day) put("昨天", item);
    else if (at >= today - 7 * day) put("过去 7 天", item);
    else put("更早", item);
  }
  return groups;
}

export function formatClock(iso: string): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleTimeString("zh-CN", { hour: "2-digit", minute: "2-digit", hour12: false });
}

export function formatWhen(iso: string, now: Date = new Date()): string {
  const date = new Date(iso);
  if (Number.isNaN(date.getTime())) return "";
  const dayDiff = Math.round((startOfDay(now) - startOfDay(date)) / 86_400_000);
  if (dayDiff <= 0) return formatClock(iso);
  if (dayDiff === 1) return `昨天 ${formatClock(iso)}`;
  if (dayDiff < 7) return ["周日", "周一", "周二", "周三", "周四", "周五", "周六"][date.getDay()] ?? "";
  return date.toLocaleDateString("zh-CN", { month: "numeric", day: "numeric" });
}
