import { describe, expect, it } from "vitest";
import type { RunSnapshot } from "../../api/types";
import { UNKNOWN, browserTimingRows, formatDuration, overviewRows, outputRows, searchRows, serverTimingRows, settingRows, usageRows } from "./runDetails";

const base: RunSnapshot = {
  runId: "r1", conversationId: "c", userMessageId: "u", assistantMessageId: "a", status: "completed", statusReason: null,
  attempt: 1, error: null, outputChars: 12, hasAssistantMessage: true, providerCallStarted: true, admittedAt: null, endedAt: "2026-01-01T00:00:05Z"
};

describe("runDetails", () => {
  it("shows unknown instead of guessing when nothing was recorded", () => {
    expect(usageRows(base)).toEqual({ source: UNKNOWN, rows: [
      { label: "输入", value: UNKNOWN }, { label: "输出", value: UNKNOWN }, { label: "思考", value: UNKNOWN }, { label: "合计", value: UNKNOWN }] });
    const rows = overviewRows(base);
    expect(rows.find(r => r.label === "结束原因")!.value).toBe(UNKNOWN);
    expect(rows.find(r => r.label === "响应 ID")!.value).toBe(UNKNOWN);
    expect(settingRows(base).every(r => r.effective === UNKNOWN)).toBe(true);
  });

  it("labels provider-reported and estimated usage, keeping missing figures unknown", () => {
    const run = { ...base, usage: { inputTokens: 10, outputTokens: 3, source: "provider" } };
    const { source, rows } = usageRows(run);
    expect(source).toBe("服务商报告");
    expect(rows.map(r => r.value)).toEqual(["10", "3", UNKNOWN, UNKNOWN]);
    expect(usageRows({ ...run, usage: { ...run.usage, source: "estimate" } }).source).toContain("估算");
  });

  it("puts requested and effective side by side and flags what the server changed", () => {
    const run: RunSnapshot = {
      ...base,
      requested: { providerId: "gemini", modelName: "m1", searchMode: "auto", advanced: { temperature: 0.9 } },
      effective: { providerId: "gemini", modelName: "m1", presetId: "default", searchMode: "off", temperature: 0.9, thinkingLevel: "high" }
    };
    const rows = Object.fromEntries(settingRows(run).map(r => [r.label, r]));
    expect(rows["模型"]).toMatchObject({ requested: "m1", effective: "m1", differs: false });
    expect(rows["联网"]).toMatchObject({ requested: "自动", effective: "关闭", differs: true });
    expect(rows["温度"]).toMatchObject({ requested: "0.9", effective: "0.9", differs: false });
    expect(rows["思考"]).toMatchObject({ requested: "默认", effective: "深度 high" });
    expect(rows["Top P"]).toMatchObject({ requested: "默认", effective: "不适用" });
  });

  it("derives server durations only from recorded times", () => {
    const run: RunSnapshot = { ...base, timing: { createdAt: "2026-01-01T00:00:00Z", startedAt: "2026-01-01T00:00:01Z", firstOutputAt: "2026-01-01T00:00:02.500Z", endedAt: "2026-01-01T00:00:05Z" } };
    const rows = Object.fromEntries(serverTimingRows(run).map(r => [r.label, r.value]));
    expect(rows["首字延迟"]).toBe("1.5 秒");
    expect(rows["生成耗时"]).toBe("4.0 秒");
    expect(rows["总耗时"]).toBe("5.0 秒");
    const neverCalled = Object.fromEntries(serverTimingRows({ ...base, providerCallStarted: false, timing: { createdAt: "2026-01-01T00:00:00Z" } }).map(r => [r.label, r.value]));
    expect(neverCalled["开始调用模型"]).toBe("未调用");
    expect(neverCalled["首字延迟"]).toBe(UNKNOWN);
  });

  it("keeps browser-side timing separate and absent after a reload", () => {
    expect(browserTimingRows(undefined)).toBeNull();
    expect(browserTimingRows({ sentAt: 1000, firstOutputAt: 1800, endedAt: null })!.map(r => r.value)).toEqual(["800 毫秒", UNKNOWN]);
  });

  it("formats durations and counts provider calls", () => {
    expect(formatDuration(65_000)).toBe("1 分 5 秒");
    expect(formatDuration(null)).toBe(UNKNOWN);
    const rows = outputRows({ ...base, providerCalls: { generate: 1, summarize: 1, countTokens: 0 }, output: { chars: 12, thinkingChars: 5, citations: 2 } });
    expect(rows.at(-1)!.value).toBe("生成 1 · 摘要 1 · 计数 0");
    expect(outputRows({ ...base, providerCallStarted: false }).at(-1)!.value).toBe("未调用");
  });
});

describe("overview", () => {
  it("does not repeat a normal completion as a reason", () => {
    const run = { ...base, statusReason: "completed" } as RunSnapshot;
    expect(overviewRows(run).some(r => r.label === "原因")).toBe(false);
    expect(overviewRows({ ...run, status: "stopped", statusReason: "user_stop" }).find(r => r.label === "原因")!.value).toBe("你停止了生成");
  });
});

describe("web search", () => {
  const withSearch = (search: RunSnapshot["search"], calls?: RunSnapshot["providerCalls"]) => ({ ...base, search, providerCalls: calls }) as RunSnapshot;

  it("has no search section when none applies", () => {
    expect(searchRows(base)).toBeNull();
  });

  it("describes a search that was used, skipped, failed or unavailable", () => {
    const used = searchRows(withSearch({ status: "used", query: "西安 天气", sources: 3 }))!;
    expect(used).toEqual([
      { label: "状态", value: "已联网检索" },
      { label: "搜索词", value: "西安 天气" },
      { label: "来源", value: "3 条" }
    ]);
    expect(searchRows(withSearch({ status: "skipped", sources: 0, reason: "判断无需联网" }))!.map(r => r.value)).toEqual(["没有联网", "判断无需联网"]);
    expect(searchRows(withSearch({ status: "failed", query: "q", sources: 0, reason: "检索失败（TimeoutException）" }))![0]!.value).toContain("没有联网");
    expect(searchRows(withSearch({ status: "unavailable", sources: 0 }))![0]!.value).toContain("没有启用");
  });

  it("counts the search requests with the other model calls", () => {
    const rows = outputRows(withSearch({ status: "used", sources: 1 }, { generate: 1, summarize: 0, countTokens: 0, search: 2 }));
    expect(rows.at(-1)!.value).toBe("生成 1 · 摘要 0 · 计数 0 · 检索 2");
  });
});
