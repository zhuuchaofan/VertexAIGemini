import { describe, expect, it } from "vitest";
import type { ModelOption, WorkspaceConfig } from "../../api/types";
import { capabilities, defaultSettings, startingSettings, thinkingLabel, toRunOptions, withModel } from "./chatSettings";

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

import { differences, fromStored, resetSetting, toStored } from "./chatSettings";

describe("stored layers", () => {
  it("reads a stored layer over the fallback, and falls back wholesale for a model that no longer exists", () => {
    const fallback = defaultSettings(workspace);
    expect(fromStored(workspace, { providerId: "gone", modelName: "x" }, fallback)).toBe(fallback);
    expect(fromStored(workspace, null, fallback)).toBe(fallback);

    const read = fromStored(workspace, {
      providerId: "other", modelName: "q", presetId: "custom", customPrompt: "be brief", searchMode: "force",
      thinking: { enabled: false, budget: 500 }, advanced: { temperature: 0.2, maxOutputTokens: 1000 }
    }, fallback);
    expect(read).toMatchObject({
      providerId: "other", modelName: "q", presetId: "custom", customPrompt: "be brief", searchMode: "force",
      thinking: { enabled: false, budget: 500 }, advanced: { temperature: 0.2, maxOutputTokens: 1000 }
    });
  });

  it("ignores stored options the model does not support and tolerates a layer from before settings existed", () => {
    const fallback = defaultSettings(workspace);
    const read = fromStored(workspace, {
      providerId: "gemini", modelName: "g-flash", searchMode: null, presetId: null,
      thinking: { level: "extreme" }, advanced: { temperature: 0.9, topP: 0.5, maxOutputTokens: 999999 }
    }, fallback);
    expect(read.searchMode).toBe("auto"); // unknown stays the default
    expect(read.thinking).toEqual({ level: "medium" }); // a level the model does not offer
    expect(read.advanced).toEqual({ maxOutputTokens: 65536 }); // no sampling on this model; the limit is clamped
  });

  it("stores only what the model supports, and keeps the prompt out of the defaults", () => {
    const settings = { ...withModel(workspace, defaultSettings(workspace), "other", "q"), presetId: "custom", customPrompt: "secret", advanced: { temperature: 0.3 } };
    const forConversation = toStored(workspace, settings, { includePrompt: true });
    expect(forConversation).toMatchObject({ providerId: "other", modelName: "q", customPrompt: "secret", thinking: { enabled: true, budget: 2000 }, advanced: { temperature: 0.3 } });
    expect(toStored(workspace, settings, { includePrompt: false }).customPrompt).toBeUndefined();

    const onGemini = toStored(workspace, { ...defaultSettings(workspace), advanced: { temperature: 0.7 } }, { includePrompt: true });
    expect(onGemini.advanced).toBeUndefined();
    expect(onGemini.thinking).toEqual({ level: "medium", enabled: true });
  });

  it("lists what differs from the defaults and puts one setting back at a time", () => {
    const defaults = defaultSettings(workspace);
    expect(differences(workspace, defaults, defaults)).toEqual([]);

    const changed = { ...defaults, searchMode: "off" as const, thinking: { level: "high" }, advanced: { maxOutputTokens: 100 } };
    expect(differences(workspace, changed, defaults)).toEqual(["thinking", "searchMode", "maxOutputTokens"]);

    const partly = resetSetting(workspace, changed, defaults, "searchMode");
    expect(partly.searchMode).toBe("auto");
    expect(partly.thinking).toEqual({ level: "high" });
    expect(resetSetting(workspace, changed, defaults, "maxOutputTokens").advanced).toEqual({});
    expect(resetSetting(workspace, changed, defaults, "thinking").thinking).toEqual({ level: "medium" });

    const otherModel = withModel(workspace, defaults, "other", "q");
    expect(differences(workspace, otherModel, defaults)).toContain("model");
    expect(resetSetting(workspace, otherModel, defaults, "model").modelName).toBe("g-flash");
  });

  it("starts a new conversation from the default role, and measures changes against that start", () => {
    const defaults = defaultSettings(workspace);
    expect(startingSettings(defaults, undefined)).toBe(defaults);
    const start = startingSettings(defaults, { prompt: "你是写作搭子" });
    expect(start).toMatchObject({ presetId: "custom", customPrompt: "你是写作搭子", modelName: defaults.modelName });
    expect(differences(workspace, start, start)).toEqual([]);
    expect(differences(workspace, { ...start, customPrompt: "别的" }, start)).toContain("preset");
    expect(resetSetting(workspace, { ...start, presetId: "default", customPrompt: "" }, start, "preset")).toMatchObject({ presetId: "custom", customPrompt: "你是写作搭子" });
  });
});
