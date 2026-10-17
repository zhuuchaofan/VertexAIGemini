import { describe, expect, it } from "vitest";
import { MAX_ATTACHMENTS, formatSize, labelOf, normalizeMimeType, selectFiles } from "./rules";

const file = (name: string, size: number, type = "") => ({ name, size, type });

describe("attachment rules", () => {
  it("fills in the type for source files the browser leaves untyped, and rejects what nobody allowed", () => {
    expect(normalizeMimeType(file("a.py", 10))).toBe("text/plain");
    expect(normalizeMimeType(file("notes.md", 10))).toBe("text/markdown");
    expect(normalizeMimeType(file("data.CSV", 10))).toBe("text/csv");
    expect(normalizeMimeType(file("x.bin", 10))).toBe("application/octet-stream");
    expect(selectFiles([file("tool.exe", 10, "application/x-msdownload")], 0).rejected[0]?.reason).toContain("不支持");
    expect(selectFiles([file("a.py", 10)], 0).accepted).toHaveLength(1);
  });

  it("holds files to their own size limit and says which one was crossed", () => {
    const result = selectFiles([file("年度报告.pdf", 600 * 1024, "application/pdf"), file("photo.png", 13 * 1024 * 1024, "image/png"), file("ok.txt", 100 * 1024, "text/plain"), file("big.png", 10 * 1024 * 1024, "image/png")], 0);
    expect(result.accepted.map(a => a.file.name)).toEqual(["ok.txt", "big.png"]); // a large image is compressed later
    expect(result.rejected).toEqual([
      { name: "年度报告.pdf", reason: "超过 512 KB（文件上限）。" },
      { name: "photo.png", reason: "超过 12.0 MB（图片原图上限）。" }
    ]);
  });

  it("refuses the files beyond the free slots, in order, instead of dropping them silently", () => {
    const files = Array.from({ length: 5 }, (_, i) => file(`f${i}.txt`, 10, "text/plain"));
    const result = selectFiles(files, MAX_ATTACHMENTS - 2);
    expect(result.accepted.map(a => a.file.name)).toEqual(["f0.txt", "f1.txt"]);
    expect(result.rejected.map(r => r.name)).toEqual(["f2.txt", "f3.txt", "f4.txt"]);
    expect(result.rejected[0]?.reason).toBe("最多 8 个附件。");
    expect(selectFiles(files, MAX_ATTACHMENTS).accepted).toHaveLength(0);
  });

  it("labels chips and formats sizes", () => {
    expect(labelOf("report.final.pdf")).toBe("PDF");
    expect(labelOf("README")).toBe("FILE");
    expect(labelOf("archive.markdown")).toBe("MARKD");
    expect(formatSize(512)).toBe("512 B");
    expect(formatSize(186 * 1024)).toBe("186 KB");
    expect(formatSize(1.5 * 1024 * 1024)).toBe("1.5 MB");
  });
});
