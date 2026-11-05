import { useCallback, useEffect, useState } from "react";

export type ThemeChoice = "system" | "light" | "dark";
const KEY = "ui:theme";

function stored(): ThemeChoice {
  try {
    const value = localStorage.getItem(KEY);
    return value === "light" || value === "dark" ? value : "system";
  } catch {
    return "system";
  }
}

function apply(choice: ThemeChoice) {
  const root = document.documentElement;
  if (choice === "system") root.removeAttribute("data-theme");
  else root.setAttribute("data-theme", choice);
}

/** Follows the system by default; a choice is remembered on this device only. */
export function useTheme() {
  const [choice, setChoice] = useState<ThemeChoice>(stored);

  useEffect(() => {
    apply(choice);
  }, [choice]);

  const update = useCallback((next: ThemeChoice) => {
    setChoice(next);
    try {
      if (next === "system") localStorage.removeItem(KEY);
      else localStorage.setItem(KEY, next);
    } catch {
      // not remembered
    }
  }, []);

  const cycle = useCallback(() => update(choice === "system" ? "light" : choice === "light" ? "dark" : "system"), [choice, update]);
  return { choice, set: update, cycle };
}

export const themeLabels: Record<ThemeChoice, string> = { system: "跟随系统", light: "浅色", dark: "深色" };

export type Accent = "blue" | "pudding" | "mint" | "sakura" | "lavender";
export const ACCENTS: Array<{ id: Accent; label: string; swatch: string; bar: string }> = [
  { id: "blue", label: "布丁蓝", swatch: "#2A4BD0", bar: "#2A4BD0" },
  { id: "pudding", label: "奶黄", swatch: "#E9A93B", bar: "#A86400" },
  { id: "mint", label: "薄荷", swatch: "#3DBB94", bar: "#0F7B5F" },
  { id: "sakura", label: "樱粉", swatch: "#EE6E9F", bar: "#C2306F" },
  { id: "lavender", label: "薰衣草", swatch: "#8F78E8", bar: "#6B4FCF" }
];
const ACCENT_KEY = "ui:accent";

function storedAccent(): Accent {
  try {
    const value = localStorage.getItem(ACCENT_KEY);
    return ACCENTS.some(item => item.id === value) ? (value as Accent) : "blue";
  } catch {
    return "blue";
  }
}

function applyAccent(accent: Accent) {
  const root = document.documentElement;
  if (accent === "blue") root.removeAttribute("data-accent");
  else root.setAttribute("data-accent", accent);
  // The browser's own bar follows the accent too.
  document.querySelector('meta[name="theme-color"]')?.setAttribute("content", ACCENTS.find(item => item.id === accent)!.bar);
}

/** The accent colour: remembered on this device only, applied as `data-accent` on the page. */
export function useAccent() {
  const [accent, setAccent] = useState<Accent>(storedAccent);

  useEffect(() => {
    applyAccent(accent);
  }, [accent]);

  const choose = useCallback((next: Accent) => {
    setAccent(next);
    try {
      if (next === "blue") localStorage.removeItem(ACCENT_KEY);
      else localStorage.setItem(ACCENT_KEY, next);
    } catch {
      // not remembered
    }
  }, []);

  return { accent, choose };
}
