import { memo } from "react";
import ReactMarkdown, { type Components } from "react-markdown";
import remarkGfm from "remark-gfm";
import { CodeBlock } from "./CodeBlock";

interface Props {
  text: string;
  streaming?: boolean;
}

function plainText(node: unknown): string {
  if (typeof node === "string") return node;
  if (Array.isArray(node)) return node.map(plainText).join("");
  return "";
}

/**
 * Model output as markdown. Raw HTML in the reply is never rendered (react-markdown escapes it) and links are
 * restricted to safe protocols by its default URL transform.
 */
function MarkdownView({ text, streaming = false }: Props) {
  const components: Components = {
    // Fenced code is rendered by CodeBlock, which owns the <pre>; react-markdown's <pre> wrapper is dropped.
    pre: ({ children }) => <>{children}</>,
    code({ className, children }) {
      const label = /language-([\w#+-]+)/.exec(className ?? "")?.[1];
      const content = plainText(children);
      if (!label && !content.includes("\n")) return <code className="inline">{children}</code>;
      return <CodeBlock code={content} label={label} streaming={streaming} />;
    },
    a: ({ href, children }) => (
      <a href={href} target="_blank" rel="noopener noreferrer">
        {children}
      </a>
    ),
    table: ({ children }) => (
      <div className="table-wrap" tabIndex={0}>
        <table>{children}</table>
      </div>
    )
  };

  return (
    <div className="md">
      <ReactMarkdown remarkPlugins={[remarkGfm]} components={components}>
        {text}
      </ReactMarkdown>
    </div>
  );
}

export const Markdown = memo(MarkdownView);
