# 批次 0 报告：本地可验证部分

日期：2026-10-17。范围：安装 SDK、代理取消测试、假上游请求计数、Firestore 模拟器、Vite 骨架，以及批次 1 之前的修正所需的契约测试。
**没有部署任何东西，没有改动任何云资源，没有调用真实模型，没有使用任何凭据。**

## 1. 结果一览

| 套件 | 命令 | 结果 |
|---|---|---|
| 后端（无模拟器） | `dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj` | 47 通过，10 跳过（需要模拟器） |
| 后端（有模拟器） | `firebase emulators:exec … "dotnet run … --no-build"`，见第 5 节 | **57 通过，0 跳过**（连续多次） |
| Web 代理与旧界面 | `cd apps/web && npm test` | 21 通过 |
| 新前端 | `cd apps/web/client && npm run typecheck && npm run lint && npm test && npm run build` | 全部通过 |
| 部署脚本 | `python3 scripts/test-deploy-cloud-run.py` | 10 通过（新增 4 条） |

基线：开始前现有 40 个后端测试在本地全部通过，说明新增内容没有破坏旧行为。

## 2. 环境事实（包括被拒绝和不可用的）

| 项 | 结果 |
|---|---|
| .NET SDK | 官方安装脚本所在的 `dot.net`、`builds.dotnet.microsoft.com`、`dotnetcli.azureedge.net`、`download.visualstudio.microsoft.com` 被网络策略拒绝（403）。**没有重试。** 改用 Ubuntu 软件源：`apt-get install dotnet-sdk-10.0`，得到 10.0.112，与项目 `net10.0` 一致 |
| `gcloud` 组件（含 Firestore 模拟器） | `dl.google.com` 被拒绝（403），`gcloud components` 不可用。**没有重试。** 改用 npm 的 `firebase-tools@14`，它从 `storage.googleapis.com` 取模拟器 JAR，可用 |
| Docker | 二进制存在，**守护进程不可用**，无法执行真正的 `docker build`。改为逐步复现（见第 7 节），并把真正的构建放进 CI |
| Java | 21（模拟器需要），可用 |

## 3. Node 代理：断开传播

**复现（修改前）。** 先写测试 `apps/web/test/proxy.test.mjs`，在未修改的 `server.mjs` 上运行：

```
not ok 1 - client disconnect during an SSE stream is propagated to the upstream within 1s
not ok 2 - client disconnect before the upstream has responded cancels the pending upstream request
ok 3 - normal responses pass through unchanged and are not cancelled
ok 4 - SSE events reach the client incrementally, not only when the response ends
not ok 5 - a client abort does not crash the proxy
```

与代码阅读的结论一致：代理转发时没有 `signal`，浏览器断开后上游一直写到结束。

**修复后：** 5 个全部通过。改动：为上游 `fetch` 加 `AbortController`，下游 `res` 的 `close`（且响应未写完）时中止；断开不当作错误记录；先 `flushHeaders()` 让事件流立即开始。

同一文件还增加 `/app/` 路由的两条测试：Vite 构建产物、客户端路由回退到 `index.html`、`assets/` 下的哈希文件 `immutable` 缓存、不存在的资源 404、`/app` 308 到 `/app/`、路径穿越被拒、旧界面 `/` 不受影响；未构建时 `/app/` 返回 404。合计 Web 21 个测试通过。

## 4. Gemini SDK（Google.GenAI 1.26.1）

测试文件：`VertexAI.Tests/GeminiRetryTests.cs`。用本地 Kestrel 假上游统计 SDK 实际发出的请求。**API Key 模式**，因为 Vertex 模式需要凭据；两种模式共用同一个 HTTP 层，但**真实 Vertex 端点上的行为仍需一次授权调用确认**。

### 4.1 一次调用一个请求

| 上游返回 | 流式生成 | 非流式生成 | 令牌计数 |
|---|---|---|---|
| 503 | 1 | 1 | 1 |
| 429 | 1 | 1 | 1 |
| 500 | 1 | 1 | 1 |
| 连接重置 | 1 | 1 | 1 |

默认配置本来就不重试；我仍然显式设置 `RetryOptions.Attempts = 1`（`GeminiHttpOptions`），使「总尝试一次、零自动重试」成为代码里的约定而不是 SDK 默认值的巧合。**证明测试不是空转：** 同一套件里 `Attempts = 3` 时对 503 确实看到多于 1 个请求。

