import { fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { Markdown } from "./Markdown";
import { resolveLanguage } from "./highlight";

afterEach(() => vi.restoreAllMocks());

describe("Markdown", () => {
  it("renders tables, lists and inline code", () => {
    const { container } = render(<Markdown text={"| a | b |\n|---|--:|\n| 1 | 2 |\n\n- one\n- `two`"} />);
    expect(container.querySelector("table")).not.toBeNull();
    expect(container.querySelectorAll("li")).toHaveLength(2);
    expect(container.querySelector("code.inline")?.textContent).toBe("two");
  });

  it("never renders raw HTML or script-bearing links from the model", () => {
    const { container } = render(
      <Markdown text={'<img src=x onerror="alert(1)"><script>alert(2)</script>\n\n[bad](javascript:alert(3)) [ok](https://example.com)'} />
    );
    expect(container.querySelector("img")).toBeNull();
    expect(container.querySelector("script")).toBeNull();
    const links = [...container.querySelectorAll("a")];
    expect(links.map(a => a.getAttribute("href"))).not.toContain("javascript:alert(3)");
    const ok = links.find(a => a.getAttribute("href") === "https://example.com");
    expect(ok?.getAttribute("rel")).toContain("noopener");
    expect(ok?.getAttribute("target")).toBe("_blank");
  });

  it("shows a fenced block with its language, highlighted, and short ones without line numbers", () => {
    const { container } = render(<Markdown text={"```python\nx = 1\n```"} />);
    expect(screen.getByText("python")).toBeInTheDocument();
    expect(container.querySelector(".hljs-number")?.textContent).toBe("1");
    expect(container.querySelector(".gutter")).toBeNull();
  });

  it("escapes code in an unknown language instead of interpreting it", () => {
    const { container } = render(<Markdown text={"```nothing\n<b>bold</b>\n```"} />);
    expect(container.querySelector("code.hljs b")).toBeNull();
    expect(container.querySelector("code.hljs")?.textContent).toContain("<b>bold</b>");
  });

  it("collapses a long block, expands it on request, and keeps it open while a reply is arriving", () => {
    const code = Array.from({ length: 40 }, (_, i) => `line ${i}`).join("\n");
    const text = "```ts\n" + code + "\n```";
    const { container, rerender } = render(<Markdown text={text} />);
    expect(container.querySelector(".gutter")).not.toBeNull();
    const more = screen.getByRole("button", { name: "展开全部 40 行" });
    expect(container.querySelector("figure")?.hasAttribute("data-collapsed")).toBe(true);
    fireEvent.click(more);
    expect(screen.getByRole("button", { name: "收起" })).toBeInTheDocument();
    expect(container.querySelector("figure")?.hasAttribute("data-collapsed")).toBe(false);

    rerender(<Markdown text={text + "\n"} streaming />);
    expect(screen.queryByRole("button", { name: /展开全部|收起/ })).toBeNull();
  });

  it("copies the code exactly and says so", async () => {
    const writeText = vi.fn().mockResolvedValue(undefined);
    Object.defineProperty(navigator, "clipboard", { value: { writeText }, configurable: true });
    render(<Markdown text={"```sh\necho hi\n```"} />);
    fireEvent.click(screen.getByRole("button", { name: "复制" }));
    await waitFor(() => expect(writeText).toHaveBeenCalledWith("echo hi"));
    expect(await screen.findByText("已复制")).toBeInTheDocument();
  });
});

describe("resolveLanguage", () => {
  it("maps common labels and rejects unknown ones", () => {
    expect(resolveLanguage("TS")).toBe("typescript");
    expect(resolveLanguage("c#")).toBe("csharp");
    expect(resolveLanguage("sh")).toBe("bash");
    expect(resolveLanguage("klingon")).toBeNull();
    expect(resolveLanguage(undefined)).toBeNull();
  });
});
