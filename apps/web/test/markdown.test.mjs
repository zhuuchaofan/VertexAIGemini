import test from "node:test";
import assert from "node:assert/strict";
import { installFakeDom } from "./fake-dom.mjs";

installFakeDom();
const { parseMarkdown } = await import("../public/markdown.js");
const render = md => parseMarkdown(md).map(String).join("");

test("renders headings and paragraphs", () => {
  assert.equal(render("# Title\n\nHello"), "<h1>Title</h1><p>Hello</p>");
});

test("renders inline code, bold and italics", () => {
  assert.equal(
    render("a `x` **b** *c*"),
    "<p>a <code>x</code> <strong>b</strong> <em>c</em></p>"
  );
});

test("groups ordered and unordered list items", () => {
  assert.equal(render("- a\n- b"), "<ul><li>a</li><li>b</li></ul>");
  assert.equal(render("1. a\n2. b"), "<ol><li>a</li><li>b</li></ol>");
});

test("fenced code keeps raw text and language", () => {
  const html = render("```js\nconst a = '<b>';\n```");
  assert.match(html, /data-language="js"/);
  assert.match(html, /const a = '<b>';/);
});

test("allows http links and rejects javascript: links", () => {
  assert.match(render("[ok](https://example.com)"), /<a href="https:\/\/example.com\/">ok<\/a>/);
  const bad = render("[x](javascript:alert(1))");
  assert.doesNotMatch(bad, /<a /);
  assert.match(bad, /\[x\]\(javascript:alert\(1\)\)/);
});

test("never interprets raw HTML", () => {
  const nodes = parseMarkdown("<script>alert(1)</script>");
  assert.equal(nodes.length, 1);
  assert.equal(nodes[0].tag, "p");
  assert.equal(nodes[0].textContent, "<script>alert(1)</script>");
});

test("unterminated code fence is still flushed", () => {
  assert.match(render("```\nabc"), /<pre>.*abc/);
});
