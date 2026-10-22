import type {
  AdvancedOptions,
  ModelOption,
  RunOptions,
  SearchMode,
  StoredSettings,
  WorkspaceConfig,
  WorkspaceProvider
} from "../../api/types";

/** The settings the composer controls, as the person chose them. */
export interface ChatSettings {
  providerId: string;
  modelName: string;
  presetId: string;
  customPrompt: string;
  searchMode: SearchMode;
  /** The thinking choice for the current model: a level value, "on"/"off", or a budget. */
  thinking: ThinkingChoice;
  advanced: AdvancedOptions;
}

export interface ThinkingChoice {
  level?: string;
  enabled?: boolean;
  budget?: number;
}

export function findProvider(workspace: WorkspaceConfig, providerId: string): WorkspaceProvider | undefined {
  return workspace.providers.find(p => p.provider.id.toLowerCase() === providerId.toLowerCase());
}

export function findModel(workspace: WorkspaceConfig, providerId: string, modelName: string): ModelOption | undefined {
  return findProvider(workspace, providerId)?.models.find(m => m.modelName === modelName);
}

/** The model's own default thinking choice. */
export function defaultThinking(model: ModelOption | undefined): ThinkingChoice {
  const thinking = model?.thinking;
  if (!thinking) return {};
  if (thinking.kind === "qwen-budget") {
    return { enabled: thinking.default !== "off", budget: thinking.defaultBudget ?? thinking.budgets[0] };
  }
  if (thinking.options.length === 0) return {};
  const fallback = thinking.options[0]?.value;
  return { level: thinking.default ?? fallback };
}

export function defaultSettings(workspace: WorkspaceConfig): ChatSettings {
  const provider = findProvider(workspace, workspace.defaultProviderId) ?? workspace.providers[0];
  const model = provider?.models.find(m => m.modelName === provider.defaultModelName) ?? provider?.models[0];
  return {
    providerId: provider?.provider.id ?? workspace.defaultProviderId,
    modelName: model?.modelName ?? "",
    presetId: provider?.defaultPresetId ?? "default",
    customPrompt: "",
    searchMode: "auto",
    thinking: defaultThinking(model),
    advanced: {}
  };
}

/** What a model lets the person change. Everything else is hidden, not disabled. */
export interface Capabilities {
  thinkingChoices: { value: string; label: string }[];
  thinkingKind: "none" | "level" | "toggle-budget" | "fixed";
  canDisableThinking: boolean;
  sampling: boolean;
  maxOutputTokens: number;
}

export function capabilities(model: ModelOption | undefined): Capabilities {
  const thinking = model?.thinking ?? null;
  const base = { sampling: model?.supportsSampling ?? true, maxOutputTokens: Math.min(model?.maxTokens ?? 65536, 65536) };
  if (!thinking) return { ...base, thinkingChoices: [], thinkingKind: "none", canDisableThinking: false };
  if (thinking.kind === "qwen-budget") {
    return { ...base, thinkingChoices: [], thinkingKind: "toggle-budget", canDisableThinking: thinking.canDisable };
  }
  if (thinking.fixedEnabled && thinking.options.length === 0 && thinking.budgets.length === 0) {
    return { ...base, thinkingChoices: [], thinkingKind: "fixed", canDisableThinking: false };
  }
  const choices = thinking.options.length > 0 ? thinking.options : [{ value: "off", label: "关闭" }, { value: "on", label: "开启" }];
  return { ...base, thinkingChoices: choices, thinkingKind: "level", canDisableThinking: choices.some(c => c.value === "off") };
}

/**
 * Switching model keeps what still applies and resets what does not: a level the new model does not offer falls
 * back to its default, and sampling controls disappear for a model that ignores them.
 */
export function withModel(workspace: WorkspaceConfig, settings: ChatSettings, providerId: string, modelName: string): ChatSettings {
  const model = findModel(workspace, providerId, modelName);
  const caps = capabilities(model);
  const defaults = defaultThinking(model);
  let thinking = defaults;
  if (caps.thinkingKind === "level" && settings.thinking.level && caps.thinkingChoices.some(c => c.value === settings.thinking.level)) {
    thinking = { level: settings.thinking.level };
  }
  if (caps.thinkingKind === "toggle-budget" && settings.thinking.budget !== undefined) {
    thinking = { enabled: settings.thinking.enabled ?? true, budget: settings.thinking.budget };
  }

  const advanced: AdvancedOptions = { ...settings.advanced };
  if (!caps.sampling) {
    delete advanced.temperature;
    delete advanced.topP;
  }
  if (advanced.maxOutputTokens !== undefined && advanced.maxOutputTokens > caps.maxOutputTokens) {
    advanced.maxOutputTokens = caps.maxOutputTokens;
  }

  const provider = findProvider(workspace, providerId);
  const presetId = provider?.presets.some(p => p.id === settings.presetId) ? settings.presetId : (provider?.defaultPresetId ?? "default");
  return { ...settings, providerId, modelName, presetId, thinking, advanced };
}

