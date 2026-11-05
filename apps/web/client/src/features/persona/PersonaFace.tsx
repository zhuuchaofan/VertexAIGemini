import type { CSSProperties } from "react";
import type { Persona } from "./persona";

/** The assistant's face: the brand mark for the default assistant, otherwise an emoji on a soft tint. */
export function PersonaFace({ persona, large = false }: { persona?: Persona; large?: boolean }) {
  const size = large ? " large" : "";
  if (!persona || persona.emoji === null) {
    return <span className={large ? "brand-mark" : "avatar brand-mark"} aria-hidden="true">球</span>;
  }
  return (
    <span className={`avatar persona-face${size}`} style={{ "--persona-h": persona.hue } as CSSProperties} aria-hidden="true">
      {persona.emoji}
    </span>
  );
}
