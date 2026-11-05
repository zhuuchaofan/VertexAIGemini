import { describe, expect, it } from "vitest";
import { entryScript, isNewer } from "./updateCheck";

const page = (hash: string) =>
  `<html><head><script type="module" crossorigin src="/app/assets/index-${hash}.js"></script><link rel="stylesheet" href="/app/assets/index-x.css"></head></html>`;

describe("entryScript", () => {
  it("finds the fingerprinted entry script", () => {
    expect(entryScript(page("AbC123"))).toBe("/app/assets/index-AbC123.js");
  });

  it("is null when the page has none", () => {
    expect(entryScript("<html></html>")).toBeNull();
  });
});

describe("isNewer", () => {
  it("is true only when both are known and differ", () => {
    expect(isNewer("/app/assets/index-a.js", "/app/assets/index-b.js")).toBe(true);
    expect(isNewer("/app/assets/index-a.js", "/app/assets/index-a.js")).toBe(false);
    expect(isNewer(null, "/app/assets/index-b.js")).toBe(false);
    expect(isNewer("/app/assets/index-a.js", null)).toBe(false);
  });
});
