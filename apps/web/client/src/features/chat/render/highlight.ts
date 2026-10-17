import hljs from "highlight.js/lib/core";
import bash from "highlight.js/lib/languages/bash";
import csharp from "highlight.js/lib/languages/csharp";
import css from "highlight.js/lib/languages/css";
import diff from "highlight.js/lib/languages/diff";
import go from "highlight.js/lib/languages/go";
import java from "highlight.js/lib/languages/java";
import javascript from "highlight.js/lib/languages/javascript";
import json from "highlight.js/lib/languages/json";
import markdown from "highlight.js/lib/languages/markdown";
import python from "highlight.js/lib/languages/python";
import rust from "highlight.js/lib/languages/rust";
import sql from "highlight.js/lib/languages/sql";
import typescript from "highlight.js/lib/languages/typescript";
import xml from "highlight.js/lib/languages/xml";
import yaml from "highlight.js/lib/languages/yaml";

const languages = { bash, csharp, css, diff, go, java, javascript, json, markdown, python, rust, sql, typescript, xml, yaml };
for (const [name, definition] of Object.entries(languages)) hljs.registerLanguage(name, definition);

const aliases: Record<string, string> = {
  sh: "bash", shell: "bash", zsh: "bash", console: "bash",
  cs: "csharp", "c#": "csharp",
  js: "javascript", jsx: "javascript", mjs: "javascript",
  ts: "typescript", tsx: "typescript",
  py: "python", rs: "rust", golang: "go",
  html: "xml", svg: "xml", yml: "yaml", md: "markdown", patch: "diff"
};

/** The registered name for a fence label, or null when there is nothing to highlight with. */
export function resolveLanguage(label: string | undefined): string | null {
  if (!label) return null;
  const key = label.toLowerCase();
  const name = aliases[key] ?? key;
  return hljs.getLanguage(name) ? name : null;
}

/** Highlighted HTML for the code. hljs escapes the input, so the result is safe to inject. */
export function highlight(code: string, language: string | null): string {
  if (!language) return escapeHtml(code);
  return hljs.highlight(code, { language, ignoreIllegals: true }).value;
}

export function escapeHtml(text: string): string {
  return text.replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");
}
