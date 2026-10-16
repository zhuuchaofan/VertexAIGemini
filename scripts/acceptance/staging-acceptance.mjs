#!/usr/bin/env node
// Business acceptance suite for the STAGING environment (real model calls, real data path).
//
// This is NOT the release smoke test. The release script only proves a candidate is up and wired together
// (/health/ready, the proxy reaching the API). This suite proves the product works end to end: login,
// streamed chat with a real model, attachments, thinking levels, search, user cancel, data really saved,
// unknown-model handling, access policy and old-conversation compatibility.
//
// It signs in with a dedicated STAGING test account through the Firebase Auth REST API and talks to the
// STAGING web URL like a browser would. It refuses to run against anything that does not look like staging.
//
// Usage (see docs/staging-and-release-guide.md):
//   ACCEPT_BASE_URL=https://vertex-ai-web-staging-....run.app \
//   ACCEPT_EMAIL=... ACCEPT_PASSWORD=... node scripts/acceptance/staging-acceptance.mjs [--long] [--only B1,C1]
// Optional: ACCEPT_OUTSIDER_EMAIL/ACCEPT_OUTSIDER_PASSWORD (a non-allow-listed account, expects 403),
//           ACCEPT_LEGACY_CONVERSATION_ID (a conversation written by the previous app version),
//           ACCEPT_AUTH_ENDPOINT (default https://identitytoolkit.googleapis.com).
import { readEventStream } from "../../apps/web/public/sse.js";

const REQUIRED = ["A1", "A2", "A3", "B1", "B2", "B4", "C1", "C2", "D1", "E1", "E2", "H1"];

const PNG_1x1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==";

function makePdfBase64(text = "Acceptance test document") {
  const objects = [
    "<</Type/Catalog/Pages 2 0 R>>",
    "<</Type/Pages/Kids[3 0 R]/Count 1>>",
    "<</Type/Page/Parent 2 0 R/MediaBox[0 0 300 200]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>",
    null,
    "<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>"
  ];
  const stream = `BT /F1 16 Tf 20 100 Td (${text}) Tj ET`;
  objects[3] = `<</Length ${stream.length}>>\nstream\n${stream}\nendstream`;
  let pdf = "%PDF-1.4\n";
  const offsets = [];
  objects.forEach((body, i) => {
    offsets.push(pdf.length);
    pdf += `${i + 1} 0 obj\n${body}\nendobj\n`;
  });
  const xref = pdf.length;
  pdf += `xref\n0 ${objects.length + 1}\n0000000000 65535 f \n`;
  for (const off of offsets) pdf += `${String(off).padStart(10, "0")} 00000 n \n`;
  pdf += `trailer\n<</Size ${objects.length + 1}/Root 1 0 R>>\nstartxref\n${xref}\n%%EOF\n`;
  return Buffer.from(pdf, "latin1").toString("base64");
}

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));

export function looksLikeStaging(baseUrl) {
  return new URL(baseUrl).hostname.includes("staging");
}

