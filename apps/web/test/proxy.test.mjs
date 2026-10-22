import test from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import net from "node:net";
import { spawn } from "node:child_process";
import { once } from "node:events";
import { mkdtemp, mkdir, writeFile, rm } from "node:fs/promises";
import { tmpdir } from "node:os";
import { join as joinPath } from "node:path";
import { fileURLToPath } from "node:url";

const serverPath = fileURLToPath(new URL("../server.mjs", import.meta.url));

async function freePort() {
  const probe = net.createServer();
  probe.listen(0, "127.0.0.1");
  await once(probe, "listening");
  const { port } = probe.address();
  probe.close();
  await once(probe, "close");
  return port;
}

// Fake API upstream. `onRequest` decides how to behave; every request records when it was closed by the peer.
async function startUpstream(onRequest) {
  const seen = [];
  const server = http.createServer((req, res) => {
    const record = { url: req.url, method: req.method, headers: req.headers, closedAt: null, startedAt: Date.now() };
    seen.push(record);
    res.on("close", () => { record.closedAt ??= Date.now(); });
    req.on("close", () => { if (!res.writableFinished) record.closedAt ??= Date.now(); });
    onRequest(req, res, record);
  });
  server.listen(0, "127.0.0.1");
  await once(server, "listening");
  return { server, seen, port: server.address().port };
}

async function startProxy(upstreamPort) {
  const port = await freePort();
  const child = spawn(process.execPath, [serverPath], {
    env: {
      ...process.env,
      PORT: String(port),
      HOST: "127.0.0.1",
      BACKEND_URL: `http://127.0.0.1:${upstreamPort}`,
      DISABLE_SERVERLESS_AUTH: "true"
    },
    stdio: ["ignore", "pipe", "pipe"]
  });
  let output = "";
  child.stdout.on("data", chunk => { output += chunk; });
  child.stderr.on("data", chunk => { output += chunk; });
  const deadline = Date.now() + 10_000;
  while (!output.includes("listening")) {
    if (child.exitCode !== null) throw new Error(`proxy exited early: ${output}`);
    if (Date.now() > deadline) throw new Error(`proxy did not start: ${output}`);
    await new Promise(resolve => setTimeout(resolve, 25));
  }
  return { port, child, logs: () => output };
}

async function waitFor(predicate, timeoutMs, label) {
  const deadline = Date.now() + timeoutMs;
  while (Date.now() < deadline) {
    if (predicate()) return;
    await new Promise(resolve => setTimeout(resolve, 20));
  }
  assert.fail(`timed out after ${timeoutMs} ms waiting for: ${label}`);
}

async function withProxy(onRequest, body) {
  const upstream = await startUpstream(onRequest);
  const proxy = await startProxy(upstream.port);
  try {
    await body({ upstream, proxy });
  } finally {
    proxy.child.kill();
    upstream.server.closeAllConnections();
    upstream.server.close();
  }
}

const sseUntilClosed = (req, res) => {
  res.writeHead(200, { "content-type": "text/event-stream" });
  const timer = setInterval(() => res.write("event: update\ndata: {}\n\n"), 30);
  res.on("close", () => clearInterval(timer));
};

test("client disconnect during an SSE stream is propagated to the upstream within 1s", async () => {
  await withProxy(sseUntilClosed, async ({ upstream, proxy }) => {
    const req = http.request({ host: "127.0.0.1", port: proxy.port, path: "/api/v2/runs", method: "POST" });
    req.end("{}");
    const [res] = await once(req, "response");
    await once(res, "data");

    const disconnectedAt = Date.now();
    req.destroy();
    res.destroy();

    await waitFor(() => upstream.seen[0]?.closedAt, 1000, "upstream to observe the closed request");
    assert.ok(upstream.seen[0].closedAt - disconnectedAt < 1000);
  });
});

test("client disconnect before the upstream has responded cancels the pending upstream request", async () => {
  // Upstream never answers: this is the window between admission and the first byte.
  await withProxy(() => {}, async ({ upstream, proxy }) => {
    const req = http.request({ host: "127.0.0.1", port: proxy.port, path: "/api/v2/runs", method: "POST" });
    req.on("error", () => {});
    req.end("{}");
    await waitFor(() => upstream.seen.length === 1, 2000, "upstream to receive the request");

    const disconnectedAt = Date.now();
    req.destroy();

    await waitFor(() => upstream.seen[0].closedAt, 1000, "upstream to observe the closed request");
    assert.ok(upstream.seen[0].closedAt - disconnectedAt < 1000);
  });
});

test("normal responses pass through unchanged and are not cancelled", async () => {
  await withProxy((req, res) => {
    res.writeHead(200, { "content-type": "application/json", "x-upstream": "yes" });
    res.end(JSON.stringify({ ok: true, method: req.method }));
  }, async ({ upstream, proxy }) => {
    const response = await fetch(`http://127.0.0.1:${proxy.port}/api/auth/status`, {
      headers: { authorization: "Bearer token" }
    });
    assert.equal(response.status, 200);
    assert.equal(response.headers.get("x-upstream"), "yes");
    assert.deepEqual(await response.json(), { ok: true, method: "GET" });
    assert.equal(upstream.seen[0].headers.authorization, "Bearer token");
    assert.equal(proxy.logs().includes("AbortError"), false, proxy.logs());
  });
});

