import type { Citation } from "../../../api/types";

/** Just enough of an mdast tree for the walk below; the real types are not a direct dependency. */
interface MdNode {
  type: string;
  value?: string;
  url?: string;
  children?: MdNode[];
  data?: { hProperties?: Record<string, unknown> };
}

// [1], [12], [1,2], [1, 2], [1、2]. A reference followed by "(" is a markdown link, not a citation.
const REF = /\[(\d{1,2}(?:\s*[,，、]\s*\d{1,2})*)\](?!\()/g;
const SKIP = new Set(["link", "linkReference", "code", "inlineCode", "definition", "html"]);

function usableUri(uri: string | undefined): uri is string {
  return typeof uri === "string" && /^https?:\/\//i.test(uri);
}

function badge(number: number, citation: Citation): MdNode {
  return {
    type: "link",
    url: citation.uri,
    data: { hProperties: { className: ["cite-ref"], title: citation.title || citation.uri, "aria-label": `来源 ${number}` } },
    children: [{ type: "text", value: String(number) }]
  };
}

/** The pieces a text value becomes: plain text, and a badge for every in-range number of a [n] marker. */
export function splitRefs(text: string, citations: Citation[]): MdNode[] {
  const out: MdNode[] = [];
  let last = 0;
  for (const match of text.matchAll(REF)) {
    const numbers = match[1]!.split(/[,，、]/).map(part => Number(part.trim()));
    const found = numbers.map(n => citations[n - 1]);
    // A marker with any number that has no source stays as written: it is more likely something else.
    if (found.some(item => !item || !usableUri(item.uri))) continue;
    const start = match.index ?? 0;
    if (start > last) out.push({ type: "text", value: text.slice(last, start) });
    numbers.forEach((n, position) => {
      if (position > 0) out.push({ type: "text", value: " " });
      out.push(badge(n, found[position]!));
    });
    last = start + match[0].length;
  }
  if (last === 0) return [{ type: "text", value: text }];
  if (last < text.length) out.push({ type: "text", value: text.slice(last) });
  return out;
}

function walk(node: MdNode, citations: Citation[]) {
  if (!node.children || SKIP.has(node.type)) return;
  node.children = node.children.flatMap(child => {
    if (child.type === "text" && child.value) return splitRefs(child.value, citations);
    walk(child, citations);
    return [child];
  });
}

/** Remark plugin: turns [1]-style markers in the text into links to the numbered sources. */
export function remarkCiteRefs(citations: Citation[] | undefined) {
  return (tree: MdNode) => {
    if (citations && citations.length > 0) walk(tree, citations);
  };
}