/** The request fields for one run. Nothing the model does not support is included. */
export function toRunOptions(workspace: WorkspaceConfig, settings: ChatSettings): RunOptions {
  const model = findModel(workspace, settings.providerId, settings.modelName);
  const caps = capabilities(model);
  const options: RunOptions = {
    providerId: settings.providerId,
    modelName: settings.modelName,
    presetId: settings.presetId,
    searchMode: settings.searchMode
  };
  if (settings.presetId === "custom") options.customPrompt = settings.customPrompt;

  if (caps.thinkingKind === "level" && settings.thinking.level) {
    options.thinkingEnabled = settings.thinking.level !== "off";
    options.thinkingLevel = settings.thinking.level;
  } else if (caps.thinkingKind === "toggle-budget") {
    const enabled = settings.thinking.enabled ?? true;
    options.thinkingEnabled = enabled;
    options.thinkingBudget = enabled ? (settings.thinking.budget ?? null) : null;
  } else if (caps.thinkingKind === "fixed") {
    options.thinkingEnabled = true;
  }

  const advanced: AdvancedOptions = {};
  if (caps.sampling && settings.advanced.temperature !== undefined) advanced.temperature = settings.advanced.temperature;
  if (caps.sampling && settings.advanced.topP !== undefined) advanced.topP = settings.advanced.topP;
  if (settings.advanced.maxOutputTokens !== undefined) advanced.maxOutputTokens = settings.advanced.maxOutputTokens;
  if (settings.advanced.safetyThreshold) advanced.safetyThreshold = settings.advanced.safetyThreshold;
  if (Object.keys(advanced).length > 0) options.advanced = advanced;
  return options;
}

/** A short label for the thinking chip, for example "中", "开" or "关". */
export function thinkingLabel(model: ModelOption | undefined, choice: ThinkingChoice): string | null {
  const caps = capabilities(model);
  if (caps.thinkingKind === "none") return null;
  if (caps.thinkingKind === "fixed") return "始终开启";
  if (caps.thinkingKind === "toggle-budget") return choice.enabled === false ? "关" : "开";
  return caps.thinkingChoices.find(c => c.value === choice.level)?.label ?? choice.level ?? null;
}

export const searchLabels: Record<SearchMode, string> = { off: "关闭", auto: "自动", force: "强制查证" };

// ---- conversion between what the server stores and what the composer works with ----


function clean<T extends object>(value: T): T | undefined {
  const entries = Object.entries(value).filter(([, v]) => v !== undefined && v !== null && v !== "");
  return entries.length > 0 ? (Object.fromEntries(entries) as T) : undefined;
}

/**
 * Reads a stored layer on top of a fallback. A layer written by an older version, or one naming a model that no
 * longer exists, never breaks the page: whatever is missing or unknown comes from the fallback.
 */
export function fromStored(workspace: WorkspaceConfig, stored: StoredSettings | null | undefined, fallback: ChatSettings): ChatSettings {
  if (!stored || !findModel(workspace, stored.providerId, stored.modelName)) return fallback;
  const model = findModel(workspace, stored.providerId, stored.modelName);
  const provider = findProvider(workspace, stored.providerId);
  const base = withModel(workspace, fallback, provider?.provider.id ?? stored.providerId, stored.modelName);

  const thinking: ThinkingChoice = {
    ...(stored.thinking?.level ? { level: stored.thinking.level } : {}),
    ...(stored.thinking?.enabled != null ? { enabled: stored.thinking.enabled } : {}),
    ...(stored.thinking?.budget != null ? { budget: stored.thinking.budget } : {})
  };
  const caps = capabilities(model);
  const usableThinking =
    caps.thinkingKind === "level" ? (caps.thinkingChoices.some(c => c.value === thinking.level) ? { level: thinking.level } : base.thinking)
    : caps.thinkingKind === "toggle-budget" ? { ...base.thinking, ...thinking }
    : base.thinking;

  const presetKnown = provider?.presets.some(p => p.id === stored.presetId) ?? false;
  return {
    ...base,
    presetId: presetKnown && stored.presetId ? stored.presetId : base.presetId,
    customPrompt: stored.customPrompt ?? "",
    searchMode: stored.searchMode === "off" || stored.searchMode === "auto" || stored.searchMode === "force" ? stored.searchMode : base.searchMode,
    thinking: usableThinking,
    advanced: {
      ...(caps.sampling && stored.advanced?.temperature != null ? { temperature: stored.advanced.temperature } : {}),
      ...(caps.sampling && stored.advanced?.topP != null ? { topP: stored.advanced.topP } : {}),
      ...(stored.advanced?.maxOutputTokens != null ? { maxOutputTokens: Math.min(stored.advanced.maxOutputTokens, caps.maxOutputTokens) } : {}),
      ...(stored.advanced?.safetyThreshold ? { safetyThreshold: stored.advanced.safetyThreshold } : {})
    }
  };
}