export async function runAcceptance(config) {
  const {
    baseUrl, email, password,
    authEndpoint = "https://identitytoolkit.googleapis.com",
    outsiderEmail, outsiderPassword, legacyConversationId,
    long = false, only = null, allowNonStaging = false,
    log = line => console.log(line)
  } = config;

  if (!allowNonStaging && !looksLikeStaging(baseUrl)) {
    throw new Error(
      `Refusing to run: ${new URL(baseUrl).hostname} does not look like a staging host. ` +
      "This suite makes real model calls and writes test conversations; it must never run against production.");
  }
  const base = baseUrl.replace(/\/$/, "");
  const results = [];
  const created = [];
  const state = { token: null, config: null, conversationId: null, lastFinal: null };

  async function scenario(id, name, fn, { optional = false } = {}) {
    if (only && !only.includes(id)) return;
    const started = Date.now();
    try {
      const outcome = await fn();
      const status = outcome?.status ?? "PASS";
      const detail = outcome?.detail ?? "";
      results.push({ id, name, status, detail, ms: Date.now() - started, optional });
    } catch (error) {
      results.push({ id, name, status: "FAIL", detail: error.message, ms: Date.now() - started, optional });
    }
    const r = results[results.length - 1];
    log(`${r.status.padEnd(4)} ${id} ${name}${r.detail ? ` - ${r.detail}` : ""} (${r.ms}ms)`);
  }

  const assert = (condition, message) => { if (!condition) throw new Error(message); };

  async function api(path, { method = "GET", body, token = state.token, signal } = {}) {
    const headers = {};
    if (token) headers.authorization = `Bearer ${token}`;
    if (body !== undefined) headers["content-type"] = "application/json";
    return fetch(`${base}${path}`, { method, headers, body: body === undefined ? undefined : JSON.stringify(body), signal });
  }

  async function signIn(mail, pass, apiKey) {
    const response = await fetch(`${authEndpoint}/v1/accounts:signInWithPassword?key=${encodeURIComponent(apiKey)}`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ email: mail, password: pass, returnSecureToken: true })
    });
    const json = await response.json().catch(() => ({}));
    if (!response.ok || !json.idToken) throw new Error(`sign-in failed (${response.status}): ${json?.error?.message ?? "no token"}`);
    return json.idToken;
  }

  /** One streamed chat call, parsed like the browser does. Optionally aborts after the first update. */
  async function chat(payload, { abortAfterFirstUpdate = false, token = state.token } = {}) {
    const controller = new AbortController();
    const response = await api("/api/chat/stream", { method: "POST", body: payload, token, signal: controller.signal });
    if (!response.ok) {
      const json = await response.json().catch(() => ({}));
      return { httpStatus: response.status, error: json.error ?? `HTTP ${response.status}`, updates: 0, final: null, conversationId: null };
    }
    let updates = 0;
    let firstUpdateMs = null;
    let final = null;
    let finals = 0;
    let conversationId = null;
    const started = Date.now();
    try {
      await readEventStream(response.body, {
        update: payload => {
          updates++;
          firstUpdateMs ??= Date.now() - started;
          conversationId ??= payload.conversationId ?? null;
          if (abortAfterFirstUpdate && updates === 1) controller.abort();
        },
        final: payload => { finals++; final ??= payload; conversationId ??= payload.conversationId ?? null; }
      });
    } catch (error) {
      if (!controller.signal.aborted) throw error;
    }
    if (final?.conversationId) created.includes(final.conversationId) || created.push(final.conversationId);
    else if (conversationId && !created.includes(conversationId)) created.push(conversationId);
    return { httpStatus: 200, updates, firstUpdateMs, final, finals, conversationId, elapsedMs: Date.now() - started };
  }

  async function getConversation(id) {
    const response = await api(`/api/conversations/${id}`);
    assert(response.ok, `GET conversation ${id} -> ${response.status}`);
    return response.json();
  }

  // ---------------------------------------------------------------------------------------------- A: wiring
  await scenario("A1", "workspace config lists models and a Firebase web config", async () => {
    const response = await api("/api/workspace/config", { token: null });
    assert(response.status === 200, `config -> ${response.status}`);
    state.config = await response.json();
    const gemini = state.config.providers?.find(p => p.provider.id === "gemini") ?? state.config.providers?.[0];
    assert(gemini?.models?.length > 0, "no models offered");
    assert(state.config.firebase?.apiKey && state.config.firebase?.projectId, "Firebase web config missing (login cannot work)");
    state.model = gemini.models.find(m => m.modelName === gemini.defaultModelName) ?? gemini.models[0];
    return { detail: `${gemini.models.length} models, default ${state.model.modelName}, firebase project ${state.config.firebase.projectId}` };
  });

  await scenario("A2", "login with the staging test account; /api/auth/status knows the user", async () => {
    assert(state.config?.firebase?.apiKey, "A1 must pass first (needs the Firebase web API key)");
    state.token = await signIn(email, password, state.config.firebase.apiKey);
    const response = await api("/api/auth/status");
    assert(response.status === 200, `auth/status -> ${response.status} (a 403 means the test account is not allow-listed)`);
    const json = await response.json();
    assert(json.success === true && json.user?.email, "auth/status did not return the user");
    return { detail: json.user.email };
  });

  await scenario("A3", "anonymous and malformed tokens are rejected (401), not 5xx", async () => {
    for (const token of [null, "not.a.jwt", "garbage"]) {
      const response = await api("/api/conversations", { token });
      assert(response.status === 401, `token ${token ?? "(none)"} -> ${response.status}, expected 401`);
    }
  });

  // ---------------------------------------------------------------------------------------------- B: chat + data saved
  await scenario("B1", "streamed chat with the real model completes", async () => {
    const result = await chat({ message: "请只回复两个字母：OK", modelName: state.model.modelName, searchMode: "off" });
    assert(result.httpStatus === 200, `HTTP ${result.httpStatus}: ${result.error}`);
    assert(result.finals === 1, `expected exactly one final event, got ${result.finals}`);
    assert(result.final.status === "completed" && result.final.succeeded === true, `status ${result.final?.status}: ${result.final?.errorMessage ?? ""}`);
    assert(result.updates >= 1, "no streamed update before the final event (not streaming)");
    assert(result.final.content?.trim().length > 0, "empty answer");
    state.conversationId = result.final.conversationId;
    state.lastFinal = result.final;
    return { detail: `${result.updates} updates, first after ${result.firstUpdateMs}ms, total ${result.elapsedMs}ms` };
  });

  await scenario("B2", "the user message and the reply were really saved", async () => {
    assert(state.conversationId, "B1 must pass first");
    const conversation = await getConversation(state.conversationId);
    const roles = conversation.messages.map(m => m.role);
    assert(roles.includes("user") && roles.includes("model"), `saved roles: ${roles.join(",")}`);
    const reply = conversation.messages.find(m => m.role === "model");
    assert(reply.content === state.lastFinal.content, "saved reply differs from the streamed reply");
    assert((reply.status ?? "completed") === "completed", `saved status ${reply.status}`);
    const list = await (await api("/api/conversations/")).json();
    assert(list.items.some(item => item.id === state.conversationId), "conversation missing from the list");
  });

  await scenario("B4", "a follow-up in the same conversation keeps history", async () => {
    assert(state.conversationId, "B1 must pass first");
    const result = await chat({ conversationId: state.conversationId, message: "我上一条消息要求你回复什么？请用一句话复述。", modelName: state.model.modelName, searchMode: "off" });
    assert(result.final?.status === "completed", `status ${result.final?.status}: ${result.final?.errorMessage ?? result.error}`);
    const conversation = await getConversation(state.conversationId);
    assert(conversation.messages.length === 4, `expected 4 saved messages, got ${conversation.messages.length}`);
  });

  // ---------------------------------------------------------------------------------------------- C: attachments
  async function attachmentCase(fileName, mimeType, base64Data, prompt) {
    const result = await chat({ message: prompt, modelName: state.model.modelName, searchMode: "off", attachments: [{ base64Data, mimeType, fileName }] });
    assert(result.final?.status === "completed", `status ${result.final?.status}: ${result.final?.errorMessage ?? result.error}`);
    const conversation = await getConversation(result.final.conversationId);
    const userMessage = conversation.messages.find(m => m.role === "user");
    assert(userMessage.attachments?.length === 1, "the attachment was not saved with the message");
    assert(userMessage.attachments[0].fileName === fileName, "saved attachment lost its file name");
    return { detail: `reply ${result.final.content.length} chars` };
  }
  await scenario("C1", "an image attachment is accepted, answered and saved", () =>
    attachmentCase("pixel.png", "image/png", PNG_1x1, "这是一张测试图片。请用一句话说明你看到了什么（颜色或尺寸即可）。"));
  await scenario("C2", "a PDF attachment is accepted, answered and saved", () =>
    attachmentCase("note.pdf", "application/pdf", makePdfBase64("Acceptance test document"), "请用一句话说明这个 PDF 里写了什么。"));

  // ---------------------------------------------------------------------------------------------- D: thinking, search
  await scenario("D1", "every thinking level of the default model works", async () => {
    const options = state.model?.thinking?.options ?? [];
    if (options.length === 0) return { status: "SKIP", detail: "the default model exposes no thinking levels" };
    const seen = [];
    for (const option of options) {
      if (option.value === "off") continue;
      const result = await chat({ message: "1+1 等于几？只回答数字。", modelName: state.model.modelName, searchMode: "off", thinkingEnabled: true, thinkingLevel: option.value });
      assert(result.final?.status === "completed", `level ${option.value}: status ${result.final?.status}: ${result.final?.errorMessage ?? result.error}`);
      seen.push(option.value);
    }
    return { detail: `levels ok: ${seen.join(", ")}` };
  });

  await scenario("D2", "web search (force) completes; citations are reported if present", async () => {
    const result = await chat({ message: "今天有什么重要的科技新闻？请简要回答并给出来源。", modelName: state.model.modelName, searchMode: "force" });
    assert(result.final?.status === "completed", `status ${result.final?.status}: ${result.final?.errorMessage ?? result.error}`);
    const citations = result.final.citations?.length ?? 0;
    return citations > 0
      ? { detail: `${citations} citations` }
      : { status: "WARN", detail: "completed but no citations were returned (search 'force' is a prompt instruction, not a guarantee)" };
  }, { optional: true });

  await scenario("D3", "search off returns no citations", async () => {
    const result = await chat({ message: "用一句话解释什么是 HTTP。", modelName: state.model.modelName, searchMode: "off" });
    assert(result.final?.status === "completed", `status ${result.final?.status}`);
    assert(!(result.final.citations?.length > 0), "citations were returned although search was off");
  }, { optional: true });

  // ---------------------------------------------------------------------------------------------- E: cancel, errors
  await scenario("E1", "user cancel keeps the partial reply, saved as 'cancelled'", async () => {
    const result = await chat(
      { message: "请写一篇不少于 1500 字的文章，主题是云计算的发展历史。", modelName: state.model.modelName, searchMode: "off" },
      { abortAfterFirstUpdate: true });
    assert(result.updates >= 1, "no update arrived before the cancel");
    const id = result.conversationId;
    assert(id, "no conversation id was streamed, cannot verify what was saved");
    let reply = null;
    for (let i = 0; i < 40 && !reply; i++) {
      await sleep(500);
      const conversation = await getConversation(id);
      reply = conversation.messages.find(m => m.role === "model");
    }
    assert(reply, "no model message was saved after the cancel (waited 20s)");
    assert(reply.status === "cancelled", `saved status '${reply.status}', expected 'cancelled'`);
    assert(reply.content.length > 0, "the partial reply was not kept");
    return { detail: `kept ${reply.content.length} chars. NOTE: whether Vertex stopped generating must be checked in Vertex usage (manual)` };
  });

  await scenario("E2", "an unknown model is rejected explicitly (no silent fallback)", async () => {
    const result = await chat({ message: "hi", modelName: "gemini-0.0-does-not-exist", searchMode: "off" });
    const message = result.final?.errorMessage ?? result.error ?? "";
    assert(result.final ? result.final.status === "failed" : result.httpStatus >= 400, "an unknown model was accepted");
    assert(/不可用|下线|unknown|not found/i.test(message), `unexpected message: ${message}`);
  });

  // ---------------------------------------------------------------------------------------------- F: access, legacy
  await scenario("F1", "an account outside the allow-list gets 403", async () => {
    if (!outsiderEmail) return { status: "SKIP", detail: "no ACCEPT_OUTSIDER_EMAIL provided" };
    const token = await signIn(outsiderEmail, outsiderPassword, state.config.firebase.apiKey);
    const response = await api("/api/auth/status", { token });
    assert(response.status === 403, `outsider -> ${response.status}, expected 403`);
  }, { optional: true });

  await scenario("F2", "a conversation written by the previous app version is readable and exportable", async () => {
    if (!legacyConversationId) return { status: "SKIP", detail: "no ACCEPT_LEGACY_CONVERSATION_ID provided (seed one with the previous version first)" };
    const conversation = await getConversation(legacyConversationId);
    assert(conversation.messages.length > 0, "legacy conversation has no messages");
    assert(conversation.messages.every(m => (m.status ?? "completed")), "a message came back without a status");
    const exported = await api(`/api/export/${legacyConversationId}/json`);
    assert(exported.status === 200, `export -> ${exported.status}`);
  }, { optional: true });

  // ---------------------------------------------------------------------------------------------- G: long stream
  if (long) {
    await scenario("G1", "a generation longer than 60s is not cut off", async () => {
      const result = await chat({ message: "请用尽可能详细的方式，分 10 个部分论述分布式系统一致性，每部分不少于 400 字。", modelName: state.model.modelName, searchMode: "off", thinkingEnabled: true, thinkingLevel: "high" });
      assert(["completed", "truncated"].includes(result.final?.status), `status ${result.final?.status}: ${result.final?.errorMessage ?? result.error}`);
      return result.elapsedMs > 60_000
        ? { detail: `${Math.round(result.elapsedMs / 1000)}s, ${result.final.status}` }
        : { status: "WARN", detail: `finished in ${Math.round(result.elapsedMs / 1000)}s - did not exceed 60s, so the timeout was not exercised` };
    }, { optional: true });
  }

  // ---------------------------------------------------------------------------------------------- H: cleanup
  await scenario("H1", "test conversations can be deleted (and are really gone)", async () => {
    for (const id of created) {
      const response = await api(`/api/conversations/${id}`, { method: "DELETE" });
      assert(response.status === 204, `DELETE ${id} -> ${response.status}`);
      const after = await api(`/api/conversations/${id}`);
      assert(after.status === 404, `deleted conversation still readable (${after.status})`);
    }
    return { detail: `${created.length} deleted` };
  });

  const ran = new Set(results.map(r => r.id));
  const failed = results.filter(r => r.status === "FAIL");
  const missingRequired = REQUIRED.filter(id => !ran.has(id) || results.find(r => r.id === id)?.status === "SKIP");
  const verdict = failed.length > 0 ? "FAILED" : (only ? "PARTIAL (subset run)" : missingRequired.length > 0 ? "INCOMPLETE" : "PASSED");
  log("");
  log(`ACCEPTANCE RESULT: ${verdict}` +
    ` - pass ${results.filter(r => r.status === "PASS").length}, fail ${failed.length}, warn ${results.filter(r => r.status === "WARN").length}, skip ${results.filter(r => r.status === "SKIP").length}`);
  if (missingRequired.length) log(`Required scenarios not run or skipped: ${missingRequired.join(", ")}`);
  log("Still MANUAL (cannot be proven from outside): Vertex usage stops after cancel, >60s streaming through the real front end, quota/global caps, cost console, IAM.");
  return { results, verdict, passed: verdict === "PASSED" };
}

