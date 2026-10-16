// Minimal, XSS-safe markdown renderer: builds DOM nodes, never injects HTML.
export function parseMarkdown(markdown) {
  const lines = markdown.replace(/\r\n?/g, "\n").split("\n");
  const nodes = [];
  let paragraph = [];
  let list = null;
  let codeFence = null;

  const flushParagraph = () => {
    if (paragraph.length === 0) return;
    const p = document.createElement("p");
    renderInlineMarkdown(p, paragraph.join("\n"));
    nodes.push(p);
    paragraph = [];
  };

  const flushList = () => {
    if (!list) return;
    nodes.push(list.element);
    list = null;
  };

  const flushCodeFence = () => {
    if (!codeFence) return;
    const pre = document.createElement("pre");
    const code = document.createElement("code");
    if (codeFence.language) {
      code.dataset.language = codeFence.language;
    }
    code.textContent = codeFence.lines.join("\n");
    pre.append(code);
    nodes.push(pre);
    codeFence = null;
  };

  for (const line of lines) {
    const fenceMatch = line.match(/^```([\w.+-]*)\s*$/);
    if (fenceMatch) {
      if (codeFence) {
        flushCodeFence();
      } else {
        flushParagraph();
        flushList();
        codeFence = { language: fenceMatch[1], lines: [] };
      }
      continue;
    }

    if (codeFence) {
      codeFence.lines.push(line);
      continue;
    }

    if (!line.trim()) {
      flushParagraph();
      flushList();
      continue;
    }

    const headingMatch = line.match(/^(#{1,6})\s+(.+)$/);
    if (headingMatch) {
      flushParagraph();
      flushList();
      const heading = document.createElement(`h${headingMatch[1].length}`);
      renderInlineMarkdown(heading, headingMatch[2].trim());
      nodes.push(heading);
      continue;
    }

    const quoteMatch = line.match(/^>\s?(.+)$/);
    if (quoteMatch) {
      flushParagraph();
      flushList();
      const blockquote = document.createElement("blockquote");
      renderInlineMarkdown(blockquote, quoteMatch[1]);
      nodes.push(blockquote);
      continue;
    }

    const listMatch = line.match(/^(\s*)([-*+]|\d+[.)])\s+(.+)$/);
    if (listMatch) {
      flushParagraph();
      const ordered = /\d/.test(listMatch[2][0]);
      if (!list || list.ordered !== ordered) {
        flushList();
        list = {
          ordered,
          element: document.createElement(ordered ? "ol" : "ul")
        };
      }
      const item = document.createElement("li");
      renderInlineMarkdown(item, listMatch[3]);
      list.element.append(item);
      continue;
    }

    paragraph.push(line);
  }

  flushCodeFence();
  flushParagraph();
  flushList();

  if (nodes.length === 0) {
    return [document.createTextNode(markdown)];
  }

  return nodes;
}

function renderInlineMarkdown(container, text) {
  const tokenPattern = /(`[^`]+`|\*\*[^*]+\*\*|__[^_]+__|\*[^*\n]+\*|_[^_\n]+_|\[([^\]\n]+)\]\(([^)\s]+)\))/g;
  let cursor = 0;
  let match;

  while ((match = tokenPattern.exec(text)) !== null) {
    if (match.index > cursor) {
      appendTextWithBreaks(container, text.slice(cursor, match.index));
    }

    const token = match[0];
    if (token.startsWith("`")) {
      const code = document.createElement("code");
      code.textContent = token.slice(1, -1);
      container.append(code);
    } else if (token.startsWith("**") || token.startsWith("__")) {
      const strong = document.createElement("strong");
      renderInlineMarkdown(strong, token.slice(2, -2));
      container.append(strong);
    } else if (token.startsWith("*") || token.startsWith("_")) {
      const em = document.createElement("em");
      renderInlineMarkdown(em, token.slice(1, -1));
      container.append(em);
    } else {
      const link = createSafeLink(match[2], match[3]);
      container.append(link ?? document.createTextNode(token));
    }

    cursor = tokenPattern.lastIndex;
  }

  if (cursor < text.length) {
    appendTextWithBreaks(container, text.slice(cursor));
  }
}

function appendTextWithBreaks(container, text) {
  const parts = text.split("\n");
  parts.forEach((part, index) => {
    if (index > 0) {
      container.append(document.createElement("br"));
    }
    if (part) {
      container.append(document.createTextNode(part));
    }
  });
}

function createSafeLink(label, href) {
  try {
    const url = new URL(href, window.location.href);
    if (!["http:", "https:", "mailto:", "tel:"].includes(url.protocol)) {
      return null;
    }

    const link = document.createElement("a");
    link.href = url.href;
    link.target = "_blank";
    link.rel = "noreferrer";
    link.textContent = label;
    return link;
  } catch {
    return null;
  }
}