/** What the server is sent to store. Nothing the model does not support is included; "my defaults" carry no prompt text. */
export function toStored(workspace: WorkspaceConfig, settings: ChatSettings, options: { includePrompt: boolean }): StoredSettings {
  const run = toRunOptions(workspace, settings);
  const thinking = clean({
    level: run.thinkingLevel ?? undefined,
    enabled: run.thinkingEnabled ?? undefined,
    budget: run.thinkingBudget ?? undefined
  });
  return {
    providerId: settings.providerId,
    modelName: settings.modelName,
    presetId: settings.presetId,
    searchMode: settings.searchMode,
    ...(options.includePrompt && settings.presetId === "custom" ? { customPrompt: settings.customPrompt } : {}),
    ...(thinking ? { thinking } : {}),
    ...(run.advanced ? { advanced: run.advanced } : {})
  };
}

export type SettingKey = "model" | "thinking" | "searchMode" | "preset" | "temperature" | "topP" | "maxOutputTokens" | "safetyThreshold";

/** Which settings differ from the defaults, so the panel can say so and offer to put each one back. */
export function differences(workspace: WorkspaceConfig, settings: ChatSettings, defaults: ChatSettings): SettingKey[] {
  const keys: SettingKey[] = [];
  const same = (a: unknown, b: unknown) => JSON.stringify(a ?? null) === JSON.stringify(b ?? null);
  if (settings.providerId !== defaults.providerId || settings.modelName !== defaults.modelName) keys.push("model");
  const mine = toRunOptions(workspace, settings);
  const theirs = toRunOptions(workspace, withModel(workspace, defaults, settings.providerId, settings.modelName));
  if (!same([mine.thinkingEnabled, mine.thinkingLevel, mine.thinkingBudget], [theirs.thinkingEnabled, theirs.thinkingLevel, theirs.thinkingBudget])) keys.push("thinking");
  if (settings.searchMode !== defaults.searchMode) keys.push("searchMode");
  if (settings.presetId !== defaults.presetId || (settings.presetId === "custom" && settings.customPrompt !== defaults.customPrompt)) keys.push("preset");
  if (!same(settings.advanced.temperature, defaults.advanced.temperature)) keys.push("temperature");
  if (!same(settings.advanced.topP, defaults.advanced.topP)) keys.push("topP");
  if (!same(settings.advanced.maxOutputTokens, defaults.advanced.maxOutputTokens)) keys.push("maxOutputTokens");
  if (!same(settings.advanced.safetyThreshold, defaults.advanced.safetyThreshold)) keys.push("safetyThreshold");
  return keys;
}

/** Puts one setting back to the defaults' value, leaving the rest alone. */
export function resetSetting(workspace: WorkspaceConfig, settings: ChatSettings, defaults: ChatSettings, key: SettingKey): ChatSettings {
  switch (key) {
    case "model":
      return withModel(workspace, settings, defaults.providerId, defaults.modelName);
    case "thinking":
      return { ...settings, thinking: withModel(workspace, defaults, settings.providerId, settings.modelName).thinking };
    case "searchMode":
      return { ...settings, searchMode: defaults.searchMode };
    case "preset":
      return { ...settings, presetId: defaults.presetId, customPrompt: defaults.presetId === "custom" ? defaults.customPrompt : "" };
    default: {
      const advanced = { ...settings.advanced };
      if (defaults.advanced[key] === undefined) delete advanced[key];
      else (advanced as Record<string, unknown>)[key] = defaults.advanced[key];
      return { ...settings, advanced };
    }
  }
}

/** What a new conversation starts with: my defaults, with the default role's prompt in place if one is set. */
export function startingSettings(defaults: ChatSettings, role: { prompt: string } | undefined): ChatSettings {
  return role ? { ...defaults, presetId: "custom", customPrompt: role.prompt } : defaults;
}
