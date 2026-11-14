import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { beforeAll, describe, expect, it, vi } from "vitest";
import type { WorkspaceConfig } from "../../api/types";
import { defaultSettings } from "../settings/chatSettings";
import type { Roles } from "../settings/useRoles";
import { ChatView } from "./ChatView";
import { FakeRunApi, MemoryPendingStore } from "./testkit";

const workspace: WorkspaceConfig = {
  defaultProviderId: "gemini",
  providers: [{
    provider: { id: "gemini", name: "Gemini", description: "" },
    models: [{ name: "G", modelName: "g", description: "", supportsThinking: false, supportsSampling: false, thinking: null, maxTokens: 8192 }],
    presets: [{ id: "default", name: "默认", prompt: "", description: "" }],
    defaultPresetId: "default",
    defaultModelName: "g"
  }]
};
const roles: Roles = {
  roles: [], maxRoles: 30, defaultRoleId: null, loaded: true,
  create: async () => ({ id: "x", name: "x", prompt: "x" }), update: async () => ({ id: "x", name: "x", prompt: "x" }),
  remove: async () => undefined, setDefault: async () => undefined
};

function renderChat(addressed: boolean, onDeleted = vi.fn()) {
  const api = new FakeRunApi(); // getConversation answers null: no such conversation for this account
  const settings = defaultSettings(workspace);
  render(
    <ChatView
      api={api}
      conversationId="2ae899b3-fb2b-4d8d-949f-5d9df976bbc2"
      workspace={workspace}
      defaults={settings}
      initial={settings}
      roles={roles}
      onSaveDefaults={async () => undefined}
      safetyThresholds={[]}
      draftKey={addressed ? "2ae899b3-fb2b-4d8d-949f-5d9df976bbc2" : "new"}
      onDeleted={onDeleted}
      addressed={addressed}
      pending={new MemoryPendingStore()}
    />
  );
  return { api, onDeleted };
}

beforeAll(() => {
  // jsdom has no media queries; a wide, mouse-driven screen.
  window.matchMedia ??= ((query: string) => ({
    matches: false, media: query, onchange: null,
    addEventListener: () => undefined, removeEventListener: () => undefined,
    addListener: () => undefined, removeListener: () => undefined, dispatchEvent: () => false
  })) as unknown as typeof window.matchMedia;
  Element.prototype.scrollIntoView ??= () => undefined;
});

describe("ChatView", () => {
  it("says so when the address names no conversation of this account, and does not send into it", async () => {
    const { api, onDeleted } = renderChat(true);
    expect(await screen.findByText("找不到这个会话。")).toBeTruthy();
    expect(screen.queryByText("有什么可以帮你？")).toBeNull();

    fireEvent.change(screen.getByLabelText("消息"), { target: { value: "你好" } });
    fireEvent.keyDown(screen.getByLabelText("消息"), { key: "Enter" });
    expect(api.createCalls).toHaveLength(0);

    fireEvent.click(screen.getByRole("button", { name: "开始新对话" }));
    expect(onDeleted).toHaveBeenCalled();
  });

  it("treats a new conversation that has not been sent yet as new, not as missing", async () => {
    const { api } = renderChat(false);
    expect(await screen.findByText("有什么可以帮你？")).toBeTruthy();
    expect(screen.queryByText("找不到这个会话。")).toBeNull();

    // The same keystrokes do send here, so the test above is not passing for want of a working send.
    fireEvent.change(screen.getByLabelText("消息"), { target: { value: "你好" } });
    fireEvent.keyDown(screen.getByLabelText("消息"), { key: "Enter" });
    await waitFor(() => expect(api.createCalls).toHaveLength(1));
  });
});
