import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize } from "node:path";

const port = Number.parseInt(process.env.PORT ?? "8080", 10);
const host = process.env.HOST ?? "0.0.0.0";
const backendUrl = resolveBackendUrl();
const publicDir = new URL("./public/", import.meta.url);

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

  const response = await fetch(target, {
    method: req.method,
    headers,
    body: ["GET", "HEAD"].includes(req.method ?? "GET") ? undefined : req,
    duplex: "half",
    redirect: "manual"
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

  for await (const chunk of response.body) {
    res.write(chunk);
  }

  res.end();
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

  try {
    const body = await readFile(fileUrl);
    const type = contentTypes[extname(fileUrl.pathname)] ?? "application/octet-stream";
    res.writeHead(200, {
      "content-type": type,
      "cache-control": "no-store, no-cache, must-revalidate, proxy-revalidate",
      "pragma": "no-cache",
      "expires": "0"
    });
    res.end(body);
  } catch {
    const fallback = await readFile(new URL("index.html", publicDir));
    res.writeHead(200, {
      "content-type": contentTypes[".html"],
      "cache-control": "no-store, no-cache, must-revalidate, proxy-revalidate",
      "pragma": "no-cache",
      "expires": "0"
    });
    res.end(fallback);
  }
}
