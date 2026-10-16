// Server-sent-event stream reader for POST fetch responses.
export async function readEventStream(body, handlers) {
  const reader = body.pipeThrough(new TextDecoderStream()).getReader();
  let buffer = "";

  while (true) {
    const { value, done } = await reader.read();
    if (done) break;

    buffer += value;
    let boundary = buffer.indexOf("\n\n");
    while (boundary >= 0) {
      const rawEvent = buffer.slice(0, boundary);
      buffer = buffer.slice(boundary + 2);
      dispatchEventBlock(rawEvent, handlers);
      boundary = buffer.indexOf("\n\n");
    }
  }
}

function dispatchEventBlock(rawEvent, handlers) {
  const lines = rawEvent.split("\n");
  const eventName = lines.find(line => line.startsWith("event:"))?.slice(6).trim();
  const data = lines
    .filter(line => line.startsWith("data:"))
    .map(line => line.slice(5).trimStart())
    .join("\n");

  if (!eventName || !data || !handlers[eventName]) {
    return;
  }

  handlers[eventName](JSON.parse(data));
}

