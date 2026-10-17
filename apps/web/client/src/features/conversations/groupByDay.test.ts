import { describe, expect, it } from "vitest";
import type { ConversationSummary } from "../../api/types";
import { formatWhen, groupByDay } from "./groupByDay";

const at = (iso: string, id: string): ConversationSummary => ({ id, title: id, modelName: "m", updatedAt: iso });
const now = new Date(2026, 9, 17, 15, 0, 0);

describe("groupByDay", () => {
  it("groups newest-first conversations by calendar day", () => {
    const groups = groupByDay(
      [
        at(new Date(2026, 9, 17, 9).toISOString(), "a"),
        at(new Date(2026, 9, 16, 23).toISOString(), "b"),
        at(new Date(2026, 9, 12, 8).toISOString(), "c"),
        at(new Date(2026, 8, 1).toISOString(), "d"),
        at(new Date(2026, 7, 1).toISOString(), "e")
      ],
      now
    );
    expect(groups.map(g => [g.label, g.items.map(i => i.id)])).toEqual([
      ["今天", ["a"]], ["昨天", ["b"]], ["过去 7 天", ["c"]], ["更早", ["d", "e"]]
    ]);
  });

  it("describes a time relative to now", () => {
    expect(formatWhen(new Date(2026, 9, 17, 14, 2).toISOString(), now)).toBe("14:02");
    expect(formatWhen(new Date(2026, 9, 16, 21, 8).toISOString(), now)).toBe("昨天 21:08");
  });
});