### 4.2 取消

| | 取消令牌到枚举结束 | 本地假上游观察到连接关闭 | 取消后假上游继续写入的分片 |
|---|---|---|---|
| 默认 .NET 处理器 | 约 4 毫秒 | **2004 毫秒后** | 64 个 |
| `ResponseDrainTimeout = 0`（现用于所有 Gemini 客户端） | 约 4 毫秒 | **1 到 3 毫秒后** | 0 |

2 秒的来源：.NET 的 `SocketsHttpHandler.ResponseDrainTimeout` 默认 2 秒，丢弃未读完的 HTTP/1.1 响应时连接继续接收。无论用取消令牌、`break` 还是显式释放枚举器结果相同。**这组数字只证明客户端侧的连接关闭行为**：默认处理器在取消后还让 TCP 连接保持约 2 秒，假上游因此又写了 64 个分片。**它不能证明真实的 Google 服务在这 2 秒里是否继续生成、是否计费**，假上游没有模型也没有账单。能确定的只有：不修的话，取消后的连接会多开约 2 秒；是否产生真实成本、成本多少，要等真实调用实测（见真实调用验证计划）。改用 `ClientOptions.HttpClientFactory` 提供共享的 `SocketsHttpHandler`（`disposeHandler: false`，连接池复用）修复，测试固定为「关闭延迟小于 500 毫秒」。

### 4.3 用量字段

`GenerateContentResponse.UsageMetadata` 存在，含 `PromptTokenCount`、`CandidatesTokenCount`、`ThoughtsTokenCount`、`TotalTokenCount` 等。反射确认；**真实响应里是否填充要等一次真实调用**。

### 4.4 一次运行里不止一次供应商调用

读代码发现 `ChatHistoryManager.TrimIfNeededAsync` 在主生成之前调用 `CountTokensAsync`，超阈值时还调用 `GenerateContentAsync` 生成摘要，之后又一次 `CountTokensAsync`。这改变了「至多一次」的定义（方案 2.3.4）：按模型会话定义，辅助调用同样带取消令牌并计入 `providerCalls`。

## 5. Firestore 模拟器与契约测试

文件：`VertexAI.Tests/FirestoreEmulatorContractTests.cs`，`VertexAI.Tests/firebase.emulator.json`。未设置 `FIRESTORE_EMULATOR_HOST` 时标为跳过，不影响 `dotnet run`。

```bash
firebase emulators:exec --only firestore --project demo-contract-tests \
  --config VertexAI.Tests/firebase.emulator.json \
  "dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj --no-build"
```

| 契约测试 | 证明了什么 |
|---|---|
| create 在文档已存在时失败 | 创建即抢占 |
| 50 个并发准入恰好 1 个赢家 | 单赢家准入（含读回协议） |
| 应答丢失的重试被识别为 `Admitted` | 否则没人执行 |
| 并发终结者是比较并设置 | 只有一个终态写入成功 |
| 租约持有者围栏过期写入者 | 僵尸心跳不能改动终态 |
| 执行权标记只能由租约持有者设置一次 | 2.3.3 |
| 对账器先赢则调用 0 次且标记仍为 `not_started` | 确定性证据 |
| 标记与终结并发 10 轮 | 「复核成功当且仅当标记为 started」 |
| 12 个并发重试只有一个后继，复用同一用户消息 | 4.1 |
| 来源仍在运行或已有内容时拒绝重试 | 4.1 |

**一个重要的发现。** 第一版并发准入测试把「事务抛异常」当作「没抢到」，结果某一次运行里 50 个事务全部报 `Transaction lock timeout`，测试失败。这不是模拟器的缺陷，而是真实行为：争用下事务可能整体被中止。结论写进了协议（方案 2.1、2.3）：**事务失败是「未知」，必须读回确认；读不到就返回 503，让客户端用同一个 `runId` 重试。** 测试改为验证这个协议，连续运行中出现过一次「49 次事务失败、仍恰好 1 个赢家」的情形，通过。

## 6. 限流叠加（真实 ASP.NET 中间件）

文件：`VertexAI.Tests/RateLimitStackingTests.cs`。

