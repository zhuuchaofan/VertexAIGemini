import test from "node:test";
import assert from "node:assert/strict";
import { createServer } from "node:http";
import { createApiProxy } from "../proxy.mjs";

const quietLogger = { error() {} };
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

async function listen(server) {
  await new Promise(resolve => server.listen(0, "127.0.0.1", resolve));
  return `http://127.0.0.1:${server.address().port}`;
}

async function close(...servers) {
  for (const server of servers) {
    server.closeAllConnections?.();
    await new Promise(resolve => server.close(resolve));
  }
}

/** Starts a fake backend plus a proxy in front of it. */
async function setup(backendHandler, options = {}) {
  const backend = createServer(backendHandler);
  const backendUrl = await listen(backend);
  const proxy = createApiProxy({
    backendUrl,
    getIdentityToken: async () => "service-token",
    upstreamTimeoutMs: 10_000,
    logger: quietLogger,
    ...options
  });
  const front = createServer((req, res) => proxy(req, res));
  const frontUrl = await listen(front);
  return { backend, front, backendUrl, frontUrl, close: () => close(front, backend) };
}

test("forwards the request with a service token and never trusts a client-supplied one", async () => {
  let seen;
  const env = await setup((req, res) => {
    seen = { ...req.headers, url: req.url, method: req.method };
    res.writeHead(200, { "content-type": "application/json" });
    res.end('{"ok":true}');
  });
  try {
    const response = await fetch(`${env.frontUrl}/api/conversations?limit=5`, {
      headers: { authorization: "Bearer firebase", "x-serverless-authorization": "Bearer forged" }
    });
    assert.equal(response.status, 200);
    assert.deepEqual(await response.json(), { ok: true });
    assert.equal(seen.url, "/api/conversations?limit=5");
    assert.equal(seen.authorization, "Bearer firebase");
    assert.equal(seen["x-serverless-authorization"], "Bearer service-token");
  } finally {
    await env.close();
  }
});

test("streams chunks as they arrive instead of buffering the whole response", async () => {
  const env = await setup(async (req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    res.write("event: update\ndata: 1\n\n");
    await sleep(400);
    res.write("event: final\ndata: 2\n\n");
    res.end();
  });
  try {
    const started = Date.now();
    const response = await fetch(`${env.frontUrl}/api/chat/stream`, { method: "POST", body: "{}" });
    const reader = response.body.getReader();
    const first = await reader.read();
    const firstAt = Date.now() - started;
    assert.match(new TextDecoder().decode(first.value), /event: update/);
    assert.ok(firstAt < 300, `first chunk should not wait for the whole stream (took ${firstAt}ms)`);

    let rest = "";
    for (let r = await reader.read(); !r.done; r = await reader.read()) rest += new TextDecoder().decode(r.value);
    assert.match(rest, /event: final/);
  } finally {
    await env.close();
  }
});

test("a long generation with keep-alive comments completes unharmed", async () => {
  const env = await setup(async (req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    for (let i = 0; i < 6; i++) {
      res.write(": ping\n\n");
      await sleep(250);
    }
    res.write("event: final\ndata: {}\n\n");
    res.end();
  }, { upstreamTimeoutMs: 5_000 });
  try {
    const response = await fetch(`${env.frontUrl}/api/chat/stream`, { method: "POST", body: "{}" });
    const text = await response.text();
    assert.equal((text.match(/: ping/g) ?? []).length, 6);
    assert.match(text, /event: final/);
  } finally {
    await env.close();
  }
});

test("the browser disconnecting aborts the upstream request", async () => {
  let upstreamClosed;
  const closedPromise = new Promise(resolve => (upstreamClosed = resolve));
  const env = await setup((req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    res.write("event: update\ndata: 1\n\n");
    res.on("close", () => upstreamClosed(Date.now()));
    // never finishes: a generation that would run on if nobody cancelled it
  });
  try {
    const controller = new AbortController();
    const response = await fetch(`${env.frontUrl}/api/chat/stream`, {
      method: "POST", body: "{}", signal: controller.signal
    });
    await response.body.getReader().read();
    const abortedAt = Date.now();
    controller.abort();

    const closedAt = await Promise.race([closedPromise, sleep(3000).then(() => null)]);
    assert.ok(closedAt, "backend connection was not closed after the client left");
    assert.ok(closedAt - abortedAt < 2000);
  } finally {
    await env.close();
  }
});

test("an upstream failure mid-stream breaks the client connection instead of ending cleanly", async () => {
  const env = await setup((req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    res.write("event: update\ndata: partial\n\n");
    setTimeout(() => res.socket.destroy(), 100); // backend dies without a final event
  });
  try {
    const response = await fetch(`${env.frontUrl}/api/chat/stream`, { method: "POST", body: "{}" });
    const reader = response.body.getReader();
    let failure = null;
    let received = "";
    try {
      for (let r = await reader.read(); !r.done; r = await reader.read()) received += new TextDecoder().decode(r.value);
    } catch (error) {
      failure = error;
    }
    assert.match(received, /partial/);
    assert.ok(failure, "client must see an error, not a normally completed body");
  } finally {
    await env.close();
  }
});

