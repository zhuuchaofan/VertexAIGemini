import { describe, expect, it } from "vitest";
import type { ModelOption, WorkspaceConfig } from "../../api/types";
import { capabilities, defaultSettings, thinkingLabel, toRunOptions, withModel } from "./chatSettings";

const gemini: ModelOption = {
  name: "Gemini", modelName: "g-flash", description: "", supportsThinking: true, supportsSampling: false, maxTokens: 65536,
  thinking: { kind: "gemini-level", canDisable: false, fixedEnabled: false, includeThoughts: true, default: "medium",
    options: [{ value: "low", label: "低" }, { value: "medium", label: "中" }, { value: "high", label: "高" }], budgets: [] }
};
const plain: ModelOption = { name: "Plain", modelName: "p", description: "", supportsThinking: false, supportsSampling: true, thinking: null, maxTokens: 8192 };
const qwen: ModelOption = {
  name: "Qwen", modelName: "q", description: "", supportsThinking: true, supportsSampling: true, maxTokens: 32768,
  thinking: { kind: "qwen-budget", canDisable: true, fixedEnabled: false, includeThoughts: true, default: "on", options: [], budgets: [500, 2000], defaultBudget: 2000 }
};
const fixed: ModelOption = {
  name: "Kimi", modelName: "k", description: "", supportsThinking: true, supportsSampling: true, maxTokens: 32768,
  thinking: { kind: "kimi-fixed", canDisable: false, fixedEnabled: true, includeThoughts: true, options: [], budgets: [] }
};
const workspace: WorkspaceConfig = {
  defaultProviderId: "gemini",
  providers: [
    { provider: { id: "gemini", name: "Gemini", description: "" }, models: [gemini, plain], presets: [{ id: "default", name: "d", prompt: "", description: "" }], defaultPresetId: "default", defaultModelName: "g-flash" },
    { provider: { id: "other", name: "Other", description: "" }, models: [qwen, fixed], presets: [{ id: "default", name: "d", prompt: "", description: "" }, { id: "custom", name: "c", prompt: "", description: "" }], defaultPresetId: "default", defaultModelName: "q" }
  ]
};

describe("chat settings", () => {
  it("starts from the default provider and model with that model's own thinking default", () => {
    expect(defaultSettings(workspace)).toMatchObject({ providerId: "gemini", modelName: "g-flash", thinking: { level: "medium" }, searchMode: "auto" });
  });

  it("shows only what the model supports", () => {
    expect(capabilities(gemini)).toMatchObject({ sampling: false, thinkingKind: "level", canDisableThinking: false });
    expect(capabilities(plain)).toMatchObject({ sampling: true, thinkingKind: "none" });
    expect(capabilities(qwen).thinkingKind).toBe("toggle-budget");
    expect(capabilities(fixed).thinkingKind).toBe("fixed");
  });

  it("never sends sampling or thinking the model does not take", () => {
    let settings = withModel(workspace, defaultSettings(workspace), "gemini", "p");
    settings = { ...settings, advanced: { temperature: 0.4, topP: 0.9, maxOutputTokens: 100 } };
    expect(toRunOptions(workspace, settings)).toEqual({
      providerId: "gemini", modelName: "p", presetId: "default", searchMode: "auto",
      advanced: { temperature: 0.4, topP: 0.9, maxOutputTokens: 100 }
    });

    const onGemini = withModel(workspace, settings, "gemini", "g-flash");
    expect(onGemini.advanced).toEqual({ maxOutputTokens: 100 }); // temperature and topP dropped
    expect(toRunOptions(workspace, onGemini)).toMatchObject({ thinkingEnabled: true, thinkingLevel: "medium", advanced: { maxOutputTokens: 100 } });
  });

  it("keeps a thinking level the new model also offers, and resets one it does not", () => {
    const high = { ...defaultSettings(workspace), thinking: { level: "high" } };
    expect(withModel(workspace, high, "gemini", "g-flash").thinking).toEqual({ level: "high" });
    expect(withModel(workspace, high, "other", "q").thinking).toEqual({ enabled: true, budget: 2000 });
    expect(withModel(workspace, high, "other", "k").thinking).toEqual({});
  });

  it("builds budget and fixed thinking requests, and clamps the output limit to the model", () => {
    const q = withModel(workspace, defaultSettings(workspace), "other", "q");
    expect(toRunOptions(workspace, q)).toMatchObject({ thinkingEnabled: true, thinkingBudget: 2000 });
    expect(toRunOptions(workspace, { ...q, thinking: { enabled: false, budget: 2000 } })).toMatchObject({ thinkingEnabled: false, thinkingBudget: null });
    expect(toRunOptions(workspace, withModel(workspace, q, "other", "k"))).toMatchObject({ thinkingEnabled: true });

    const big = { ...defaultSettings(workspace), advanced: { maxOutputTokens: 60000 } };
    expect(withModel(workspace, big, "gemini", "p").advanced.maxOutputTokens).toBe(8192);
  });

  it("sends the custom prompt only for the custom preset", () => {
    const base = withModel(workspace, defaultSettings(workspace), "other", "q");
    expect(toRunOptions(workspace, { ...base, customPrompt: "hi" }).customPrompt).toBeUndefined();
    expect(toRunOptions(workspace, { ...base, presetId: "custom", customPrompt: "hi" }).customPrompt).toBe("hi");
  });

  it("labels the thinking chip", () => {
    expect(thinkingLabel(gemini, { level: "high" })).toBe("高");
    expect(thinkingLabel(plain, {})).toBeNull();
    expect(thinkingLabel(qwen, { enabled: false })).toBe("关");
    expect(thinkingLabel(fixed, {})).toBe("始终开启");
  });
});
