import { describe, expect, it } from "vitest";
import type { QuotaStatus } from "../../api/types";
import { describeReset, formatBytes, formatCount, summarize } from "./quota";

const status = (overrides: Partial<QuotaStatus> = {}): QuotaStatus => ({
  limited: true,
  date: "20260621",
  resetsAtUtc: "2026-06-22T00:00:00Z",
  requests: { used: 10, limit: 100 },
  tokens: { used: 50_000, limit: 200_000 },
  searches: { used: 3, limit: 30 },
  attachmentBytes: { used: 0, limit: 26_214_400 },
  ...overrides
});

describe("summarize", () => {
  it("follows the allowance that will run out first", () => {
    const summary = summarize(status())!;
    expect(summary.percent).toBe(25);
    expect(summary.fullest.key).toBe("tokens");
    expect(summary.level).toBe("ok");
  });

  it("warns from 80 percent and is full at 100", () => {
    expect(summarize(status({ requests: { used: 80, limit: 100 } }))!.level).toBe("warn");
    const full = summarize(status({ searches: { used: 31, limit: 30 } }))!;
    expect(full.level).toBe("full");
    expect(full.percent).toBe(100);
  });

  it("shows nothing for people the quota does not apply to, or before it is known", () => {
    expect(summarize(status({ limited: false }))).toBeNull();
    expect(summarize(null)).toBeNull();
  });

  it("does not divide by a missing limit", () => {
    expect(summarize(status({ requests: { used: 5, limit: 0 } }))!.lines[0]!.ratio).toBe(0);
  });
});

describe("formatting", () => {
  it("shortens large counts and sizes", () => {
    expect(formatCount(950)).toBe("950");
    expect(formatCount(12_400)).toBe("12.4K");
    expect(formatCount(200_000)).toBe("200K");
    expect(formatBytes(26_214_400)).toBe("25 MB");
    expect(formatBytes(2048)).toBe("2 KB");
  });
});

describe("describeReset", () => {
  it("says today or tomorrow by the viewer's own date", () => {
    const now = new Date(2026, 5, 21, 9, 0);
    expect(describeReset(new Date(2026, 5, 21, 20, 0), now)).toBe("今天 20:00");
    expect(describeReset(new Date(2026, 5, 22, 8, 0), now)).toBe("明天 08:00");
  });
});
