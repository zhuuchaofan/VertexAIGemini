import { useState } from "react";

interface Props {
  text: string;
  /** True while the model is still thinking: open and labelled as in progress. */
  live?: boolean;
}

export function Thinking({ text, live = false }: Props) {
  const [open, setOpen] = useState(false);
  if (!text) return null;
  const shown = live || open;
  return (
    <section className="thinking" data-live={live || undefined}>
      <button type="button" className="thinking-head" aria-expanded={shown} disabled={live} onClick={() => setOpen(value => !value)}>
        <span>{live ? "思考中…" : "思考过程"}</span>
        {!live && <span className="muted">{text.length} 字 · {shown ? "收起" : "展开"}</span>}
      </button>
      {shown && <pre className="thinking-body">{text}</pre>}
    </section>
  );
}
