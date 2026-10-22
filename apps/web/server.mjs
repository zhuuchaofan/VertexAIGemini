import { createServer } from "node:http";
import { createHash } from "node:crypto";
import { readFile } from "node:fs/promises";
import { gzipSync } from "node:zlib";
import { extname, join, normalize, resolve } from "node:path";
import { pathToFileURL } from "node:url";

const port = Number.parseInt(process.env.PORT ?? "8080", 10);
const host = process.env.HOST ?? "0.0.0.0";
const backendUrl = resolveBackendUrl();
// The new Vite client is served under /app/. CLIENT_DIST_DIR overrides the location (used by tests).
const clientDir = process.env.CLIENT_DIST_DIR
  ? pathToFileURL(`${resolve(process.env.CLIENT_DIST_DIR)}/`)
  : new URL("./client/dist/", import.meta.url);
let serverlessTokenCache = null;

const contentTypes = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".json": "application/json; charset=utf-8",
  ".webmanifest": "application/manifest+json; charset=utf-8",
  ".svg": "image/svg+xml",
  ".png": "image/png",
  ".ico": "image/x-icon",
  ".woff2": "font/woff2",
  ".map": "application/json; charset=utf-8"
};

createServer(async (req, res) => {
  try {
    if (req.url?.startsWith("/api/")) {
      await proxyApi(req, res);
      return;
    }

    const pathname = new URL(req.url ?? "/", "https://cloud-run.invalid").pathname;
    if (pathname === "/app") {
      res.writeHead(308, { location: "/app/" });
      res.end();
      return;
    }

    if (pathname === "/healthz") {
      res.writeHead(200, { "content-type": "text/plain; charset=utf-8", "cache-control": "no-store" });
      res.end("ok");
      return;
    }

    if (pathname === "/") {
      res.writeHead(302, { location: "/app/", "cache-control": "no-store" });
      res.end();
      return;
    }

    if (pathname.startsWith("/app/")) {
      await serveStatic(req, res, clientDir, "/app");
      return;
    }

    res.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
    res.end("Not found");
  } catch (error) {
    console.error(error);
    if (!res.headersSent) {
      res.writeHead(500, { "content-type": "text/plain; charset=utf-8" });
    }
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

async function proxyApi(req, res) {
  const target = new URL(req.url, backendUrl);
  const headers = new Headers();

  for (const [name, value] of Object.entries(req.headers)) {
    if (value === undefined || name === "host" || name === "connection") {
      continue;
    }

    if (Array.isArray(value)) {
      for (const item of value) headers.append(name, item);
    } else {
      headers.set(name, value);
    }
  }

  // Cancel the upstream request as soon as the browser goes away, so the API sees a cancelled
  // request (and can stop the model call) instead of writing into a dead socket until it finishes.
  const upstream = new AbortController();
  const abortUpstream = () => {
    if (!res.writableFinished) upstream.abort();
  };
  res.on("close", abortUpstream);

  try {
    await addServerlessAuthorization(headers);

    const response = await fetch(target, {
      method: req.method,
      headers,
      body: ["GET", "HEAD"].includes(req.method ?? "GET") ? undefined : req,
      duplex: "half",
      redirect: "manual",
      signal: upstream.signal
    });

    res.statusCode = response.status;
    const setCookies = response.headers.getSetCookie?.() ?? [];
    response.headers.forEach((value, name) => {
      if (name === "transfer-encoding" || name === "content-encoding" || name === "set-cookie") {
        return;
      }

      res.setHeader(name, value);
    });
    if (setCookies.length > 0) {
      res.setHeader("set-cookie", setCookies);
    }

    if (!response.body) {
      res.end();
      return;
    }

    // Send headers now so event streams start immediately, then relay chunks as they arrive.
    res.flushHeaders();
    for await (const chunk of response.body) {
      if (res.destroyed) break;
      res.write(chunk);
    }

    res.end();
  } catch (error) {
    // A browser disconnect is not an error: the upstream was cancelled on purpose.
    if (upstream.signal.aborted || res.destroyed) {
      if (!res.destroyed) res.destroy();
      return;
    }
    throw error;
  } finally {
    res.off("close", abortUpstream);
  }
}

async function addServerlessAuthorization(headers) {
  const token = await getServerlessIdentityToken();
  if (token) {
    headers.set("x-serverless-authorization", `Bearer ${token}`);
  }
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

async function serveStatic(req, res, root, prefix) {
  const fullPath = new URL(req.url ?? "/", "https://cloud-run.invalid").pathname;
  const requestPath = fullPath.startsWith(prefix) ? fullPath.slice(prefix.length) || "/" : fullPath;
  const safePath = normalize(decodeURIComponent(requestPath)).replace(/^(\.\.[/\\])+/, "");
  const relativePath = safePath === "/" ? "index.html" : safePath.slice(1);
  const fileUrl = new URL(join(root.pathname, relativePath), "file://");

  if (!fileUrl.pathname.startsWith(root.pathname)) {
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
    try {
      body = await readFile(new URL("index.html", root));
    } catch {
      res.writeHead(404, { "content-type": "text/plain; charset=utf-8" });
      res.end("Not found");
      return;
    }
    type = contentTypes[".html"];
  }

  // Vite fingerprints everything under assets/, so those files can be cached forever.
  sendStatic(req, res, body, type, relativePath.startsWith("assets/"));
}

// Always revalidate (ES module imports carry no version query), but answer 304 via ETag and gzip text.
function sendStatic(req, res, body, type, immutable = false) {
  const etag = `"${createHash("sha1").update(body).digest("base64url")}"`;
  const headers = {
    "content-type": type,
    "cache-control": immutable ? "public, max-age=31536000, immutable" : "no-cache",
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
