import test from "node:test";
import assert from "node:assert/strict";
import { createStreamTracker, describeStatus, kindFromServerStatus } from "../public/stream-state.js";

const final = (over = {}) => ({
  conversationId: "c1", content: "full answer", thinkingContent: null, succeeded: true,
  errorMessage: null, citations: null, status: "completed", ...over
});

test("normal completion has no warning notice", () => {
  const t = createStreamTracker();
  t.onUpdate({ content: "ful", conversationId: "c1" });
  assert.equal(t.onFinal(final()), true);
  const outcome = t.resolve();
  assert.equal(outcome.kind, "completed");
  assert.equal(outcome.content, "full answer");
  assert.equal(describeStatus(outcome.kind), null);
});

test("a long run of updates followed by final still completes", () => {
  const t = createStreamTracker();
  for (let i = 0; i < 500; i++) t.onUpdate({ content: "x".repeat(i), conversationId: "c1" });
  t.onFinal(final({ content: "x".repeat(499) }));
  assert.equal(t.resolve().kind, "completed");
});

test("truncated output is a distinct, visible state", () => {
  const t = createStreamTracker();
  t.onFinal(final({ status: "truncated", content: "cut off" }));
  const outcome = t.resolve();
  assert.equal(outcome.kind, "truncated");
  assert.equal(outcome.content, "cut off");
  assert.match(describeStatus(outcome.kind).text, /截断/);
});

test("user stop without a final event is cancelled and keeps the partial text", () => {
  const t = createStreamTracker();
  t.onUpdate({ content: "half", conversationId: "c9" });
  assert.equal(t.requestStop(), true);
  assert.equal(t.requestStop(), false, "repeated stop is a no-op");
  const outcome = t.resolve({ error: Object.assign(new Error("aborted"), { name: "AbortError" }) });
  assert.equal(outcome.kind, "cancelled");
  assert.equal(outcome.content, "half");
  assert.equal(outcome.conversationId, "c9");
});

test("a stream that ends without final, without a stop, is interrupted - never success", () => {
  const t = createStreamTracker();
  t.onUpdate({ content: "partial", conversationId: "c2" });
  const outcome = t.resolve();
  assert.equal(outcome.kind, "interrupted");
  assert.equal(outcome.content, "partial");
  assert.ok(describeStatus(outcome.kind).text.includes("中断"));
});

test("a network error mid-stream is interrupted and carries the reason", () => {
  const t = createStreamTracker();
  t.onUpdate({ content: "abc" });
  const outcome = t.resolve({ error: new TypeError("network error") });
  assert.equal(outcome.kind, "interrupted");
  assert.equal(outcome.errorMessage, "network error");
});

test("server failure keeps partial content and shows the server message", () => {
  const t = createStreamTracker();
  t.onUpdate({ content: "part" });
  t.onFinal(final({ status: "failed", succeeded: false, content: "part", errorMessage: "AI 服务暂时不可用" }));
  const outcome = t.resolve();
  assert.equal(outcome.kind, "failed");
  assert.equal(outcome.content, "part");
  assert.equal(describeStatus(outcome.kind, outcome.errorMessage).text, "AI 服务暂时不可用");
});

test("failed final with empty content falls back to what was streamed", () => {
  const t = createStreamTracker();
  t.onUpdate({ content: "streamed" });
  t.onFinal(final({ status: "failed", succeeded: false, content: "", errorMessage: "x" }));
  assert.equal(t.resolve().content, "streamed");
});

test("duplicate final events and late updates are ignored", () => {
  const t = createStreamTracker();
  assert.equal(t.onFinal(final()), true);
  assert.equal(t.onFinal(final({ content: "other", status: "failed", succeeded: false })), false);
  t.onUpdate({ content: "late" });
  const outcome = t.resolve();
  assert.equal(outcome.kind, "completed");
  assert.equal(outcome.content, "full answer");
});

test("a final that arrives after the user pressed stop wins (server already decided)", () => {
  const t = createStreamTracker();
  t.requestStop();
  t.onFinal(final({ status: "cancelled", succeeded: false, content: "so far" }));
  assert.equal(t.resolve().kind, "cancelled");
});

test("pre-stream HTTP errors are failures, not interruptions", () => {
  const t = createStreamTracker();
  const error = Object.assign(new Error("今日请求次数已用尽"), { preStream: true });
  const outcome = t.resolve({ error });
  assert.equal(outcome.kind, "failed");
  assert.equal(outcome.errorMessage, "今日请求次数已用尽");
});

test("legacy servers without a status field map from succeeded", () => {
  assert.equal(kindFromServerStatus(undefined, true), "completed");
  assert.equal(kindFromServerStatus(undefined, false), "failed");
  assert.equal(kindFromServerStatus("bogus", true), "completed");
});
