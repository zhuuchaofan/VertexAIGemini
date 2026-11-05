const QUOTE_LIMIT = 400;

/** The text of a message as a quotation to start a reply with: each line marked, long text cut short. */
export function quoteText(text: string): string {
  const trimmed = text.trim();
  const cut = trimmed.length > QUOTE_LIMIT ? `${trimmed.slice(0, QUOTE_LIMIT).trimEnd()}…` : trimmed;
  return `${cut.split("\n").map(line => `> ${line}`.trimEnd()).join("\n")}\n\n`;
}

/** Markdown reduced to what should be read aloud: no code blocks, links as their words, no marks. */
export function speakableText(markdown: string): string {
  return markdown
    .replace(/```[\s\S]*?```/g, " 代码省略。 ")
    .replace(/!\[[^\]]*\]\([^)]*\)/g, "")
    .replace(/\[([^\]]+)\]\([^)]*\)/g, "$1")
    .replace(/https?:\/\/\S+/g, "链接")
    .replace(/^\s{0,3}(#{1,6}|>|[-*+]|\d+\.)\s+/gm, "")
    .replace(/[`*_~|]/g, "")
    .replace(/\n{2,}/g, "\n")
    .trim();
}