test("the overall upstream timeout ends a runaway stream abnormally", async () => {
  const env = await setup((req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    res.write(": ping\n\n");
  }, { upstreamTimeoutMs: 400 });
  try {
    const response = await fetch(`${env.frontUrl}/api/chat/stream`, { method: "POST", body: "{}" });
    const reader = response.body.getReader();
    let failure = null;
    try {
      for (let r = await reader.read(); !r.done; r = await reader.read());
    } catch (error) {
      failure = error;
    }
    assert.ok(failure, "timeout must not look like a completed response");
  } finally {
    await env.close();
  }
});

test("a timeout before any response becomes 504", async () => {
  const env = await setup(() => { /* never answers */ }, { upstreamTimeoutMs: 300 });
  try {
    const response = await fetch(`${env.frontUrl}/api/conversations`);
    assert.equal(response.status, 504);
  } finally {
    await env.close();
  }
});

test("a missing service identity token is a 502 and nothing is forwarded", async () => {
  let called = false;
  const env = await setup((req, res) => { called = true; res.end("x"); }, { getIdentityToken: async () => null });
  try {
    const response = await fetch(`${env.frontUrl}/api/conversations`);
    assert.equal(response.status, 502);
    assert.equal(called, false);
  } finally {
    await env.close();
  }
});

test("local mode without service auth forwards without a token", async () => {
  let seen;
  const env = await setup((req, res) => { seen = req.headers; res.end("ok"); },
    { getIdentityToken: async () => null, requireIdentityToken: false });
  try {
    const response = await fetch(`${env.frontUrl}/api/workspace/config`);
    assert.equal(response.status, 200);
    assert.equal(seen["x-serverless-authorization"], undefined);
  } finally {
    await env.close();
  }
});

test("an unreachable backend is a 502 and unsupported methods are 405", async () => {
  const env = await setup((req, res) => res.end("x"));
  const frontUrl = env.frontUrl;
  await close(env.backend); // backend goes away
  try {
    const down = await fetch(`${frontUrl}/api/conversations`);
    assert.equal(down.status, 502);
    const trace = await fetch(`${frontUrl}/api/conversations`, { method: "OPTIONS" });
    assert.equal(trace.status, 405);
  } finally {
    await close(env.front);
  }
});

test("the local auth bypass is refused on Cloud Run and allowed locally", async () => {
  const { assertAuthBypassAllowed } = await import("../proxy.mjs");
  assert.throws(() => assertAuthBypassAllowed({ DISABLE_SERVERLESS_AUTH: "true", K_SERVICE: "vertex-ai-web" }), /local development only/);
  assert.throws(() => assertAuthBypassAllowed({ DISABLE_SERVERLESS_AUTH: "true", K_REVISION: "vertex-ai-web-00013" }), /Cloud Run/);
  assert.doesNotThrow(() => assertAuthBypassAllowed({ DISABLE_SERVERLESS_AUTH: "true" }));
  assert.doesNotThrow(() => assertAuthBypassAllowed({ K_SERVICE: "vertex-ai-web" }));
  assert.doesNotThrow(() => assertAuthBypassAllowed({}));
});

test("server.mjs refuses to start on Cloud Run with the bypass flag", async () => {
  const { spawnSync } = await import("node:child_process");
  const result = spawnSync("node", ["server.mjs"], {
    cwd: new URL("..", import.meta.url).pathname,
    env: { ...process.env, BACKEND_URL: "http://127.0.0.1:9", PORT: "0", DISABLE_SERVERLESS_AUTH: "true", K_SERVICE: "vertex-ai-web" },
    encoding: "utf8", timeout: 10_000
  });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /local development only/);
});

test("the ID token audience is the service URL, never a tagged revision URL", async () => {
  const { resolveBackendAudience } = await import("../proxy.mjs");
  const service = new URL("https://vertex-ai-api-123.asia-east1.run.app");
  assert.equal(resolveBackendAudience({}, service), "https://vertex-ai-api-123.asia-east1.run.app");
  assert.equal(resolveBackendAudience({}, new URL("https://vertex-ai-api-123.asia-east1.run.app/with/path")), "https://vertex-ai-api-123.asia-east1.run.app");

  const tagged = new URL("https://candidate---vertex-ai-api-123.asia-east1.run.app");
  assert.throws(() => resolveBackendAudience({}, tagged), /SERVICE url/);
  assert.equal(
    resolveBackendAudience({ BACKEND_AUDIENCE: "https://vertex-ai-api-123.asia-east1.run.app/ignored-path" }, tagged),
    "https://vertex-ai-api-123.asia-east1.run.app");
});

test("server.mjs refuses a tagged BACKEND_URL without BACKEND_AUDIENCE", async () => {
  const { spawnSync } = await import("node:child_process");
  const result = spawnSync("node", ["server.mjs"], {
    cwd: new URL("..", import.meta.url).pathname,
    env: { ...process.env, PORT: "0", BACKEND_URL: "https://candidate---vertex-ai-api-123.asia-east1.run.app", DISABLE_SERVERLESS_AUTH: "" },
    encoding: "utf8", timeout: 10_000
  });
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /tagged revision URL/);
});
