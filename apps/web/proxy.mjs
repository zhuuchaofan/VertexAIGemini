import { once } from "node:events";

const ALLOWED_METHODS = new Set(["GET", "HEAD", "POST", "PATCH", "PUT", "DELETE"]);

// Hop-by-hop headers must not be forwarded by a proxy (RFC 9110 §7.6.1).
const HOP_BY_HOP = new Set([
  "connection", "keep-alive", "proxy-authenticate", "proxy-authorization",
  "proxy-connection", "te", "trailer", "transfer-encoding", "upgrade"
]);

/**
 * Builds the /api reverse-proxy handler.
 *
 * Reliability rules (see docs/cloud-run.md):
 *  - the browser disconnecting aborts the upstream request, which in turn aborts generation in the API;
 *  - an upstream failure after the response started destroys the client connection instead of ending the
 *    body cleanly, so a half-finished stream can never look like a finished one;
 *  - a missing service-to-service identity token is a 502, never a silent unauthenticated forward;
 *  - the whole exchange is capped by `upstreamTimeoutMs`, which must stay <= the Cloud Run timeout.
 */
export function createApiProxy({
  backendUrl,
  getIdentityToken,
  requireIdentityToken = true,
  upstreamTimeoutMs = 295_000,
  fetchImpl = fetch,
  logger = console
}) {
  return async function proxyApi(req, res) {
    const method = req.method ?? "GET";
    if (!ALLOWED_METHODS.has(method)) {
      sendJson(res, 405, { error: "Method not allowed" });
      return;
    }

    const clientGone = new AbortController();
    const onClose = () => {
      if (!res.writableEnded) clientGone.abort(new Error("client disconnected"));
    };
    res.on("close", onClose);

    try {
      const headers = buildUpstreamHeaders(req.headers);

      const token = await getIdentityToken();
      if (token) {
        headers.set("x-serverless-authorization", `Bearer ${token}`);
      } else if (requireIdentityToken) {
        logger.error("Cannot obtain a service identity token for the backend");
        sendJson(res, 502, { error: "后端认证暂时不可用，请稍后重试" });
        return;
      }

      const signal = AbortSignal.any([clientGone.signal, AbortSignal.timeout(upstreamTimeoutMs)]);

      let response;
      try {
        response = await fetchImpl(new URL(req.url, backendUrl), {
          method,
          headers,
          body: method === "GET" || method === "HEAD" ? undefined : req,
          duplex: "half",
          redirect: "manual",
          signal
        });
      } catch (error) {
        if (clientGone.signal.aborted) return; // nobody left to answer
        const timedOut = error?.name === "TimeoutError" || error?.cause?.name === "TimeoutError";
        logger.error(`Backend request failed: ${error?.message ?? error}`);
        sendJson(res, timedOut ? 504 : 502, {
          error: timedOut ? "后端响应超时" : "后端服务暂时不可用，请稍后重试"
        });
        return;
      }

      res.statusCode = response.status;
      copyResponseHeaders(response, res);
      res.flushHeaders();

      if (!response.body) {
        res.end();
        return;
      }

      try {
        for await (const chunk of response.body) {
          if (!res.write(chunk)) await waitForDrain(res);
        }
        res.end();
      } catch (error) {
        if (clientGone.signal.aborted) return;
        // Upstream broke after we already told the client "200". End the connection abnormally so the
        // browser sees a network error rather than a complete-looking response.
        logger.error(`Backend stream interrupted: ${error?.message ?? error}`);
        res.destroy(error instanceof Error ? error : new Error(String(error)));
      }
    } finally {
      res.off("close", onClose);
    }
  };
}

function buildUpstreamHeaders(source) {
  const headers = new Headers();
  for (const [name, value] of Object.entries(source)) {
    const lower = name.toLowerCase();
    if (value === undefined || lower === "host" || HOP_BY_HOP.has(lower)) continue;
    // Callers may never supply the service credential themselves.
    if (lower === "x-serverless-authorization") continue;

    if (Array.isArray(value)) {
      for (const item of value) headers.append(name, item);
    } else {
      headers.set(name, value);
    }
  }
  return headers;
}

function copyResponseHeaders(response, res) {
  // fetch() has already decoded the body, so the original encoding/length no longer apply.
  const decoded = response.headers.has("content-encoding");
  const setCookies = response.headers.getSetCookie?.() ?? [];
  response.headers.forEach((value, name) => {
    const lower = name.toLowerCase();
    if (HOP_BY_HOP.has(lower) || lower === "set-cookie" || lower === "content-encoding") return;
    if (decoded && lower === "content-length") return;
    res.setHeader(name, value);
  });
  if (setCookies.length > 0) res.setHeader("set-cookie", setCookies);
}

async function waitForDrain(res) {
  // Resolves on drain, or when the client went away (a closed socket never drains).
  const controller = new AbortController();
  try {
    await Promise.race([
      once(res, "drain", { signal: controller.signal }),
      once(res, "close", { signal: controller.signal })
    ]);
  } finally {
    controller.abort();
  }
}

function sendJson(res, status, body) {
  if (res.headersSent) {
    res.destroy();
    return;
  }
  const payload = JSON.stringify(body);
  res.writeHead(status, {
    "content-type": "application/json; charset=utf-8",
    "content-length": Buffer.byteLength(payload),
    "cache-control": "no-store"
  });
  res.end(payload);
}
