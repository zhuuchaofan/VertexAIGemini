import { useEffect, useRef, useState } from "react";
import { copyText } from "./clipboard";

interface Props {
  text: string | (() => string);
  label?: string;
  className?: string;
}

/** Copies on click and says so for a moment. The label change is announced, not only coloured. */
export function CopyButton({ text, label = "复制", className = "act" }: Props) {
  const [state, setState] = useState<"idle" | "copied" | "failed">("idle");
  const timer = useRef<ReturnType<typeof setTimeout> | undefined>(undefined);
  useEffect(() => () => clearTimeout(timer.current), []);

  return (
    <button
      type="button"
      className={className}
      onClick={async () => {
        const ok = await copyText(typeof text === "function" ? text() : text);
        setState(ok ? "copied" : "failed");
        clearTimeout(timer.current);
        timer.current = setTimeout(() => setState("idle"), 1600);
      }}
    >
      <span aria-live="polite">{state === "copied" ? "已复制" : state === "failed" ? "复制失败" : label}</span>
    </button>
  );
}