| 做法 | 实测 |
|---|---|
| 命名端点策略（预算 10）+ 全局限流（预算 3） | 只放行 3 次：**命名策略叠加，不能放宽** |
| `DisableRateLimiting()` | 5 次全部放行：**全局限流也被绕过，完全不限流** |
| 在全局限流器内按路由分类 | 控制类 10 次全部通过，且不占用其他接口的预算；创建运行仍受原限制 |

注意：我最初假设 `DisableRateLimiting()` 不会绕过全局限流，**测试证明这个假设是错的**，所以方案没有照这个假设写，而是按实测结果采用第三种。

## 7. Vite 骨架与 Dockerfile

`apps/web/client/`：

- 依赖（`package-lock.json` 已提交）：react 19.3.1、react-dom 19.3.1；开发：vite 8.3.4、@vitejs/plugin-react 6.1.2、typescript（解析为 6.0.x）、vitest 5.0.5、jsdom、@testing-library/react 与 jest-dom、eslint 10 与 typescript-eslint、eslint-plugin-react-hooks、@types/node 22。只装了骨架需要的，没有提前引入状态管理、路由等库。
- `typecheck`、`lint`、`test`、`build` 全部通过；构建产物约 220 KB（gzip 69 KB）。
- 视觉稿的色板直接作为 `tokens.css`（浅色、跟随系统的深色、显式深色）。

Dockerfile 变为多阶段。**沙箱里 Docker 守护进程不可用，所以用 `tar` 按 `.dockerignore` 排除后复制构建上下文，在干净目录里逐步复现：**

1. `npm ci`（只复制 `package.json` 与 lockfile）：添加 234 个包，成功，证明 lockfile 可复现；
2. `npm run build`：成功；
3. 按运行阶段的布局组装 `/app`：`server.mjs`、`public`、`client/dist`；
4. 启动 `server.mjs` 并请求：

```
/                       -> 200 no-cache
/app/                   -> 200 no-cache
/app/c/abc              -> 200 no-cache              （客户端路由回退）
/app/assets/index-….js  -> 200 public, max-age=31536000, immutable
```

`.dockerignore`、`.gcloudignore` 排除 `client/node_modules`、`client/dist`、`client/coverage`；根 `.gitignore` 增加 `node_modules/`、`apps/web/client/dist/`。

## 8. 部署脚本与超时

新增 4 条测试，共 10 条通过：默认值得到 `Runs__MaxDurationSeconds=270` 且 Web 超时为 300 秒；`WEB_TIMEOUT=60s` 在任何变更之前被拒绝（只做了一次服务查询）；`5m` 这类无法解析的值被拒绝；镜像更新路径不带任何 `--timeout`，也不受校验影响。工作流 YAML 解析通过，脚本 `bash -n` 通过。**没有执行任何 gcloud 变更。**

## 9. CI

`.github/workflows/ci.yml` 新增三个 job：`api-firestore-emulator`（Java 21、Node 24、用 `firebase emulators:exec` 跑完整后端测试）、`web-client`（`npm ci`、类型检查、lint、测试、构建）、`web-docker`（真正的 `docker build` 与冒烟）；`web-test` 增加 Node 22 与 24 的矩阵。YAML 已解析验证。

**这些 job 目前一次都没有真正跑过：** `ci.yml` 只在 `pull_request` 和 `main` 上触发，分支推送不会触发。上面所有结果都来自本地。

## 10. 没有做、不能确认的事

- 真实 Vertex 端点上的取消、重试、用量字段：需要凭据和一次计费调用，**待你授权**。
- 请求超时是否计入流式响应的整个持续时间：需要在无流量的标签修订上实测，涉及部署，**待你授权**。
- 线上把 Web 超时提到 300 秒：**待你确认**，命令在方案 2.10。
- 真正的 `docker build` 与 CI：**需要开 PR 才会运行**。
- Node 24 下的 Web 测试：本地只有 Node 22，CI 矩阵会覆盖 24。
- 备份与恢复手册里的命令：**一个都没运行**。
- `HttpClient` 工厂换成共享处理器后，在真实 Vertex 认证下是否完全正常：只能在第一次真实调用时验证（认证由 SDK 在请求层加头，理论上与处理器无关）。
