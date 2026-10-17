import { useMemo, useState } from "react";
import { CopyButton } from "../../../ui/CopyButton";
import { highlight, resolveLanguage } from "./highlight";

const COLLAPSE_AT = 24;
const NUMBERS_AT = 8;

interface Props {
  code: string;
  label?: string;
  /** While a reply is still arriving, the block is shown in full rather than collapsing under the reader. */
  streaming?: boolean;
}

export function CodeBlock({ code, label, streaming = false }: Props) {
  const [wrap, setWrap] = useState(false);
  const [expanded, setExpanded] = useState(false);

  const text = code.replace(/\n$/, "");
  const lineCount = text.split("\n").length;
  const language = resolveLanguage(label);
  const html = useMemo(() => highlight(text, language), [text, language]);
  const numbered = lineCount >= NUMBERS_AT;
  const collapsible = lineCount > COLLAPSE_AT && !streaming;
  const collapsed = collapsible && !expanded;

  return (
    <figure className="code" data-collapsed={collapsed || undefined}>
      <figcaption className="code-bar">
        <span className="code-lang">
          {label || "text"}
          {lineCount > 1 && <span className="code-count"> · {lineCount} 行</span>}
        </span>
        <span className="code-actions">
          <button type="button" className="act" aria-pressed={wrap} onClick={() => setWrap(value => !value)}>
            自动换行
          </button>
          <CopyButton text={text} />
        </span>
      </figcaption>
      <pre className={wrap ? "wrap" : undefined} tabIndex={0}>
        {numbered && (
          <span className="gutter" aria-hidden="true">
            {Array.from({ length: lineCount }, (_, index) => index + 1).join("\n")}
          </span>
        )}
        <code className="hljs" dangerouslySetInnerHTML={{ __html: html }} />
      </pre>
      {collapsible && (
        <button type="button" className="code-more" aria-expanded={expanded} onClick={() => setExpanded(value => !value)}>
          {expanded ? "收起" : `展开全部 ${lineCount} 行`}
        </button>
      )}
    </figure>
  );
}
