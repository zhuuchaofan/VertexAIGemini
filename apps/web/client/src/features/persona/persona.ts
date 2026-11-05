import type { UserRole, WorkspaceConfig } from "../../api/types";
import { findProvider, type ChatSettings } from "../settings/chatSettings";

/** How the assistant shows itself in a conversation: a face, a soft tint and an opening line. */
export interface Persona {
  /** An emoji, or null for the plain brand mark (the default assistant). */
  emoji: string | null;
  name: string;
  greeting: string;
  /** 0-359, used for a soft tint behind the face. */
  hue: number;
}

export const DEFAULT_GREETING = "直接输入问题。下方可以切换模型、思考深度和联网方式。";

const ANIMALS = ["🐻", "🐰", "🦊", "🐱", "🐼", "🐥", "🐹", "🐨", "🦄", "🐧"];

const KEYWORDS: Array<[RegExp, string]> = [
  [/翻译|translat/i, "🌏"],
  [/代码|编程|开发|程序|code|dev/i, "💻"],
  [/写作|文案|作文|润色|write/i, "✍️"],
  [/总结|摘要|概括|summar/i, "📝"],
  [/老师|学习|教练|辅导|tutor|teach/i, "📚"],
  [/英语|english/i, "🔤"],
  [/医|健康|health/i, "🩺"],
  [/厨|菜|美食|cook/i, "🍳"],
  [/旅|travel/i, "🧳"]
];

/** A small stable number for a name, so the same role always gets the same face and tint. */
export function hashName(name: string): number {
  let hash = 0;
  for (const ch of name) hash = (hash * 31 + ch.codePointAt(0)!) >>> 0;
  return hash;
}

export function emojiFor(name: string): string {
  const match = KEYWORDS.find(([pattern]) => pattern.test(name));
  return match ? match[1] : ANIMALS[hashName(name) % ANIMALS.length]!;
}

export function personaFor(workspace: WorkspaceConfig, settings: ChatSettings, roles: UserRole[]): Persona {
  const provider = findProvider(workspace, settings.providerId);

  if (settings.presetId === "custom") {
    const role = roles.find(item => item.prompt === settings.customPrompt);
    if (!role) return { emoji: "🫧", name: "自定义", greeting: "按你写的提示词来聊。", hue: 200 };
    return { emoji: emojiFor(role.name), name: role.name, greeting: `我是「${role.name}」，开始聊吧。`, hue: hashName(role.name) % 360 };
  }

  const preset = provider?.presets.find(item => item.id === settings.presetId);
  if (!preset || preset.id === provider?.defaultPresetId) {
    return { emoji: null, name: "球球布丁", greeting: DEFAULT_GREETING, hue: 225 };
  }
  return {
    emoji: emojiFor(preset.name),
    name: preset.name,
    greeting: preset.description || `我是「${preset.name}」，开始聊吧。`,
    hue: hashName(preset.name) % 360
  };
}

export interface Starter {
  emoji: string;
  title: string;
  /** Complete on its own, so one tap can send it. */
  prompt: string;
}

export const STARTERS: Starter[] = [
  { emoji: "🍳", title: "今晚吃什么", prompt: "帮我想想今晚吃什么，给 3 个简单易做的建议。" },
  { emoji: "✨", title: "讲个小笑话", prompt: "讲一个让人会心一笑的小笑话。" },
  { emoji: "🧠", title: "解释个概念", prompt: "用通俗易懂的方式给我讲讲什么是「复利」，再举一个生活里的例子。" },
  { emoji: "💻", title: "写个小脚本", prompt: "用 Python 写一个批量重命名文件的小脚本，并简单解释每一步。" }
];
