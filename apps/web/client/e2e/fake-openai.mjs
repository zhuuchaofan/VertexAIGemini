// A stand-in for an OpenAI-compatible provider, for the end-to-end tests. It streams server-sent events, includes
// reasoning and usage like real ones do, and reports (at /__state) what it was asked and when a client went away.
import http from "node:http";

const state = { requests: 0, lastBody: null, closed: 0 };

export function startFakeOpenAI(port) {
  const server = http.createServer((req, res) => {
    if (req.url === "/__state") {
      res.setHeader("content-type", "application/json");
      res.end(JSON.stringify(state));
      return;
    }

    let raw = "";
    req.on("data", chunk => { raw += chunk; });
    req.on("end", () => {
      state.requests += 1;
      let body = {};
      try { body = JSON.parse(raw); } catch { /* not json */ }
      state.lastBody = body;
      const last = [...(body.messages ?? [])].reverse().find(m => m.role === "user");
      const raw_text = typeof last?.content === "string" ? last.content : (last?.content ?? []).filter(p => p.type === "text").map(p => p.text).join(" ");
      // The server puts the date (and, when it searched, the findings) in front of the question; echo only the question.
      const text = raw_text.replace(/^\[当前时间：[^\]]*\]\s*/, "").replace(/^[\s\S]*\[检索结果结束\]\s*回答时[^\n]*\n\s*/, "");

      res.writeHead(200, { "content-type": "text/event-stream", "cache-control": "no-cache" });
      const send = delta => res.write(`data: ${JSON.stringify({ id: "fake-1", model: body.model, choices: [{ delta }] })}\n\n`);
      res.on("close", () => { if (!res.writableFinished) state.closed += 1; });

      if (text.includes("slow")) {
        let n = 0;
        const timer = setInterval(() => { n += 1; send({ content: `word${n} ` }); }, 100);
        res.on("close", () => clearInterval(timer));
        return;
      }

      send({ reasoning_content: "checking the question. " });
      send({ content: `fake provider says: ${text}` });
      res.write(`data: ${JSON.stringify({ choices: [{ delta: {}, finish_reason: "stop" }] })}\n\n`);
      res.write(`data: ${JSON.stringify({ choices: [], usage: { prompt_tokens: 12, completion_tokens: 7, total_tokens: 19, completion_tokens_details: { reasoning_tokens: 3 } } })}\n\n`);
      res.write("data: [DONE]\n\n");
      res.end();
    });
  });
  return new Promise(resolve => server.listen(port, "127.0.0.1", () => resolve(server)));
}
