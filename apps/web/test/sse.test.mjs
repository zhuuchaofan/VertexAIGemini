import test from "node:test";
import assert from "node:assert/strict";
import { readEventStream } from "../public/sse.js";

const streamOf = chunks => new ReadableStream({
  start(controller) {
    const encoder = new TextEncoder();
    for (const chunk of chunks) controller.enqueue(encoder.encode(chunk));
    controller.close();
  }
});

test("dispatches events by name with parsed JSON", async () => {
  const seen = [];
  await readEventStream(
    streamOf(['event: update\ndata: {"content":"a"}\n\n', 'event: final\ndata: {"ok":true}\n\n']),
    { update: p => seen.push(["update", p]), final: p => seen.push(["final", p]) }
  );
  assert.deepEqual(seen, [["update", { content: "a" }], ["final", { ok: true }]]);
});

test("reassembles events split across chunks", async () => {
  const seen = [];
  await readEventStream(
    streamOf(["event: upd", 'ate\ndata: {"n":', "1}\n", "\n"]),
    { update: p => seen.push(p) }
  );
  assert.deepEqual(seen, [{ n: 1 }]);
});

test("ignores unknown events and blocks without data", async () => {
  const seen = [];
  await readEventStream(
    streamOf(["event: other\ndata: {}\n\n", "event: update\n\n", ': ping\n\n']),
    { update: p => seen.push(p) }
  );
  assert.deepEqual(seen, []);
});