// CLI
if (import.meta.url === `file://${process.argv[1]}`) {
  const args = process.argv.slice(2);
  const only = args.includes("--only") ? args[args.indexOf("--only") + 1].split(",") : null;
  const env = process.env;
  for (const key of ["ACCEPT_BASE_URL", "ACCEPT_EMAIL", "ACCEPT_PASSWORD"]) {
    if (!env[key]) { console.error(`Missing ${key}. See the header of this file and docs/staging-and-release-guide.md.`); process.exit(2); }
  }
  try {
    const { passed } = await runAcceptance({
      baseUrl: env.ACCEPT_BASE_URL, email: env.ACCEPT_EMAIL, password: env.ACCEPT_PASSWORD,
      authEndpoint: env.ACCEPT_AUTH_ENDPOINT || undefined,
      outsiderEmail: env.ACCEPT_OUTSIDER_EMAIL, outsiderPassword: env.ACCEPT_OUTSIDER_PASSWORD,
      legacyConversationId: env.ACCEPT_LEGACY_CONVERSATION_ID,
      long: args.includes("--long"), only,
      allowNonStaging: args.includes("--allow-non-staging")
    });
    process.exit(passed ? 0 : 1);
  } catch (error) {
    console.error(error.message);
    process.exit(2);
  }
}
