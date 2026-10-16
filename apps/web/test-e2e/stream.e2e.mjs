// Browser end-to-end check of the streaming UX: real app.js in Chromium -> real server.mjs/proxy.mjs ->
// a scripted fake backend. Firebase is stubbed (no network, no credentials).
//
// Not part of `npm test` (needs Playwright + Chromium). Run:
//   PW_ROOT=$(npm root -g) node test-e2e/stream.e2e.mjs
import assert from "node:assert/strict";
import { createRequire } from "node:module";
import { createServer } from "node:http";
import { spawn } from "node:child_process";

const require = createRequire(`${process.env.PW_ROOT ?? process.cwd()}/`);
const { chromium } = require("playwright");
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

// ---- scripted fake backend -------------------------------------------------------------
const seen = { closed: [], started: [] };
const conversationId = "11111111-1111-1111-1111-111111111111";
const sse = (event, data) => `event: ${event}\ndata: ${JSON.stringify(data)}\n\n`;
const update = (content, extra = {}) => sse("update", { content, thinkingContent: null, citations: null, conversationId, ...extra });
const final = (over = {}) => sse("final", {
  conversationId, content: "", thinkingContent: null, succeeded: true, errorMessage: null, citations: null, status: "completed", ...over
});

const backend = createServer(async (req, res) => {
  const json = (body, status = 200) => { res.writeHead(status, { "content-type": "application/json" }); res.end(JSON.stringify(body)); };
  if (req.url.startsWith("/api/workspace/config")) {
    const model = { name: "Test", modelName: "test-model", description: "", supportsThinking: false, stage: "ga" };
    const preset = { id: "default", name: "默认", prompt: "", description: "" };
    return json({
      providers: [{ provider: { id: "gemini", name: "Gemini", description: "" }, models: [model], presets: [preset], defaultPresetId: "default", defaultModelName: "test-model" }],
      defaultProviderId: "gemini", models: [model], presets: [preset], defaultPresetId: "default", defaultModelName: "test-model",
      firebase: { apiKey: "k", projectId: "p", authDomain: "p.firebaseapp.com", appId: "a" }
    });
  }
  if (req.url.startsWith("/api/conversations")) return json({ items: [], offset: 0, limit: 30, hasMore: false, nextCursor: null });
  if (req.url.startsWith("/api/chat/stream")) {
    let body = "";
    for await (const chunk of req) body += chunk;
    const message = JSON.parse(body).message;
    seen.started.push(message);
    res.on("close", () => seen.closed.push(message));
    res.writeHead(200, { "content-type": "text/event-stream" });
    res.write(": stream-open\n\n");
    if (message === "complete") {
      res.write(update("Hel")); await sleep(50); res.write(update("Hello"));
      res.end(final({ content: "Hello world" }));
    } else if (message === "truncated") {
      res.write(update("cut"));
      res.end(final({ content: "cut off here", status: "truncated" }));
    } else if (message === "failed") {
      res.write(update("partial reply"));
      res.end(final({ content: "partial reply", succeeded: false, status: "failed", errorMessage: "AI 服务暂时不可用" }));
    } else if (message === "cut") {
      res.write(update("half an ans"));
      await sleep(100);
      res.socket.destroy(); // network/proxy drop: no final event
    } else if (message === "slow") {
      for (let i = 0; i < 100 && !res.destroyed; i++) { res.write(update(`token ${i} `)); await sleep(150); }
      if (!res.destroyed) res.end(final({ content: "never reached" }));
    }
    return;
  }
  json({ error: "not found" }, 404);
});
await new Promise(resolve => backend.listen(0, "127.0.0.1", resolve));
const backendPort = backend.address().port;

// ---- real web server -------------------------------------------------------------------
const webPort = 18000 + Math.floor(Math.random() * 1000);
const web = spawn("node", ["server.mjs"], {
  env: { ...process.env, PORT: String(webPort), BACKEND_URL: `http://127.0.0.1:${backendPort}`, DISABLE_SERVERLESS_AUTH: "true" },
  stdio: "ignore"
});
for (let i = 0; i < 40; i++) {
  try { if ((await fetch(`http://127.0.0.1:${webPort}/`)).ok) break; } catch { /* starting */ }
  await sleep(100);
}

