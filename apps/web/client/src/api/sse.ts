/**
 * Reads a server-sent event stream from a fetch response body. Handles events split across chunks, CRLF line
 * endings, multi-byte characters cut at a chunk boundary, and comment lines such as ": ping".
 */
export type SseHandlers = Record<string, (data: unknown) => void>;

export async function readEventStream(body: ReadableStream<Uint8Array>, handlers: SseHandlers): Promise<void> {
  const reader = body.getReader();
  const decoder = new TextDecoder();
  let buffer = "";

  for (;;) {
    const { value, done } = await reader.read();
    // `stream: true` keeps a multi-byte character that was cut at a chunk boundary until its other half arrives.
    buffer += done ? decoder.decode() : decoder.decode(value, { stream: true }).replace(/\r\n/g, "\n");
    buffer = buffer.replace(/\r\n/g, "\n");

    let boundary = buffer.indexOf("\n\n");
    while (boundary >= 0) {
      dispatch(buffer.slice(0, boundary), handlers);
      buffer = buffer.slice(boundary + 2);
      boundary = buffer.indexOf("\n\n");
    }

    if (done) break;
  }
}

function dispatch(rawEvent: string, handlers: SseHandlers): void {
  let name: string | undefined;
  const data: string[] = [];

  for (const line of rawEvent.split("\n")) {
    if (line.startsWith(":")) continue; // comment, e.g. keep-alive ping
    if (line.startsWith("event:")) name = line.slice(6).trim();
    else if (line.startsWith("data:")) data.push(line.slice(5).trimStart());
  }

  const handler = name ? handlers[name] : undefined;
  if (!handler || data.length === 0) return;
  handler(JSON.parse(data.join("\n")));
}
