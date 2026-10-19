import type { RunSnapshot } from "../../api/types";

/** Shown wherever the server did not record a value. A figure we do not have is never guessed. */
export const UNKNOWN = "未知";

export interface Observed {
  sentAt: number;
  firstOutputAt: number | null;
  endedAt: number | null;
}

export interface Row {
  label: string;
  value: string;
  /** The value is a code or id that should read as such. */
  mono?: boolean;
}

export interface SettingRow {
  label: string;
  requested: string;
  effective: string;
  /** The server applied something other than what was asked. */
  differs: boolean;
}

export const statusText: Record<string, string> = {
  running: "进行中",
  completed: "已完成",
  stopped: "已停止",
  failed: "失败",
  interrupted: "已中断"
};

const reasonText: Record<string, string> = {
  user_stop: "你停止了生成",
  server_lost: "服务端中断",
  client_lost: "连接中断",
  timeout: "超过时限"
};

const finishText: Record<string, string> = {
  STOP: "正常结束",
  stop: "正常结束",
  MAX_TOKENS: "达到输出上限",
  length: "达到输出上限",
  SAFETY: "被安全策略拦截",
  content_filter: "被内容过滤拦截"
};

const searchText: Record<string, string> = { off: "关闭", auto: "自动", force: "总是联网" };
const onOff = (value: boolean | null | undefined) => (value === true ? "开启" : value === false ? "关闭" : undefined);

const number = (value: number | null | undefined) => (value === null || value === undefined ? undefined : String(value));