// ---- browser ----------------------------------------------------------------------------
const firebaseApp = "export const initializeApp = () => ({});";
const firebaseAuth = `
  const auth = { currentUser: null };
  export const getAuth = () => auth;
  export const onAuthStateChanged = (a, cb) => { setTimeout(() => cb(a.currentUser), 0); return () => {}; };
  export const signInWithEmailAndPassword = async (a, email) => {
    const user = { uid: "u1", email, emailVerified: true, getIdToken: async () => "fake-id-token" };
    a.currentUser = user; return { user };
  };
  export const createUserWithEmailAndPassword = signInWithEmailAndPassword;
  export class GoogleAuthProvider {}
  export const signInWithPopup = async () => { throw new Error("unused"); };
  export const sendPasswordResetEmail = async () => {};
  export const signOut = async () => {};
`;

const browser = await chromium.launch({ executablePath: "/opt/pw-browsers/chromium", args: ["--no-sandbox"] });
const page = await browser.newPage();
const pageErrors = [];
page.on("pageerror", error => pageErrors.push(String(error)));
await page.route("https://www.gstatic.com/**/firebase-app.js", r => r.fulfill({ contentType: "text/javascript", body: firebaseApp }));
await page.route("https://www.gstatic.com/**/firebase-auth.js", r => r.fulfill({ contentType: "text/javascript", body: firebaseAuth }));
await page.goto(`http://127.0.0.1:${webPort}/`, { waitUntil: "networkidle" });
await page.fill('#auth-form input[name="email"]', "tester@example.com");
await page.fill('#auth-form input[name="password"]', "password-123");
await page.click("#auth-submit");
await page.waitForSelector("#message-input", { state: "visible" });

async function send(text) {
  await page.fill("#message-input", text);
  await page.click("#send-button");
}
const lastAssistant = () => page.locator(".message.assistant").last();
const statusOf = async () => (await lastAssistant().locator(".message-status").count())
  ? await lastAssistant().locator(".message-status").innerText() : null;
async function waitIdle() {
  await page.waitForFunction(() => document.querySelector("#send-button") && !document.querySelector("#send-button").disabled, null, { timeout: 15000 });
}

const results = [];
async function scenario(name, fn) {
  try { await fn(); results.push(`PASS ${name}`); }
  catch (error) { results.push(`FAIL ${name}: ${error.message}`); process.exitCode = 1; }
}

await scenario("normal completion shows the full answer and no warning", async () => {
  await send("complete"); await waitIdle();
  assert.match(await lastAssistant().locator(".message-content").innerText(), /Hello world/);
  assert.equal(await statusOf(), null);
});

await scenario("output limit shows a truncation notice", async () => {
  await send("truncated"); await waitIdle();
  assert.match(await statusOf(), /截断/);
  assert.match(await lastAssistant().locator(".message-content").innerText(), /cut off here/);
});

await scenario("server failure keeps the partial text and shows the error", async () => {
  await send("failed"); await waitIdle();
  assert.match(await lastAssistant().locator(".message-content").innerText(), /partial reply/);
  assert.match(await statusOf(), /AI 服务暂时不可用/);
});

await scenario("connection drop without a final event is NOT shown as success", async () => {
  await send("cut"); await waitIdle();
  assert.match(await lastAssistant().locator(".message-content").innerText(), /half an ans/);
  assert.match(await statusOf(), /连接中断/);
  assert.match(await page.locator("#connection-state").innerText(), /连接中断/);
});

await scenario("stop button is visible while streaming, hidden after, and closes the upstream request", async () => {
  await send("slow");
  await page.waitForSelector("#stop-button", { state: "visible" });
  await page.waitForFunction(() => /token 2/.test(document.querySelector(".message.assistant:last-child .message-content")?.textContent ?? ""), null, { timeout: 8000 });
  await page.click("#stop-button");
  await page.click("#stop-button", { force: true, timeout: 500 }).catch(() => {}); // repeated stop is harmless
  await waitIdle();
  assert.match(await statusOf(), /已停止生成/);
  assert.match(await lastAssistant().locator(".message-content").innerText(), /token 2/);
  assert.equal(await page.locator("#stop-button").isVisible(), false);
  const waitedFrom = Date.now();
  while (!seen.closed.includes("slow") && Date.now() - waitedFrom < 3000) await sleep(50);
  assert.ok(seen.closed.includes("slow"), "backend request was not closed after pressing stop");
});

await scenario("no uncaught page errors", async () => assert.deepEqual(pageErrors, []));

console.log(results.join("\n"));
await browser.close();
web.kill();
backend.closeAllConnections?.();
backend.close();
