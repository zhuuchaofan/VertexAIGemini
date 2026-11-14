import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";
import { FakeRunApi } from "../chat/testkit";
import { MemoryPanel } from "./MemoryPanel";

describe("MemoryPanel", () => {
  it("reads the memory when opened, then adds, removes and switches it off", async () => {
    const api = new FakeRunApi();
    await api.addMemoryItem("我在上海工作");
    render(<MemoryPanel api={api} />);

    fireEvent.click(screen.getByRole("button", { name: /我的记忆/ }));
    expect(await screen.findByText("我在上海工作")).toBeTruthy();

    fireEvent.change(screen.getByLabelText("新的记忆"), { target: { value: "回答尽量简短" } });
    fireEvent.click(screen.getByRole("button", { name: "记住" }));
    expect(await screen.findByText("回答尽量简短")).toBeTruthy();
    expect(api.memory.items.map(item => item.text)).toEqual(["我在上海工作", "回答尽量简短"]);

    fireEvent.click(screen.getByRole("button", { name: "删除：我在上海工作" }));
    await waitFor(() => expect(screen.queryByText("我在上海工作")).toBeNull());
    expect(api.memory.items).toHaveLength(1);

    fireEvent.click(screen.getByRole("checkbox"));
    await waitFor(() => expect(api.memory.enabled).toBe(false));
  });
});
