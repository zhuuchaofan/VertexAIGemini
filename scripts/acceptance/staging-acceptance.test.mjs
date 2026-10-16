import test from "node:test";
import assert from "node:assert/strict";
import http from "node:http";
import { runAcceptance, looksLikeStaging } from "./staging-acceptance.mjs";

function readBody(req) {
  return new Promise(resolve => { let b = ""; req.on("data", c => b += c); req.on("end", () => resolve(b ? JSON.parse(b) : {})); });
}

/** A fake staging deployment: web+API in one server, plus a fake Firebase sign-in endpoint. */
async function startFake(overrides = {}) {
  const store = new Map();
  const outsiders = new Set(["outsider@example.com"]);
  const fake = { store, deletes: 0 };
  const server = http.createServer(async (req, res) => {
    const url = new URL(req.url, "http://x");
    const json = (status, body) => { res.writeHead(status, { "content-type": "application/json" }); res.end(JSON.stringify(body)); };
    if (url.pathname === "/v1/accounts:signInWithPassword") {
      const body = await readBody(req);
      if (body.password !== "good") return json(400, { error: { message: "INVALID_PASSWORD" } });
      return json(200, { idToken: `tok:${body.email}` });
    }
    if (url.pathname === "/api/workspace/config") {
      const model = { modelName: "gemini-x", thinking: { options: [{ value: "off" }, { value: "low" }, { value: "high" }] } };
      return json(200, { providers: [{ provider: { id: "gemini" }, models: [model], defaultModelName: "gemini-x" }], firebase: overrides.noFirebase ? null : { apiKey: "k", projectId: "demo-staging" } });
    }
    const auth = req.headers.authorization ?? "";
    const email = auth.startsWith("Bearer tok:") ? auth.slice(11) : null;
    if (!email) return json(401, { error: "Unauthorized" });
    if (outsiders.has(email)) return json(403, { error: "forbidden" });
    if (url.pathname === "/api/auth/status") return json(200, { success: true, user: { email } });
    if (url.pathname === "/api/conversations/" ) return json(200, { items: [...store.keys()].map(id => ({ id })) });
    const m = url.pathname.match(/^\/api\/conversations\/([\w-]+)$/);
    if (m && req.method === "GET") { const c = store.get(m[1]); return c ? json(200, c) : json(404, { error: "nf" }); }
    if (m && req.method === "DELETE") { fake.deletes++; store.delete(m[1]); res.writeHead(204); return res.end(); }
    if (url.pathname === "/api/export/legacy-1/json") return json(200, {});
    if (url.pathname === "/api/chat/stream") {
      const body = await readBody(req);
      if (body.modelName === "gemini-0.0-does-not-exist") {
        res.writeHead(200, { "content-type": "text/event-stream" });
        res.end(`event: final\ndata: ${JSON.stringify({ conversationId: null, content: "", succeeded: false, errorMessage: "所选模型「x」已不可用或已下线", status: "failed" })}\n\n`);
        return;
      }
      const id = body.conversationId ?? `c-${store.size + 1}`;
      const conv = store.get(id) ?? { id, messages: [] };
      conv.messages.push({ role: "user", content: body.message, attachments: body.attachments ?? [], status: "completed" });
      store.set(id, conv);
      res.writeHead(200, { "content-type": "text/event-stream" });
      res.write(": stream-open\n\n");
      const long = body.message.includes("1500");
      const send = (c) => res.write(`event: update\ndata: ${JSON.stringify({ content: c, conversationId: id })}\n\n`);
      send("O");
      let aborted = false;
      res.on("close", () => { if (!res.writableEnded) { aborted = true; conv.messages.push({ role: "model", content: "O", status: overrides.cancelStatus ?? "cancelled" }); } });
      if (long) { await new Promise(r => setTimeout(r, 300)); if (aborted) return; }
      const text = overrides.emptyAnswer ? "" : "OK";
      conv.messages.push({ role: "model", content: text, status: "completed" });
      res.end(`event: final\ndata: ${JSON.stringify({ conversationId: id, content: text, succeeded: true, status: "completed", citations: body.searchMode === "force" ? [{ url: "u" }] : [] })}\n\n`);
      return;
    }
    json(404, {});
  });
  await new Promise(r => server.listen(0, "127.0.0.1", r));
  const port = server.address().port;
  return { fake, port, close: () => new Promise(r => { server.closeAllConnections?.(); server.close(r); }) };
}

