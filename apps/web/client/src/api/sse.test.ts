import { describe, expect, test } from "vitest";
import { readEventStream } from "./sse";

function streamOf(chunks: Array<string | Uint8Array>): ReadableStream<Uint8Array> {
  const encoder = new TextEncoder();
  return new ReadableStream({
    start(controller) {
      for (const chunk of chunks) controller.enqueue(typeof chunk === "string" ? encoder.encode(chunk) : chunk);
      controller.close();
    }
  });
}

async function collect(chunks: Array<string | Uint8Array>): Promise<Array<[string, unknown]>> {
  const seen: Array<[string, unknown]> = [];
  await readEventStream(streamOf(chunks), {
    run: data => seen.push(["run", data]),
    delta: data => seen.push(["delta", data]),
    done: data => seen.push(["done", data])
  });
  return seen;
}

describe("readEventStream", () => {
  test("dispatches events by name with parsed JSON", async () => {
    expect(await collect(['event: run\ndata: {"a":1}\n\nevent: done\ndata: {"b":2}\n\n'])).toEqual([
      ["run", { a: 1 }],
      ["done", { b: 2 }]
    ]);
  });

  test("handles an event split across chunks at any byte", async () => {
    const whole = 'event: delta\ndata: {"offset":0,"content":"hello"}\n\n';
    for (let cut = 1; cut < whole.length; cut += 1) {
      expect(await collect([whole.slice(0, cut), whole.slice(cut)])).toEqual([["delta", { offset: 0, content: "hello" }]]);
    }
  });

  test("handles CRLF line endings, including a CR LF pair split between chunks", async () => {
    expect(await collect(['event: run\r\ndata: {"ok":true}\r', "\n\r\n"])).toEqual([["run", { ok: true }]]);
  });

  test("keeps a multi-byte character that is cut in the middle", async () => {
    const bytes = new TextEncoder().encode('event: delta\ndata: {"offset":0,"content":"球球布丁"}\n\n');
    const middle = bytes.indexOf(0xe7) + 1; // inside the first three-byte character
    expect(await collect([bytes.slice(0, middle), bytes.slice(middle)])).toEqual([
      ["delta", { offset: 0, content: "球球布丁" }]
    ]);
  });

  test("ignores keep-alive comments and unknown events", async () => {
    expect(await collect([': ping\n\n', 'event: other\ndata: {"x":1}\n\n', 'event: run\ndata: {"ok":1}\n\n'])).toEqual([
      ["run", { ok: 1 }]
    ]);
  });

  test("joins multi-line data fields", async () => {
    expect(await collect(['event: run\ndata: {"a":\ndata: 1}\n\n'])).toEqual([["run", { a: 1 }]]);
  });

  test("drops a trailing event that never completed", async () => {
    expect(await collect(['event: run\ndata: {"a":1}\n\nevent: done\ndata: {"b"'])).toEqual([["run", { a: 1 }]]);
  });
});
