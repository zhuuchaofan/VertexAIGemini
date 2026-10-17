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