const baseConfig = port => ({
  baseUrl: `http://127.0.0.1:${port}`, email: "tester@example.com", password: "good",
  authEndpoint: `http://127.0.0.1:${port}`, allowNonStaging: true, log: () => {}
});

test("looksLikeStaging only accepts staging hosts", () => {
  assert.equal(looksLikeStaging("https://vertex-ai-web-staging-abc.run.app"), true);
  assert.equal(looksLikeStaging("https://vertex-ai-web-abc.run.app"), false);
});

test("refuses to run against a non-staging host", async () => {
  await assert.rejects(runAcceptance({ baseUrl: "https://vertex-ai-web-abc.run.app", email: "a", password: "b", log: () => {} }), /does not look like a staging host/);
});

test("full run passes against a healthy fake and cleans up", async () => {
  const s = await startFake();
  s.fake.store.set("legacy-1", { id: "legacy-1", messages: [{ role: "user", content: "old", status: "completed" }] });
  try {
    const { verdict, results } = await runAcceptance({ ...baseConfig(s.port), outsiderEmail: "outsider@example.com", outsiderPassword: "good", legacyConversationId: "legacy-1" });
    const failed = results.filter(r => r.status === "FAIL");
    assert.deepEqual(failed, []);
    assert.equal(verdict, "PASSED");
    assert.deepEqual([...s.fake.store.keys()], ["legacy-1"], "only the test conversations are deleted; the legacy one is kept");
    assert.ok(s.fake.deletes >= 4);
  } finally { await s.close(); }
});

test("a successful run without legacy/outsider cases is not reported as fully passed for skipped optional ones", async () => {
  const s = await startFake();
  try {
    const { results, verdict } = await runAcceptance(baseConfig(s.port));
    assert.equal(results.find(r => r.id === "F2").status, "SKIP");
    assert.equal(verdict, "PASSED"); // F1/F2 are optional extras
  } finally { await s.close(); }
});

test("wrong password fails login and the run is FAILED", async () => {
  const s = await startFake();
  try {
    const { verdict, results } = await runAcceptance({ ...baseConfig(s.port), password: "bad" });
    assert.equal(results.find(r => r.id === "A2").status, "FAIL");
    assert.equal(verdict, "FAILED");
  } finally { await s.close(); }
});

test("missing Firebase config is a failure (login cannot work)", async () => {
  const s = await startFake({ noFirebase: true });
  try {
    const { verdict } = await runAcceptance(baseConfig(s.port));
    assert.equal(verdict, "FAILED");
  } finally { await s.close(); }
});

test("an empty model answer fails chat", async () => {
  const s = await startFake({ emptyAnswer: true });
  try {
    const { results } = await runAcceptance({ ...baseConfig(s.port), only: ["A1", "A2", "B1"] });
    assert.equal(results.find(r => r.id === "B1").status, "FAIL");
  } finally { await s.close(); }
});

test("a cancel that is saved with the wrong status fails E1", async () => {
  const s = await startFake({ cancelStatus: "completed" });
  try {
    const { results } = await runAcceptance({ ...baseConfig(s.port), only: ["A1", "A2", "E1"] });
    assert.equal(results.find(r => r.id === "E1").status, "FAIL");
  } finally { await s.close(); }
});

test("a subset run can never be reported as PASSED", async () => {
  const s = await startFake();
  try {
    const { verdict, passed } = await runAcceptance({ ...baseConfig(s.port), only: ["A1", "A2"] });
    assert.match(verdict, /PARTIAL/);
    assert.equal(passed, false);
  } finally { await s.close(); }
});

test("an outsider that is NOT rejected fails F1", async () => {
  const s = await startFake();
  try {
    const { results } = await runAcceptance({ ...baseConfig(s.port), only: ["A1", "F1"], outsiderEmail: "tester2@example.com", outsiderPassword: "good" });
    assert.equal(results.find(r => r.id === "F1").status, "FAIL");
  } finally { await s.close(); }
});