export function formatDuration(ms: number | null): string {
  if (ms === null || !Number.isFinite(ms) || ms < 0) return UNKNOWN;
  if (ms < 1000) return `${Math.round(ms)} 毫秒`;
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)} 秒`;
  const seconds = Math.round(ms / 1000);
  return `${Math.floor(seconds / 60)} 分 ${seconds % 60} 秒`;
}

const between = (from?: string | null, to?: string | null): number | null => {
  if (!from || !to) return null;
  const a = Date.parse(from);
  const b = Date.parse(to);
  return Number.isNaN(a) || Number.isNaN(b) ? null : b - a;
};

export function formatTimestamp(value?: string | null): string {
  if (!value) return UNKNOWN;
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return UNKNOWN;
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${pad(date.getMonth() + 1)}-${pad(date.getDate())} ${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

/** Requested vs applied, one row per setting. "默认" means nothing was asked, so the server chose. */
export function settingRows(run: RunSnapshot): SettingRow[] {
  const req = run.requested ?? null;
  const eff = run.effective ?? null;
  const row = (label: string, requested: string | undefined, effective: string | undefined, effectiveWhenUnrecorded = UNKNOWN): SettingRow => {
    const left = requested ?? "默认";
    const right = eff ? (effective ?? "不适用") : effectiveWhenUnrecorded;
    return { label, requested: left, effective: right, differs: requested !== undefined && eff !== null && effective !== undefined && requested !== effective };
  };
  const thinking = (enabled: boolean | null | undefined, level: string | null | undefined, budget: number | null | undefined) =>
    level ? `深度 ${level}` : budget !== null && budget !== undefined ? `预算 ${budget}` : onOff(enabled);

  return [
    row("服务商", req?.providerId, eff?.providerId),
    row("模型", req?.modelName, eff?.modelName),
    row("预设", req?.presetId, eff?.presetId),
    row("联网", req?.searchMode ? searchText[req.searchMode] ?? req.searchMode : undefined, eff ? searchText[eff.searchMode] ?? eff.searchMode : undefined),
    row("思考", thinking(req?.thinkingEnabled, req?.thinkingLevel, req?.thinkingBudget), eff ? thinking(eff.thinkingEnabled, eff.thinkingLevel, eff.thinkingBudget) : undefined),
    row("温度", number(req?.advanced?.temperature), number(eff?.temperature)),
    row("Top P", number(req?.advanced?.topP), number(eff?.topP)),
    row("最大输出", number(req?.advanced?.maxOutputTokens), number(eff?.maxOutputTokens)),
    row("安全阈值", req?.advanced?.safetyThreshold ?? undefined, eff?.safetyThreshold ?? undefined)
  ];
}

export function overviewRows(run: RunSnapshot): Row[] {
  const rows: Row[] = [{ label: "状态", value: statusText[run.status] ?? run.status }];
  if (run.statusReason && run.statusReason !== "completed") rows.push({ label: "原因", value: reasonText[run.statusReason] ?? run.statusReason });
  rows.push({ label: "结束原因", value: run.finishReason ? finishText[run.finishReason] ? `${finishText[run.finishReason]}（${run.finishReason}）` : run.finishReason : UNKNOWN });
  rows.push({ label: "尝试次数", value: String(run.attempt) });
  rows.push({ label: "模型版本", value: run.modelVersion || UNKNOWN, mono: Boolean(run.modelVersion) });
  rows.push({ label: "响应 ID", value: run.responseId || UNKNOWN, mono: Boolean(run.responseId) });
  rows.push({ label: "运行 ID", value: run.runId, mono: true });
  return rows;
}

export function serverTimingRows(run: RunSnapshot): Row[] {
  const t = run.timing ?? null;
  return [
    { label: "创建", value: formatTimestamp(t?.createdAt) },
    { label: "开始调用模型", value: run.providerCallStarted ? formatTimestamp(t?.startedAt) : "未调用" },
    { label: "首个输出", value: formatTimestamp(t?.firstOutputAt) },
    { label: "结束", value: formatTimestamp(t?.endedAt ?? run.endedAt) },
    { label: "首字延迟", value: formatDuration(between(t?.startedAt, t?.firstOutputAt)) },
    { label: "生成耗时", value: formatDuration(between(t?.startedAt, t?.endedAt ?? run.endedAt)) },
    { label: "总耗时", value: formatDuration(between(t?.createdAt, t?.endedAt ?? run.endedAt)) }
  ];
}

/** Only what this window watched happen. A run seen after a reload has none of this, and says so. */
export function browserTimingRows(observed: Observed | undefined): Row[] | null {
  if (!observed) return null;
  return [
    { label: "发送到首字", value: formatDuration(observed.firstOutputAt === null ? null : observed.firstOutputAt - observed.sentAt) },
    { label: "发送到结束", value: formatDuration(observed.endedAt === null ? null : observed.endedAt - observed.sentAt) }
  ];
}

export function usageRows(run: RunSnapshot): { source: string; rows: Row[] } {
  const u = run.usage ?? null;
  const tokens = (value?: number | null) => (value === null || value === undefined ? UNKNOWN : String(value));
  const source = !u ? UNKNOWN : u.source === "provider" ? "服务商报告" : u.source === "estimate" ? "估算（字符数 ÷ 4）" : u.source;
  return {
    source,
    rows: [
      { label: "输入", value: tokens(u?.inputTokens) },
      { label: "输出", value: tokens(u?.outputTokens) },
      { label: "思考", value: tokens(u?.thoughtsTokens) },
      { label: "合计", value: tokens(u?.totalTokens) }
    ]
  };
}

const searchStatusText: Record<string, string> = {
  used: "已联网检索",
  skipped: "没有联网",
  failed: "检索失败，回答没有联网",
  unavailable: "这个部署没有启用联网检索"
};

/** The web search done for a provider with no search of its own; null when none applies (off, or a native search). */
export function searchRows(run: RunSnapshot): Row[] | null {
  const search = run.search ?? null;
  if (!search) return null;
  const rows: Row[] = [{ label: "状态", value: searchStatusText[search.status] ?? search.status }];
  if (search.query) rows.push({ label: "搜索词", value: search.query });
  if (search.status === "used") rows.push({ label: "来源", value: `${search.sources} 条` });
  if (search.reason) rows.push({ label: "说明", value: search.reason });
  return rows;
}

export function outputRows(run: RunSnapshot): Row[] {
  const out = run.output ?? null;
  const calls = run.providerCalls ?? null;
  return [
    { label: "回复字数", value: String(out?.chars ?? run.outputChars) },
    { label: "思考字数", value: out ? String(out.thinkingChars) : UNKNOWN },
    { label: "引用数", value: out ? String(out.citations) : UNKNOWN },
    {
      label: "模型调用",
      value: calls
        ? `生成 ${calls.generate} · 摘要 ${calls.summarize} · 计数 ${calls.countTokens}${calls.search ? ` · 检索 ${calls.search}` : ""}`
        : run.providerCallStarted ? UNKNOWN : "未调用"
    }
  ];
}
