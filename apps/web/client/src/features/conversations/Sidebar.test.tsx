import { act, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { ConversationSummary, RunApi } from "../../api/types";
import { Sidebar } from "./Sidebar";

const iso = (minutesAgo: number) => new Date(Date.now() - minutesAgo * 60_000).toISOString();
const item = (id: string, title: string, minutesAgo: number): ConversationSummary => ({ id, title, modelName: "m", updatedAt: iso(minutesAgo) });

function setup(searchResult: ConversationSummary[], pinned: ConversationSummary[] = [], setPinned = vi.fn(async () => undefined)) {
  const search = vi.fn<(query: string) => Promise<{ items: ConversationSummary[]; hasMore: boolean; nextCursor: null }>>(async () => ({ items: searchResult, hasMore: false, nextCursor: null }));
  const api = {
    listConversations: async () => ({ items: [item("a", "复利笔记", 1), item("b", "晚饭", 2), ...pinned], hasMore: true, nextCursor: "c", pinned }),
    searchConversations: search,
    setPinned
  } as unknown as RunApi;
  render(
    <Sidebar
      api={api}
      activeId={null}
      version={0}
      email="me@example.com"
      theme="system"
      onCycleTheme={() => undefined}
      accent="blue"
      onAccent={() => undefined}
      onOpen={() => undefined}
      onNew={() => undefined}
      onSignOut={() => undefined}
      open={false}
      onClose={() => undefined}
      onDeleted={() => undefined}
    />
  );
  return search;
}

beforeEach(() => vi.useFakeTimers({ shouldAdvanceTime: true }));
afterEach(() => vi.useRealTimers());

describe("Sidebar search", () => {
  it("filters what is loaded at once, then adds older matches from the server after a pause", async () => {
    const search = setup([item("z", "复利的老笔记", 60 * 24 * 40)]);
    await screen.findByText("复利笔记");

    fireEvent.change(screen.getByPlaceholderText("搜索会话"), { target: { value: "复利" } });
    expect(screen.queryByText("晚饭")).toBeNull();
    expect(screen.getByText("复利笔记")).toBeTruthy();
    expect(search).not.toHaveBeenCalled();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(350);
    });
    expect(search).toHaveBeenCalledWith("复利");
    expect(await screen.findByText("复利的老笔记")).toBeTruthy();
  });

  it("says it is searching rather than that nothing matches while the server is asked", async () => {
    setup([]);
    await screen.findByText("复利笔记");

    fireEvent.change(screen.getByPlaceholderText("搜索会话"), { target: { value: "不存在" } });
    expect(screen.getByText("正在搜索…")).toBeTruthy();

    await act(async () => {
      await vi.advanceTimersByTimeAsync(350);
    });
    expect(await screen.findByText("没有匹配的会话。")).toBeTruthy();
  });
});

describe("Sidebar pinning", () => {
  it("shows pinned conversations in their own group on top, once", async () => {
    setup([], [{ ...item("p", "旅行计划", 60 * 24 * 30), pinned: true }]);
    const pinnedGroup = await screen.findByRole("region", { name: "📌 置顶" });
    expect(pinnedGroup.textContent).toContain("旅行计划");
    expect(screen.getAllByText("旅行计划")).toHaveLength(1);
  });

  it("pins from the row menu and moves the conversation up", async () => {
    const setPinned = vi.fn(async () => undefined);
    setup([], [], setPinned);
    await screen.findByText("晚饭");
    expect(screen.queryByRole("region", { name: "📌 置顶" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "会话操作：晚饭" }));
    fireEvent.click(await screen.findByRole("menuitem", { name: /置顶/ }));

    expect(setPinned).toHaveBeenCalledWith("b", true);
    const pinnedGroup = await screen.findByRole("region", { name: "📌 置顶" });
    expect(pinnedGroup.textContent).toContain("晚饭");
  });
});
