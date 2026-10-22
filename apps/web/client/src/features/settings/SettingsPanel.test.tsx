import { fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";
import type { ModelOption, WorkspaceConfig } from "../../api/types";
import { defaultSettings, withModel, type ChatSettings } from "./chatSettings";
import { SettingsPanel } from "./SettingsPanel";
import { useRoles, type RoleApi } from "./useRoles";

const levels: ModelOption = {
  name: "Levels", modelName: "levels", description: "", supportsThinking: true, supportsSampling: false, maxTokens: 8192,
  thinking: { kind: "gemini-level", canDisable: false, fixedEnabled: false, includeThoughts: true, default: "medium",
    options: [{ value: "low", label: "低" }, { value: "medium", label: "中" }, { value: "high", label: "高" }], budgets: [] }
};
const plain: ModelOption = { name: "Plain", modelName: "plain", description: "", supportsThinking: false, supportsSampling: true, thinking: null, maxTokens: 4096 };
const budget: ModelOption = {
  name: "Budget", modelName: "budget", description: "", supportsThinking: true, supportsSampling: true, maxTokens: 8192,
  thinking: { kind: "qwen-budget", canDisable: true, fixedEnabled: false, includeThoughts: true, default: "on", options: [], budgets: [500, 2000], defaultBudget: 2000 }
};
const presets = [{ id: "default", name: "默认助手", prompt: "", description: "" }, { id: "custom", name: "自定义", prompt: "", description: "" }];
const workspace: WorkspaceConfig = {
  defaultProviderId: "p",
  providers: [{ provider: { id: "p", name: "P", description: "" }, models: [levels, plain, budget], presets, defaultPresetId: "default", defaultModelName: "levels" }]
};

function fakeRoles(initial: { id: string; name: string; prompt: string }[] = []) {
  let roles = initial;
  let defaultRoleId: string | null = null;
  const api: RoleApi = {
    listRoles: vi.fn(async () => ({ roles, maxRoles: 30, defaultRoleId })),
    setDefaultRole: vi.fn(async id => { defaultRoleId = id; }),
    createRole: vi.fn(async role => { const created = { id: `r${roles.length + 1}`, ...role }; roles = [...roles, created]; return created; }),
    updateRole: vi.fn(async (id, role) => { const updated = { id, ...role }; roles = roles.map(r => (r.id === id ? updated : r)); return updated; }),
    deleteRole: vi.fn(async id => { roles = roles.filter(r => r.id !== id); })
  };
  return api;
}

function Harness({ roleApi, onSaveDefaults = vi.fn().mockResolvedValue(undefined), thresholds = ["BLOCK_MEDIUM_AND_ABOVE", "BLOCK_ONLY_HIGH"], start }: {
  roleApi?: RoleApi; onSaveDefaults?: (s: ChatSettings) => Promise<void>; thresholds?: string[]; start?: ChatSettings;
}) {
  const defaults = defaultSettings(workspace);
  const [settings, setSettings] = useState<ChatSettings>(start ?? defaults);
  const roles = useRoles(roleApi);
  return (
    <SettingsPanel
      workspace={workspace} safetyThresholds={thresholds} settings={settings} onSettings={setSettings}
      defaults={defaults} onSaveDefaults={onSaveDefaults} sessionState="saved" roles={roleApi ? roles : undefined} idPrefix="t"
    />
  );
}

describe("SettingsPanel", () => {
  it("draws only the controls the chosen model supports", () => {
    const { rerender } = render(<Harness />);
    // levels model: thinking levels, no temperature or top-p, a note explaining why
    expect(screen.getByRole("radiogroup", { name: "思考级别" })).toBeInTheDocument();
    expect(screen.queryByLabelText("温度")).toBeNull();
    expect(screen.getByText(/忽略温度和 Top-P/)).toBeInTheDocument();

    rerender(<Harness key="plain" start={withModel(workspace, defaultSettings(workspace), "p", "plain")} />);
    expect(screen.queryByRole("radiogroup", { name: "思考级别" })).toBeNull();
    expect(screen.queryByText("思考")).toBeNull();
    expect(screen.getByLabelText("温度")).toBeInTheDocument();
    expect(screen.getByLabelText("Top-P")).toBeInTheDocument();

    rerender(<Harness key="budget" start={withModel(workspace, defaultSettings(workspace), "p", "budget")} />);
    expect(screen.getByRole("checkbox", { name: "开启思考" })).toBeInTheDocument();
    expect(screen.getByLabelText("思考预算（token）")).toHaveValue(2000);
  });

  it("shows the filter choice only for the levels the account may use", () => {
    const { rerender } = render(<Harness />);
    const select = screen.getByLabelText("内容过滤");
    expect(within(select).getAllByRole("option").map(o => o.textContent)).toEqual(["跟随服务器策略", "拦截中、高风险", "仅拦截高风险"]);
    rerender(<Harness thresholds={[]} />);
    expect(screen.queryByLabelText("内容过滤")).toBeNull();
  });

  it("explains an out-of-range number instead of passing it on, and keeps the last good value", () => {
    render(<Harness start={withModel(workspace, defaultSettings(workspace), "p", "plain")} />);
    const temperature = screen.getByLabelText("温度");
    fireEvent.change(temperature, { target: { value: "3" } });
    expect(screen.getByRole("alert")).toHaveTextContent("0 至 2");
    fireEvent.change(temperature, { target: { value: "0.5" } });
    expect(screen.queryByRole("alert")).toBeNull();

    const topP = screen.getByLabelText("Top-P");
    fireEvent.change(topP, { target: { value: "0" } });
    expect(screen.getByRole("alert")).toHaveTextContent("大于 0");
    const max = screen.getByLabelText("最大输出 token");
    fireEvent.change(max, { target: { value: "5000" } }); // above this model's 4096
    expect(screen.getAllByRole("alert").some(a => a.textContent?.includes("4096"))).toBe(true);
  });

  it("marks what differs from the defaults and puts it back, one setting or all", () => {
    render(<Harness />);
    expect(screen.getByText("与默认相同")).toBeInTheDocument();
    fireEvent.click(screen.getByRole("radio", { name: "高" }));
    fireEvent.click(screen.getByRole("radio", { name: "关闭" }));
    expect(screen.getByText(/与默认不同：2 项/)).toBeInTheDocument();
    const resets = screen.getAllByRole("button", { name: "重置" });
    expect(resets).toHaveLength(2);
    fireEvent.click(resets[0]!);
    expect(screen.getByRole("radio", { name: "中" })).toHaveAttribute("aria-checked", "true");
    expect(screen.getByText(/与默认不同：1 项/)).toBeInTheDocument();
    fireEvent.click(screen.getByRole("button", { name: "全部恢复" }));
    expect(screen.getByText("与默认相同")).toBeInTheDocument();
  });

  it("saves this conversation's settings as the defaults, and reports a failure", async () => {
    const onSaveDefaults = vi.fn().mockResolvedValueOnce(undefined).mockRejectedValueOnce(new Error("服务器拒绝了这些设置"));
    render(<Harness onSaveDefaults={onSaveDefaults} />);
    fireEvent.click(screen.getByRole("radio", { name: "高" }));
    fireEvent.click(screen.getByRole("button", { name: "将此会话设置存为我的默认" }));
    await waitFor(() => expect(screen.getByRole("status")).toHaveTextContent("已保存"));
    expect(onSaveDefaults).toHaveBeenCalledWith(expect.objectContaining({ thinking: { level: "high" } }));

    fireEvent.click(screen.getByRole("button", { name: "将此会话设置存为我的默认" }));
    await waitFor(() => expect(screen.getByText("服务器拒绝了这些设置")).toBeInTheDocument());
  });

  it("edits the defaults on their own tab, saves only on request, and has no place for a prompt", async () => {
    const onSaveDefaults = vi.fn().mockResolvedValue(undefined);
    render(<Harness onSaveDefaults={onSaveDefaults} />);
    fireEvent.click(screen.getByRole("radio", { name: "我的默认" }));
    const save = screen.getByRole("button", { name: "保存我的默认" });
    expect(save).toBeDisabled();

    fireEvent.click(screen.getByRole("radio", { name: "关闭" }));
    expect(onSaveDefaults).not.toHaveBeenCalled();
    expect(save).toBeEnabled();
    expect(within(screen.getByLabelText("提示词预设")).queryByRole("option", { name: "自定义" })).toBeNull();

    fireEvent.click(save);
    await waitFor(() => expect(onSaveDefaults).toHaveBeenCalledWith(expect.objectContaining({ searchMode: "off" })));

    // The conversation tab was not touched by editing the defaults.
    fireEvent.click(screen.getByRole("radio", { name: "此会话" }));
    expect(screen.getByRole("radio", { name: "自动" })).toHaveAttribute("aria-checked", "true");
  });

  it("offers a custom prompt only for a conversation, and only when that preset is chosen", () => {
    render(<Harness />);
    expect(screen.queryByLabelText("自定义提示词")).toBeNull();
    fireEvent.change(screen.getByLabelText("提示词预设"), { target: { value: "custom" } });
    expect(screen.getByLabelText("自定义提示词")).toBeInTheDocument();
  });

  it("lists my roles beside the presets and copies the chosen one into the conversation", async () => {
    render(<Harness roleApi={fakeRoles([{ id: "r1", name: "写作搭子", prompt: "你是写作搭子" }])} />);
    const select = screen.getByLabelText("提示词预设");
    await waitFor(() => expect(within(select).getByRole("option", { name: "写作搭子" })).toBeInTheDocument());

    fireEvent.change(select, { target: { value: "role:r1" } });
    expect(select).toHaveValue("role:r1");
    expect(screen.queryByLabelText("自定义提示词")).toBeNull(); // a role is edited as a role, not as free text
    expect(screen.getByRole("button", { name: "编辑角色" })).toBeInTheDocument();
  });

  it("saves the custom prompt as a role and selects it", async () => {
    const api = fakeRoles();
    render(<Harness roleApi={api} start={{ ...defaultSettings(workspace), presetId: "custom", customPrompt: "我的提示词" }} />);
    fireEvent.click(await screen.findByRole("button", { name: "存为角色" }));
    fireEvent.change(screen.getByLabelText("角色名"), { target: { value: "神秘" } });
    fireEvent.click(screen.getByRole("button", { name: "保存角色" }));
    await waitFor(() => expect(api.createRole).toHaveBeenCalledWith({ name: "神秘", prompt: "我的提示词" }));
    await waitFor(() => expect(screen.getByLabelText("提示词预设")).toHaveValue("role:r1"));
  });

  it("deleting a role leaves the conversation's prompt in place as a custom prompt", async () => {
    const api = fakeRoles([{ id: "r1", name: "写作搭子", prompt: "你是写作搭子" }]);
    vi.spyOn(window, "confirm").mockReturnValue(true);
    render(<Harness roleApi={api} start={{ ...defaultSettings(workspace), presetId: "custom", customPrompt: "你是写作搭子" }} />);
    fireEvent.click(await screen.findByRole("button", { name: "删除角色" }));
    await waitFor(() => expect(api.deleteRole).toHaveBeenCalledWith("r1"));
    await waitFor(() => expect(screen.getByLabelText("自定义提示词")).toHaveValue("你是写作搭子"));
    expect(screen.getByLabelText("提示词预设")).toHaveValue("custom");
  });

  it("shows no role controls on the defaults tab", async () => {
    render(<Harness roleApi={fakeRoles([{ id: "r1", name: "写作搭子", prompt: "x" }])} />);
    fireEvent.click(screen.getByRole("radio", { name: "我的默认" }));
    expect(screen.queryByRole("button", { name: "新建角色" })).toBeNull();
  });

  it("sets and clears the default role, and marks it in the list", async () => {
    const api = fakeRoles([{ id: "r1", name: "写作搭子", prompt: "你是写作搭子" }]);
    render(<Harness roleApi={api} start={{ ...defaultSettings(workspace), presetId: "custom", customPrompt: "你是写作搭子" }} />);
    fireEvent.click(await screen.findByRole("button", { name: "设为默认角色" }));
    await waitFor(() => expect(api.setDefaultRole).toHaveBeenCalledWith("r1"));
    expect(await screen.findByRole("option", { name: "写作搭子（默认）" })).toBeInTheDocument();

    fireEvent.click(screen.getByRole("button", { name: "取消默认角色" }));
    await waitFor(() => expect(api.setDefaultRole).toHaveBeenLastCalledWith(null));
    expect(await screen.findByRole("option", { name: "写作搭子" })).toBeInTheDocument();
  });

  it("can move from a role to 自定义 and to a built-in preset, and the list follows", async () => {
    const api = fakeRoles([{ id: "r1", name: "写作搭子", prompt: "你是写作搭子" }]);
    render(<Harness roleApi={api} start={{ ...defaultSettings(workspace), presetId: "custom", customPrompt: "你是写作搭子" }} />);
    const select = await screen.findByLabelText("提示词预设");
    await waitFor(() => expect(select).toHaveValue("role:r1"));

    fireEvent.change(select, { target: { value: "custom" } });
    expect(select).toHaveValue("custom");
    expect(screen.getByLabelText("自定义提示词")).toHaveValue(""); // a blank prompt of its own, not the role's text

    fireEvent.change(screen.getByLabelText("自定义提示词"), { target: { value: "临时试一试" } });
    expect(select).toHaveValue("custom");
    expect(screen.getByRole("button", { name: "存为角色" })).toBeInTheDocument();

    fireEvent.change(select, { target: { value: "role:r1" } });
    expect(select).toHaveValue("role:r1");
    fireEvent.change(select, { target: { value: "default" } });
    expect(select).toHaveValue("default");
  });
});
