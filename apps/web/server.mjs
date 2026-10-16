import { createServer } from "node:http";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { gzipSync } from "node:zlib";
import { extname, join, normalize } from "node:path";
import { createApiProxy } from "./proxy.mjs";

const port = Number.parseInt(process.env.PORT ?? "8080", 10);
const host = process.env.HOST ?? "0.0.0.0";
const backendUrl = resolveBackendUrl();
const publicDir = new URL("./public/", import.meta.url);
let serverlessTokenCache = null;

// Keep this below the Cloud Run request timeout of this service (see docs/cloud-run.md): the proxy then
// reports a clean gateway timeout instead of the platform cutting the connection mid-stream.
const upstreamTimeoutMs = Number.parseInt(process.env.UPSTREAM_TIMEOUT_MS ?? "295000", 10);
const serverlessAuthDisabled = process.env.DISABLE_SERVERLESS_AUTH === "true";

const proxyApi = createApiProxy({
  backendUrl,
  getIdentityToken: getServerlessIdentityToken,
  requireIdentityToken: !serverlessAuthDisabled,
  upstreamTimeoutMs
});

const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8"
};

createServer(async (req, res) => {
  try {
    if (req.url?.startsWith("/api/")) {
      await proxyApi(req, res);
      return;
    }

    await serveStatic(req, res);
  } catch (error) {
    console.error(error);
    if (res.headersSent) {
      // Never append text to a response that already started (it could be an SSE stream).
      res.destroy(error instanceof Error ? error : undefined);
      return;
    }
    res.writeHead(500, { "content-type": "text/plain; charset=utf-8" });
    res.end("Internal server error");
  }
}).listen(port, host, () => {
  console.log(`Web workspace listening on http://${host}:${port}`);
  console.log(`Proxying /api requests to ${backendUrl.origin}`);
});

function resolveBackendUrl() {
  if (process.env.BACKEND_URL) {
    return new URL(process.env.BACKEND_URL);
  }

  throw new Error("BACKEND_URL is required.");
}

async function getServerlessIdentityToken() {
  if (process.env.DISABLE_SERVERLESS_AUTH === "true") {
    return null;
  }

  const now = Date.now();
  if (serverlessTokenCache && serverlessTokenCache.expiresAt > now + 60_000) {
    return serverlessTokenCache.token;
  }

  const metadataUrl = new URL("http://metadata.google.internal/computeMetadata/v1/instance/service-accounts/default/identity");
  metadataUrl.searchParams.set("audience", backendUrl.origin);
  metadataUrl.searchParams.set("format", "full");

  try {
    const response = await fetch(metadataUrl, {
      headers: { "Metadata-Flavor": "Google" }
    });
    if (!response.ok) {
      return null;
    }

    const token = await response.text();
    serverlessTokenCache = {
      token,
      expiresAt: now + 50 * 60 * 1000
    };
    return token;
  } catch {
    return null;
  }
}

async function serveStatic(req, res) {
  const requestPath = new URL(req.url ?? "/", "https://cloud-run.invalid").pathname;
  const safePath = normalize(decodeURIComponent(requestPath)).replace(/^(\.\.[/\\])+/, "");
  const relativePath = safePath === "/" ? "index.html" : safePath.slice(1);
  const fileUrl = new URL(join(publicDir.pathname, relativePath), "file://");

  if (!fileUrl.pathname.startsWith(publicDir.pathname)) {
    res.writeHead(403);
    res.end("Forbidden");
    return;
  }

  let body;
  let type;
  try {
    body = await readFile(fileUrl);
    type = contentTypes[extname(fileUrl.pathname)] ?? "application/octet-stream";
  } catch {
    // Unknown asset-looking paths are real 404s; extensionless routes fall back to the SPA shell.
    if (extname(fileUrl.pathname)) {
      res.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
      res.end("Not found");
      return;
    }
    body = await readFile(new URL("index.html", publicDir));
    type = contentTypes[".html"];
  }

  sendStatic(req, res, body, type);
}

// Always revalidate (ES module imports carry no version query), but answer 304 via ETag and gzip text.
function sendStatic(req, res, body, type) {
  const etag = `"${createHash("sha1").update(body).digest("base64url")}"`;
  const headers = {
    "content-type": type,
    "cache-control": "no-cache",
    "etag": etag,
    "vary": "Accept-Encoding",
    "x-content-type-options": "nosniff",
    "referrer-policy": "strict-origin-when-cross-origin",
    "x-frame-options": "DENY"
  };

  if (req.headers["if-none-match"] === etag) {
    res.writeHead(304, headers);
    res.end();
    return;
  }

  const compressible = type.startsWith("text/") || type.startsWith("application/json");
  if (compressible && /\bgzip\b/.test(req.headers["accept-encoding"] ?? "")) {
    body = gzipSync(body);
    headers["content-encoding"] = "gzip";
  }

  headers["content-length"] = body.length;
  res.writeHead(200, headers);
  res.end(req.method === "HEAD" ? undefined : body);
}