test("SSE events reach the client incrementally, not only when the response ends", async () => {
  await withProxy((req, res) => {
    res.writeHead(200, { "content-type": "text/event-stream" });
    res.write("event: run\ndata: {}\n\n");
    setTimeout(() => res.end("event: done\ndata: {}\n\n"), 600);
  }, async ({ proxy }) => {
    const started = Date.now();
    const response = await fetch(`http://127.0.0.1:${proxy.port}/api/v2/runs`, { method: "POST", body: "{}" });
    const reader = response.body.getReader();
    const first = await reader.read();
    assert.ok(Date.now() - started < 400, "first event should arrive before the upstream finishes");
    assert.match(new TextDecoder().decode(first.value), /event: run/);
    await reader.cancel();
  });
});

test("a client abort does not crash the proxy", async () => {
  await withProxy(sseUntilClosed, async ({ upstream, proxy }) => {
    for (let i = 0; i < 3; i += 1) {
      const req = http.request({ host: "127.0.0.1", port: proxy.port, path: "/api/v2/runs", method: "POST" });
      req.on("error", () => {});
      req.end("{}");
      const [res] = await once(req, "response");
      await once(res, "data");
      req.destroy();
    }
    await waitFor(() => upstream.seen.length === 3 && upstream.seen.every(r => r.closedAt), 2000, "all upstream requests closed");
    const response = await fetch(`http://127.0.0.1:${proxy.port}/health`);
    assert.equal(response.status, 200);
    assert.equal(proxy.child.exitCode, null, proxy.logs());
  });
});

// ---- /app/ static routing for the Vite client ----

async function startProxyWithClient(clientDir, extraEnv = {}) {
  const upstream = await startUpstream((req, res) => res.end("{}"));
  const port = await freePort();
  const child = spawn(process.execPath, [serverPath], {
    env: { ...process.env, PORT: String(port), HOST: "127.0.0.1", BACKEND_URL: `http://127.0.0.1:${upstream.port}`,
      DISABLE_SERVERLESS_AUTH: "true", CLIENT_DIST_DIR: clientDir, ...extraEnv },
    stdio: ["ignore", "pipe", "pipe"]
  });
  let output = "";
  child.stdout.on("data", c => { output += c; });
  child.stderr.on("data", c => { output += c; });
  await waitFor(() => output.includes("listening"), 10_000, "proxy to start");
  return { port, stop: () => { child.kill(); upstream.server.closeAllConnections(); upstream.server.close(); } };
}

test("/app/ serves the Vite build, falls back to index.html for client routes and caches hashed assets", async () => {
  const dir = await mkdtemp(joinPath(tmpdir(), "client-dist-"));
  await mkdir(joinPath(dir, "assets"));
  await writeFile(joinPath(dir, "index.html"), "<!doctype html><title>new ui</title>");
  await writeFile(joinPath(dir, "assets", "index-abc123.js"), "console.log(1)");
  const proxy = await startProxyWithClient(dir);
  try {
    const base = `http://127.0.0.1:${proxy.port}`;
    const index = await fetch(`${base}/app/`);
    assert.equal(index.status, 200);
    assert.match(await index.text(), /new ui/);
    assert.equal(index.headers.get("cache-control"), "no-cache");

    const route = await fetch(`${base}/app/c/6f1d2c52-0b6a-4c54-8c8b-7e5a3e0b1a11`);
    assert.equal(route.status, 200);
    assert.match(await route.text(), /new ui/);

    const asset = await fetch(`${base}/app/assets/index-abc123.js`);
    assert.equal(asset.status, 200);
    assert.match(asset.headers.get("cache-control"), /immutable/);

    assert.equal((await fetch(`${base}/app/assets/missing.js`)).status, 404);
    const redirect = await fetch(`${base}/app`, { redirect: "manual" });
    assert.equal(redirect.status, 308);
    assert.equal(redirect.headers.get("location"), "/app/");

    const traversal = await fetch(`${base}/app/..%2f..%2fserver.mjs`);
    assert.notEqual(traversal.status, 200);

    // Nothing but the client is served: no stray static root, no old page.
    assert.equal((await fetch(`${base}/legacy`)).status, 404);
    assert.equal((await fetch(`${base}/styles.css`)).status, 404);
  } finally {
    proxy.stop();
    await rm(dir, { recursive: true, force: true });
  }
});

test("/app/ returns 404 when the client has not been built", async () => {
  const dir = await mkdtemp(joinPath(tmpdir(), "client-empty-"));
  const proxy = await startProxyWithClient(dir);
  try {
    assert.equal((await fetch(`http://127.0.0.1:${proxy.port}/app/`)).status, 404);
  } finally {
    proxy.stop();
    await rm(dir, { recursive: true, force: true });
  }
});

test("/ goes to the app and /health answers without the client or the API", async () => {
  const dir = await mkdtemp(joinPath(tmpdir(), "client-ui-"));
  const proxy = await startProxyWithClient(dir);
  try {
    const base = `http://127.0.0.1:${proxy.port}`;
    const root = await fetch(`${base}/`, { redirect: "manual" });
    assert.equal(root.status, 302);
    assert.equal(root.headers.get("location"), "/app/");
    assert.equal(root.headers.get("cache-control"), "no-store");
    const health = await fetch(`${base}/health`);
    assert.equal(health.status, 200);
    assert.equal(await health.text(), "ok");
  } finally {
    proxy.stop();
    await rm(dir, { recursive: true, force: true });
  }
});

test("a leftover WEB_DEFAULT_UI setting is ignored", async () => {
  const dir = await mkdtemp(joinPath(tmpdir(), "client-ui-"));
  const proxy = await startProxyWithClient(dir, { WEB_DEFAULT_UI: "legacy" });
  try {
    const root = await fetch(`http://127.0.0.1:${proxy.port}/`, { redirect: "manual" });
    assert.equal(root.status, 302);
  } finally {
    proxy.stop();
    await rm(dir, { recursive: true, force: true });
  }
});
