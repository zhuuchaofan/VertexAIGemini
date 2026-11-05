import { describe, expect, it } from "vitest";
import type { WorkspaceConfig } from "../../api/types";
import { startingSettings, type ChatSettings } from "../settings/chatSettings";
import { emojiFor, hashName, personaFor } from "./persona";

const workspace = {
  defaultProviderId: "gemini",
  providers: [
    {
      provider: { id: "gemini", name: "Gemini", description: "" },
      models: [],
      presets: [
        { id: "default", name: "默认", prompt: "", description: "通用助手" },
        { id: "code", name: "编程助手", prompt: "x", description: "帮你写代码" }
      ],
      defaultPresetId: "default",
      defaultModelName: "m"
    }
  ]
} as unknown as WorkspaceConfig;

const base = { providerId: "gemini", modelName: "m", presetId: "default", customPrompt: "" } as unknown as ChatSettings;

describe("personaFor", () => {
  it("keeps the brand mark for the default preset", () => {
    expect(personaFor(workspace, base, []).emoji).toBeNull();
  });

  it("uses the preset's name, description and a keyword face", () => {
    const persona = personaFor(workspace, { ...base, presetId: "code" }, []);
    expect(persona).toMatchObject({ emoji: "💻", name: "编程助手", greeting: "帮你写代码" });
  });

  it("finds the role whose prompt is in use", () => {
    const roles = [{ id: "r1", name: "翻译官", prompt: "translate" }];
    const persona = personaFor(workspace, startingSettings(base, roles[0]), roles);
    expect(persona).toMatchObject({ emoji: "🌏", name: "翻译官" });
  });

  it("falls back to a neutral face for a prompt that is no saved role", () => {
    const persona = personaFor(workspace, { ...base, presetId: "custom", customPrompt: "free text" }, []);
    expect(persona.name).toBe("自定义");
  });
});

describe("faces", () => {
  it("is stable for a name", () => {
    expect(emojiFor("小布丁")).toBe(emojiFor("小布丁"));
    expect(hashName("a")).toBe(hashName("a"));
  });
});
