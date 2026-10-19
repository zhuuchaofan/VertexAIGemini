// Starts the whole stack for the end-to-end tests and runs Playwright against it:
//   Firestore emulator (provided by `firebase emulators:exec`) -> API (Development, fake user, mock model)
//   -> Node proxy serving the built client under /app/ -> browser.
import { spawn, spawnSync } from "node:child_process";
import { setTimeout as sleep } from "node:timers/promises";
import { fileURLToPath } from "node:url";
import { resolve } from "node:path";

const here = fileURLToPath(new URL(".", import.meta.url));
const root = resolve(here, "../../../..");
const client = resolve(here, "..");
const apiPort = 5081;
const webPort = 5091;
const upstreamPort = 5099;
const children = [];

function start(command, args, options) {
  const child = spawn(command, args, { stdio: ["ignore", "pipe", "pipe"], ...options });
  const log = [];
  child.stdout.on("data", chunk => log.push(String(chunk)));
  child.stderr.on("data", chunk => log.push(String(chunk)));
  child.tail = () => log.join("").split("\n").slice(-30).join("\n");
  children.push(child);
  return child;
}

async function waitFor(url, label, child, accept = response => response.status > 0) {
  const deadline = Date.now() + 90_000;
  while (Date.now() < deadline) {
    if (child.exitCode !== null) throw new Error(`${label} exited early:\n${child.tail()}`);
    try {
      if (accept(await fetch(url))) return;
    } catch {
      // not up yet
    }
    await sleep(300);
  }
  throw new Error(`${label} did not start:\n${child.tail()}`);
}

function stopAll() {
  for (const child of children) child.kill();
}

try {
  // A second kind of provider, so the same chain is exercised with something other than the built-in mock.
  // Its own process: this script blocks while the tests run, so it could not answer from in here.
  const upstream = start("node", [resolve(here, "fake-openai-main.mjs")], { env: { ...process.env, FAKE_OPENAI_PORT: String(upstreamPort) } });
  await waitFor(`http://127.0.0.1:${upstreamPort}/__state`, "fake upstream", upstream);

  const build = spawnSync("npm", ["run", "build:e2e"], { cwd: client, stdio: "inherit" });
  if (build.status !== 0) throw new Error("client build failed");

  const api = start("dotnet", [resolve(root, "VertexAI/bin/Debug/net10.0/VertexAI.dll")], {
    cwd: resolve(root, "VertexAI"),
    env: {
      ...process.env,
      ASPNETCORE_ENVIRONMENT: "Development",
      ASPNETCORE_URLS: `http://127.0.0.1:${apiPort}`,
      Dev__FakeUser: "true",
      Runs__Enabled: "true",
      Runs__DisconnectGraceMilliseconds: "600",
      // The fake upstream is on this machine: never send it through an outbound proxy (as sandboxed machines have).
      NO_PROXY: "127.0.0.1,localhost",
      no_proxy: "127.0.0.1,localhost",
      Workspace__DefaultProviderId: "mock",
      // Web search for providers without their own: a deterministic stand-in (development only), no network.
      WebSearch__Backend: "fake",
      OpenAICompatible__Providers__0__Enabled: "true",
      OpenAICompatible__Providers__0__ProviderId: "deepseek",
      OpenAICompatible__Providers__0__Name: "Fake DeepSeek",
      OpenAICompatible__Providers__0__Endpoint: `http://127.0.0.1:${upstreamPort}/v1/chat/completions`,
      OpenAICompatible__Providers__0__ApiKey: "test-key",
      OpenAICompatible__Providers__0__ModelName: "deepseek-fake",
      OpenAICompatible__Providers__0__Models__0__Name: "Deepseek Fake",
      OpenAICompatible__Providers__0__Models__0__ModelName: "deepseek-fake",
      OpenAICompatible__Providers__0__Models__0__SupportsThinking: "true",
      OpenAICompatible__Providers__0__Models__0__MaxTokens: "32768",
      Quota__Enabled: "false",
      FIRESTORE_PROJECT_ID: `demo-e2e-${Date.now()}`
    }
  });
  await waitFor(`http://127.0.0.1:${apiPort}/api/auth/status`, "API", api);

  const web = start("node", ["server.mjs"], {
    cwd: resolve(root, "apps/web"),
    env: {
      ...process.env,
      PORT: String(webPort),
      HOST: "127.0.0.1",
      BACKEND_URL: `http://127.0.0.1:${apiPort}`,
      DISABLE_SERVERLESS_AUTH: "true",
      CLIENT_DIST_DIR: resolve(client, "dist-e2e")
    }
  });
  await waitFor(`http://127.0.0.1:${webPort}/app/`, "web proxy", web, response => response.ok);

  const tests = spawnSync("npx", ["playwright", "test", ...process.argv.slice(2)], {
    cwd: client,
    stdio: "inherit",
    env: { ...process.env, E2E_BASE_URL: `http://127.0.0.1:${webPort}` }
  });
  stopAll();
  process.exit(tests.status ?? 1);
} catch (error) {
  console.error(error instanceof Error ? error.message : error);
  stopAll();
  process.exit(1);
}
