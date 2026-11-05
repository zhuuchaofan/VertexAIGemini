import { describe, expect, it } from "vitest";
import { quoteText, speakableText } from "./messageText";

describe("quoteText", () => {
  it("marks every line and leaves room to reply", () => {
    expect(quoteText("一行\n\n两行")).toBe("> 一行\n>\n> 两行\n\n");
  });

  it("cuts a long message short", () => {
    const quoted = quoteText("字".repeat(1000));
    expect(quoted.length).toBeLessThan(420);
    expect(quoted).toContain("…");
  });
});

describe("speakableText", () => {
  it("drops code and marks but keeps the words", () => {
    const text = speakableText("# 标题\n\n**重点** 见 [文档](https://a.b/c)\n\n```js\nconst a = 1;\n```\n\n- 第一条");
    expect(text).toContain("标题");
    expect(text).toContain("重点 见 文档");
    expect(text).toContain("第一条");
    expect(text).not.toContain("const");
    expect(text).not.toContain("**");
    expect(text).not.toContain("#");
  });
});
