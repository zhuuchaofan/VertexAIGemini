import { fireEvent, render, screen, within } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";
import type { RunSnapshot } from "../../api/types";
import { RunInspector } from "./RunInspector";

const run: RunSnapshot = {
  runId: "run-1", conversationId: "c", userMessageId: "u", assistantMessageId: "a", status: "failed", statusReason: null,
  attempt: 2, error: { code: "provider_error", message: "模型出错", retryable: true }, outputChars: 0, hasAssistantMessage: false,
  providerCallStarted: true, admittedAt: null, endedAt: null, retryOfRunId: "run-0", retriedByRunId: "run-2",
  requested: { providerId: "gemini", modelName: "m1" },
  effective: { providerId: "gemini", modelName: "m1", presetId: "default", searchMode: "off" }
};

describe("RunInspector", () => {
  it("explains itself when no run is chosen", () => {
    render(<RunInspector run={null} />);
    expect(screen.getByText(/点“运行详情”/)).toBeInTheDocument();
  });

  it("shows request and applied columns, the error, and unknown where nothing was recorded", () => {
    render(<RunInspector run={run} />);
    const table = screen.getByRole("table");
    expect(within(table).getByRole("columnheader", { name: "请求" })).toBeInTheDocument();
    expect(within(table).getByRole("columnheader", { name: "实际" })).toBeInTheDocument();
    expect(screen.getByText("模型出错")).toBeInTheDocument();
    expect(screen.getByText("provider_error")).toBeInTheDocument();
    expect(screen.getByRole("heading", { name: "用量（未知）" })).toBeInTheDocument();
    expect(screen.getByText(/只有在本窗口发出的生成才有/)).toBeInTheDocument();
  });

  it("shows this window's measurements only when it has them", () => {
    render(<RunInspector run={run} observed={{ sentAt: 0, firstOutputAt: 1500, endedAt: 4000 }} />);
    expect(screen.getByText("1.5 秒")).toBeInTheDocument();
    expect(screen.getByText("4.0 秒")).toBeInTheDocument();
  });

  it("jumps between attempts", () => {
    const open = vi.fn();
    render(<RunInspector run={run} onOpenRun={open} />);
    fireEvent.click(screen.getByRole("button", { name: "查看被重试的一次" }));
    fireEvent.click(screen.getByRole("button", { name: "查看重试后的一次" }));
    expect(open.mock.calls).toEqual([["run-0"], ["run-2"]]);
  });
});

describe("RunInspector web search", () => {
  it("shows the search that was done for the answer", () => {
    render(<RunInspector run={{ ...run, search: { status: "used", query: "西安 天气", sources: 2 } }} />);
    expect(screen.getByRole("heading", { name: "联网检索" })).toBeInTheDocument();
    expect(screen.getByText("西安 天气")).toBeInTheDocument();
    expect(screen.getByText("2 条")).toBeInTheDocument();
  });

  it("has no search section for a run that had none", () => {
    render(<RunInspector run={run} />);
    expect(screen.queryByRole("heading", { name: "联网检索" })).toBeNull();
  });
});
