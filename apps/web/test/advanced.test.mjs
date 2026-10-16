import { test } from "node:test";
import assert from "node:assert/strict";
import { getAdvancedPayload } from "../public/advanced.js";

test("blank advanced controls preserve server defaults", () => {
  assert.deepEqual(getAdvancedPayload({
    temperature: "", topP: "", maxOutputTokens: "", safetyThreshold: ""
  }), {});
});

test("advanced controls serialize actual selected values", () => {
  assert.deepEqual(getAdvancedPayload({
    temperature: "0", topP: "0.9", maxOutputTokens: "4096", safetyThreshold: "OFF"
  }), { temperature: 0, topP: 0.9, maxOutputTokens: 4096, safetyThreshold: "OFF" });
});

test("invalid generation settings cannot be sent", () => {
  for (const values of [
    { temperature: "NaN" }, { temperature: "2.1" }, { topP: "0" },
    { topP: "1.1" }, { maxOutputTokens: "1.5" }, { maxOutputTokens: "65537" }
  ]) assert.throws(() => getAdvancedPayload(values));
});

test("models without sampling omit stale temperature and Top-P preferences", () => {
  assert.deepEqual(getAdvancedPayload({
    temperature: "1.5", topP: "0.5", maxOutputTokens: "8192"
  }, { supportsSampling: false }), { maxOutputTokens: 8192 });
});
