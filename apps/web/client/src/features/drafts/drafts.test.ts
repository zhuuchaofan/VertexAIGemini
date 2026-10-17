import { beforeEach, describe, expect, it } from "vitest";
import { clearDraft, listDraftIds, loadDraft, saveDraft } from "./drafts";

beforeEach(() => localStorage.clear());

describe("drafts without IndexedDB (text only)", () => {
  it("keeps unsent text per conversation and gives it back", async () => {
    await saveDraft("c1", { text: "half a thought", files: [] });
    await saveDraft("new", { text: "another", files: [] });
    expect(await loadDraft("c1")).toEqual({ text: "half a thought", files: [] });
    expect(listDraftIds()).toEqual(new Set(["c1", "new"]));
  });

  it("a draft that becomes empty is removed, and clearing is idempotent", async () => {
    await saveDraft("c1", { text: "x", files: [] });
    await saveDraft("c1", { text: "", files: [] });
    expect(await loadDraft("c1")).toBeNull();
    expect(listDraftIds().size).toBe(0);
    await clearDraft("c1");
    await clearDraft("never-existed");
  });

  it("keeps the text when the files cannot be stored, and never claims files it did not keep", async () => {
    await saveDraft("c1", { text: "see attached", files: [{ fileName: "a.txt", mimeType: "text/plain", size: 1, base64Data: "eA==" }] });
    expect(await loadDraft("c1")).toEqual({ text: "see attached", files: [] });
    expect(JSON.parse(localStorage.getItem("draft:c1")!).files).toBe(0);
  });

  it("survives unreadable storage content", async () => {
    localStorage.setItem("draft:c1", "{not json");
    expect(await loadDraft("c1")).toBeNull();
  });
});
