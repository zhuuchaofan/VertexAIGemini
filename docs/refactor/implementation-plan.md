# 聊天工作台重构：实现方案

状态：待确认。本文只是方案，没有任何实现代码，也没有部署任何东西。
视觉稿：[`docs/design/design-board.html`](../design/design-board.html)，实际尺寸截图见 [`docs/design/screenshots/`](../design/screenshots/)。

## 0. 摘要

1. **运行（Run）是一等对象。** 每次发送先由浏览器生成 `runId`，服务端以它做幂等键，并以它派生所有 ID。同一个 `runId` 最多执行一次模型调用、一次配额预留、写入一条用户消息、一条助手消息。
2. **终态四种：** `completed`、`stopped`（用户主动）、`interrupted`（连接中断或服务端丢失）、`failed`。终态一经写入不再改变。
3. **停止是显式的。** 浏览器先调用 `POST …/stop`，再中断流。后端取消模型请求，把已生成的部分保存为助手消息（`status=partial`）。
4. **运行记录独立于消息。** 存在新集合 `runs`，保存请求、实际生效配置、状态、耗时、用量和错误。助手消息只保存内容。
5. **第一批交付是一条垂直链路：** 创建运行、流式、停止、中断、重新查询、幂等，从 API 到一个最小的新前端壳，而不是重写所有页面。旧界面和旧接口在第一批里不动。
6. **回滚靠开关和叠加式数据。** 所有 Firestore 变更都是可选字段或新集合，旧代码读到会忽略。

### 需要你确认的决策（附默认值）

| # | 决策 | 默认 |
|---|---|---|
| D1 | 新前端路径用 `/app/`，上线后 `/` 重定向到 `/app/`，旧界面移到 `/legacy/` | 同意 |
| D2 | 用户主动停止之外，连接中断一律不继续生成（取消模型请求，保存部分内容） | 同意 |
| D3 | 单次生成服务端上限 270 秒（Cloud Run 当前请求超时 300 秒），超时记为 `failed(timeout)`，已生成部分照常保存 | 同意，不改部署配置 |
| D4 | 一个会话同时只允许一个进行中的运行，第二个返回 409 | 同意 |
| D5 | 已失败且没有产生内容的运行允许「重试」；已有部分内容（停止或中断）的运行本阶段**不提供重试**，用户直接发新消息继续，部分内容作为上下文 | 同意（这是与上一版视觉稿的差异，已更新截图） |
| D6 | 旧接口 `/api/chat/stream` 在第三批改为调用新的运行服务（只维护一套内部实现），第四批评估后下线 | 同意 |
| D7 | 本地开发：新增仅开发环境、需显式开关的假用户上下文和 Mock 提供商注册（Mock 客户端现在没有在 DI 中注册，只用于测试） | 同意 |
| D8 | 首次在生产启用 v2 之前，是否先做一次 Firestore 托管导出作为备份（只增不删，不是必需，但成本很低） | 建议做，需你确认 |

## 1. 已核实的现状事实

下面这些是读代码得到的，方案以它们为前提。

| 事实 | 位置 | 影响 |
|---|---|---|
| 模型客户端接口没有取消令牌：`StreamChatAsync(ChatModelRequest)`。OpenAI 兼容客户端内部有带令牌的私有重载，但公开入口传的是 `CancellationToken.None`。Gemini 的流式调用没有传令牌 | `IChatModelClient.cs`、`OpenAICompatibleChatModelClient.cs:64`、`GeminiService.cs:222` | 必须扩展接口并贯穿，这是停止生成的前提 |
| 模型客户端是**有状态的**，每次请求新建，`LoadHistoryAsync` 灌入历史 | `ChatOrchestrator.SendAsync` | 上下文选取在加载前完成，客户端本身不改 |
| 先写用户消息，再调模型；失败时不保存已生成内容；用户消息会成为没有回复的孤儿 | `ChatOrchestrator.cs` | 孤儿用户消息现在会进入后续历史，新方案按运行状态排除 |
| 新会话的 `conversationId` 由服务端在请求中生成，浏览器要等 `final` 才知道 | `EnsureConversationAsync` | 重试首条消息无法幂等，新方案改为浏览器生成 |
| 配额在模型调用前预留（事务），之后记录 token，没有释放机制 | `FirestoreChatQuotaService.cs` | 重复提交会重复扣额度，必须在配额之前做幂等 |
| `update` 事件每次发送累计全文，不是增量 | `ChatEndpoints.cs` | v2 改为增量 `delta` |
| Node 代理转发请求时没有传递客户端断开：`fetch` 没有 `signal`，用 `for await` 一直写 | `apps/web/server.mjs` `proxyApi` | 停止和断开不会到达后端，必须修 |
| 全局限流：每个令牌或 IP 每分钟 30 次，作用于所有接口 | `ServiceCollectionExtensions.cs:46` | 轮询运行状态和停止会被限流，需要单独策略 |
| API 请求超时 300 秒，最小实例 0，CPU 在请求之外会被限制 | `scripts/deploy-cloud-run.sh` | 请求结束后不能指望后台继续生成 |
| Firestore 集合：`conversations`、`messages`、`usageQuotas`。没有用户设置集合 | 代码检索 | 用户默认设置需要新集合 |
| 历史按 `uid + conversationId + createdAt` 索引，取最近 N 条 | `firestore.indexes.json` | 新索引只增不删 |
| 当前沙箱**没有 .NET SDK** | `which dotnet` | 后端验证依赖 CI，或先在沙箱安装 SDK，见第 8 节批次 0 |

## 2. 运行生命周期

### 2.1 标识与幂等

- `runId`：浏览器为每次「发送」意图生成的 UUIDv4，保存在草稿的待发送记录里。**只有同一意图的重放才复用它**；用户主动重试是新的 `runId`。
- 新会话的 `conversationId` 同样由浏览器生成。服务端只接受两种情况：文档不存在（创建，归当前用户），或存在且属于当前用户。属于他人时返回 409，不泄露细节。
- 服务端派生 ID（UUIDv5，固定命名空间）：
  `userMessageId = v5(runId + ":user")`，`assistantMessageId = v5(runId + ":assistant")`。重放时不需要读库就能得到同样的 ID，消息写入用 `Set`，重复写入是覆盖而不是新增。
- **指纹**：对 `{conversationId, message, 附件内容哈希列表, 规范化后的请求设置}` 做 SHA-256，存入 `runs/{runId}.fingerprint`。
- 重放规则：

| 情况 | 响应 |
|---|---|
| `runId` 不存在 | 正常创建并执行 |
| 存在，同一用户，指纹相同 | 不执行任何东西。返回 `run` 快照事件，再返回 `end{replayed:true}`；终态则带 `done`。客户端改用轮询 |
| 存在，同一用户，指纹不同 | 409 `idempotency_mismatch` |
| 存在，属于其他用户 | 409 `conflict`，不泄露 |

- **保证：同一 `runId` 至多一次执行。** 歧义（响应丢了、不知道成没成）只能靠查询状态消除，不会靠静默重新执行消除。
- 双击和多标签：前端按会话维度做进行中锁，服务端靠幂等和「单会话单运行」兜底（D4）。
- 浏览器自动重试：只在**收到首个字节之前**的网络错误上重试，最多 2 次，沿用同一个 `runId`。收到 `run` 事件之后不再自动重试，改走对账。

### 2.2 状态机

```
            ┌────────── 配额/校验/配置失败 ──────────┐
queued ──► running ──► completed
              │  └──► stopped        (用户显式停止，且已观察到取消)
              │  └──► interrupted    (连接中断，或租约过期)
              └─────► failed         (供应商错误、安全拦截、空响应、超时、配额)
```

`stopping` 不是存储状态，是派生状态：`running` 且 `stopRequestedAt` 非空。终态不可变，唯一写入者通过事务做比较并设置（CAS），谁先写谁赢，其余是无操作。

| 终态 | `statusReason` | 触发 | 助手消息 |
|---|---|---|---|
| `completed` | — | 流正常结束且内容非空 | `status=final` |
| `stopped` | `user` | 收到显式停止，模型调用已取消 | 有内容则 `status=partial` |
| `interrupted` | `connection` | `RequestAborted` 且 2 秒宽限内没有收到停止 | 有内容则 `status=partial` |
| `interrupted` | `server_lost` | 租约过期，由对账器写入 | 用最近一次检查点的内容 |
| `failed` | `quota` / `safety` / `provider_error` / `empty_response` / `timeout` / `validation` / `config` / `permission` / `internal` | 异常或规则拒绝 | 有内容则 `partial`，没有则不写 |

没有内容时不写助手消息。UI 用运行记录渲染错误卡，不依赖消息。

### 2.3 创建流程（服务端）

1. **校验**：认证、请求体、附件校验、`Advanced.Validate`、安全阈值权限。失败直接返回 4xx，**不创建运行**。沿用现有校验和现有错误文案。
2. **附件**上传到 GCS，对象名由 `conversationId/userMessageId/index` 确定，重放覆盖同名对象。事务被拒绝时尽力删除。
3. **一个 Firestore 事务**：
   - 读 `runs/{runId}`，存在则走重放规则。
   - 读（或创建）`conversations/{id}`，校验归属。
   - 读 `activeRunId` 指向的运行：未终态且租约未过期则拒绝（409 `run_in_progress`）；租约已过期则先对账它。
   - 写入：`runs/{runId}`（`running`、`fingerprint`、`requested`）、用户消息（确定的 ID，带 `runId`）、`conversations.activeRunId`、必要时创建会话并写入 `settings`。
4. **配额预留**（事务之外）。超额则运行转 `failed(quota)`，用户消息保留，不调用模型。配额预留只会发生在创建成功的那一次调用里，所以天然只发生一次。
5. 若此时请求已被取消（浏览器在创建期间中断）：不调用模型，直接按 `interrupted/connection` 或 `stopped` 终结，没有内容。
6. 构建模型客户端，用 `RunContextSelector` 选取上下文，开始流式。
7. 流式循环里并行三件事（见 2.4）。
8. **终结事务**（见 2.5）。

### 2.4 流式期间

- 取消令牌是三者的联合：`RequestAborted`、停止信号、服务端 270 秒上限。
- **停止检查**：每 1 秒读一次 `runs/{id}.stopRequestedAt`，同时检查进程内的取消注册表（同实例时立即生效）。多实例下停止请求可能落在另一个实例，所以必须经 Firestore 传递。
- **心跳**：每 5 秒写 `heartbeatAt`，并在内容有变化时写检查点 `partial{content, thinking, seq, updatedAt}`（上限 512 KB）。
- **租约**：`heartbeatAt` 超过 30 秒视为失联。
- SSE 注释行 `: ping` 每 15 秒一次，防止中间代理断开空闲连接。

### 2.5 终结事务与取消后的持久化

- 终结用 `CancellationToken.None` 并设 15 秒上限，**不能**用已取消的请求令牌，否则取消后写不进去。
- 单个事务内：读运行（已终态则无操作）；写助手消息（确定的 ID，有内容才写，`status=final|partial`）；写运行终态字段（状态、原因、耗时、用量、错误、`output` 统计）并清除 `partial`；更新会话的 `updatedAt`、`tokenCount`，并在 `activeRunId == runId` 时清除它。
- Token 记录：已完成沿用现有计数。中断、停止、失败时按已生成内容估算，运行里标记 `usage.source = "estimate"`。**配额不退还**：模型调用已经开始，供应商可能已计费。只有「在调用模型之前就失败」的运行（校验、配额）没有预留，所以也无需退还。

### 2.6 重新查询与对账

- `GET /api/v2/runs/{runId}` 返回快照。读的时候如果发现 `running` 且租约过期，**先对账再返回**：CAS 写 `interrupted/server_lost`，用检查点内容写部分助手消息。并发 GET 只有一个会赢，结果一致。
- 会话详情（`GET /api/v2/conversations/{id}`）同样会对账该会话的 `activeRunId`。
- **客户端对账**（流结束但没有收到 `done`，或收到 `run` 之后 fetch 出错）：
  1. UI 显示「正在确认结果…」，这是真实状态，不推测阶段。
  2. 以 1、1、2、2、4 秒间隔轮询，最长 60 秒，直到终态。
  3. 终态后用快照覆盖本地的临时内容，把助手消息拉回来。
  4. 如果始终没收到过 `run` 事件且 GET 返回 404，说明请求没到服务端：把消息放回输入框，允许用**同一个 `runId`** 重新发送，这是安全的。
- 页面刷新：草稿存储里记着「待确认的 `runId`」，启动时先对账，再决定是否恢复为草稿。

### 2.7 停止与中断的区分

浏览器只靠断开连接无法区分「用户停止」和「网络断了」。流程：

```
点击停止
  ├─ POST /api/v2/runs/{runId}/stop       （最多等 3 秒，失败则继续）
  └─ AbortController.abort()              （中断 SSE 流）

服务端：
  stop 到达  → 写 stopRequestedAt，取消令牌 → 终结为 stopped(user)
  流先断开    → 等待 2 秒宽限，期间若看到 stopRequestedAt → stopped，否则 interrupted(connection)
  停止晚于终态 → 返回当前终态，不报错
  停止早于创建 → 404，客户端在创建在途时重试停止最多 3 秒
```

**Node 代理必须配合**：监听下游 `res.on('close')`，在响应未结束时对上游 `fetch` 调用 `abort()`；SSE 响应不缓冲。这是批次 0 的第一项，并有自动化测试。

### 2.8 SSE v2 事件

`POST /api/v2/runs` 返回 `text/event-stream`，事件按下面的名字和形状。所有时间戳是服务端 ISO 8601。

| 事件 | 载荷 | 说明 |
|---|---|---|
| `run` | `{runId, conversationId, userMessageId, assistantMessageId, status, replayed}` | 第一个事件，收到即表示服务端已接受 |
| `delta` | `{offset, content?, thinking?}` | 增量文本，`offset` 为已发送内容的 UTF-16 长度，预留给以后可续传 |
| `citations` | `{items:[{title, uri}]}` | 引用到达时发送，只有真的有才发 |
| `meta` | `{effective, finishReason?, usage?}` | `effective` 为服务端实际生效的配置；`usage` 只在供应商给出时存在 |
| `done` | 运行快照（含终态和错误） | 终止事件 |
| `end` | `{replayed?}` | 重放时没有执行，只告知结束 |

没有「阶段」事件。**在后端真的提供阶段事件之前，UI 只显示「正在生成」、浏览器计时、已收到字数**，收到思考内容或引用时才多一行对应提示。

### 2.9 限流

现有全局限流每个令牌每分钟 30 次，会让轮询和停止失败。新增命名策略 `runs-control`（每分钟 120 次，按用户），应用于 `GET /runs/{id}`、`POST …/stop`。`POST /runs` 仍受原策略约束。

## 3. Firestore 字段

所有变更只增不改。旧文档没有这些字段，读取时用默认值。

### 3.1 新集合 `runs/{runId}`

```
schemaVersion        1
uid, localUserId
conversationId, turnId, attempt (1), trigger ("send"), retryOfRunId?
userMessageId, assistantMessageId
status               queued | running | completed | stopped | interrupted | failed
statusReason         user | connection | server_lost | quota | safety | provider_error |
                     empty_response | timeout | validation | config | permission | internal
fingerprint          sha256
requested            { providerId, modelName, presetId, customPrompt?, searchMode,
                       thinking{enabled?, level?, budget?},
                       advanced{temperature?, topP?, maxOutputTokens?, safetyThreshold?} }
effective            { 同上形状；服务端解析后的值；不适用的参数不写 }   （meta 事件写入）
attachments          [{fileName, mimeType, sizeBytes, storageObjectName}]   （不含 base64）
timing               { createdAt, startedAt?, firstOutputAt?, endedAt? }    （均为服务端时间）
usage?               { inputTokens?, outputTokens?, source: "provider" | "estimate" }
finishReason?        供应商给出时才写
error?               { code, message, retryable }
output               { chars, thinkingChars, citations }
stopRequestedAt?, heartbeatAt, leaseOwner
partial?             { content, thinking, seq, updatedAt }                 （终态时清除）
createdAt, updatedAt
```

- **缺失就是缺失**：没有拿到的值（用量、结束原因、模型处理耗时）不写字段，检视器显示「未知」，不填 0。
- 浏览器侧的耗时（发出请求到首字、到完成）只存在于浏览器，**不写进 `timing`**。检视器把两者分开标注来源。
- `requested.customPrompt` 保存提示词原文，与现有 `conversation.customPrompt` 一致，是用户自己的数据。

### 3.2 `messages/{id}` 新增可选字段

```
runId?, turnId?
status?        "final" | "partial"       缺省按 final 处理
revision?, supersededByMessageId?, selectedRunId?     （预留，见第 4 节，本阶段不会被写入）
```

### 3.3 `conversations/{id}` 新增可选字段

```
settings?      { version, providerId, modelName, presetId, customPrompt?, searchMode,
                 thinking?, advanced?, updatedAt }
activeRunId?
```

旧会话没有 `settings`：用现有的 `providerId`、`modelName`、`presetId`、`customPrompt` 兜底，搜索模式和思考取用户默认。

### 3.4 新集合 `userSettings/{uid}`

```
schemaVersion, chatDefaults { 同 settings 形状，不含 customPrompt }, updatedAt
```

### 3.5 三层设置的语义

| 层 | 存放 | 谁写 | 用途 | 可变 |
|---|---|---|---|---|
| 我的默认 | `userSettings/{uid}.chatDefaults` | 用户在检视器「我的默认」保存 | 创建新会话时初始化 | 可 |
| 此会话 | `conversations/{id}.settings` | 用户改设置，或每次发送时服务端用 `requested` 刷新 | 重新打开会话时恢复 | 可 |
| 运行快照 | `runs/{id}.requested / effective` | 服务端，创建时与 `meta` 事件 | 检视和复现 | **只写一次** |

冲突策略：同一会话的设置后写覆盖先写，以 `updatedAt` 为准。设置里的安全阈值每次使用时仍由 `GeminiSafetyPolicy` 重新校验，保存下来的值不构成授权。

### 3.6 索引（只增不删）

```
runs: uid ASC, conversationId ASC, createdAt ASC
```

部署索引需要你的确认。会话详情接口在索引就绪前不能启用 `runs`。

## 4. 历史上下文只取有效版本

新增纯函数 `RunContextSelector.Select(messages, runs, maxTurns)`，替代 `GetHistoryAsync` 的「取最近 N 条消息」。

规则，按顺序：

1. 按 `createdAt` 排序，把消息分成轮次：有 `runId/turnId` 的按它归组；**旧消息**（没有这些字段）按相邻的「用户 → 模型」配对，没有配对的孤儿用户消息视为无回复。
2. 一个轮次**有效**，当且仅当存在有效的助手回复：旧消息的助手回复，或终态为 `completed` 的运行的助手消息，或 `stopped / interrupted / failed` 且有非空部分内容的助手消息（保留用户看到的内容，包括截断的部分）。
3. 没有有效回复的轮次（失败且无内容、停止且无内容）**整轮排除**，包括它的用户消息。这同时修复了现有的「孤儿用户消息进入历史」。
4. 进行中的运行（非终态）不参与。
5. 取**最近 `maxTurns` 个有效轮次**（沿用现有 `MaxHistoryRounds`）。

### 预留给重新生成与编辑重发（本阶段不实现，不可由 API 触达）

| 字段 | 位置 | 含义 |
|---|---|---|
| `trigger` | 运行 | `send`（本阶段）、`retry`（批次 2，仅无内容的失败）、预留 `regenerate`、`edit` |
| `attempt` | 运行 | 同一轮次内的第几次运行，从 1 开始 |
| `userMessageId` | 运行 | 这次运行基于哪个用户消息版本 |
| `revision`、`supersededByMessageId` | 用户消息 | 用户消息版本号，被取代的版本及其运行从上下文和默认时间线中排除 |
| `selectedRunId` | 用户消息 | 该轮次被选中的回复；空则取最新且有内容的终态运行 |

不变量：同一会话任一时刻最多一个非终态运行；只有**最后一轮**可以被重新生成或编辑；上下文只取每个轮次的选中版本，旧版本保留不删除，不做分支。

选取规则用夹具单元测试覆盖，包括这些预留字段，所以以后上线功能时不需要改选取逻辑。

## 5. API 增量

所有新接口在 `/api/v2/` 下，认证同现有（Firebase Bearer）。所有权校验失败一律返回 404。

| 方法与路径 | 批次 | 说明 |
|---|---|---|
| `POST /api/v2/runs` | 1 | 创建运行，返回 SSE（第 2.8 节）。请求体含 `runId`、`conversationId`、`message`、`attachments`、`settings` |
| `POST /api/v2/runs/{runId}/stop` | 1 | 幂等。返回当前运行快照；未知返回 404 |
| `GET /api/v2/runs/{runId}` | 1 | 快照，必要时对账 |
| `GET /api/v2/conversations/{id}` | 1 | 在现有字段之上增加 `runs[]`（摘要）、`settings`、消息的 `runId` 与 `status`；同时对账 `activeRunId` |
| `GET/PUT /api/v2/me/settings` | 2 | 我的默认 |
| `PUT /api/v2/conversations/{id}/settings` | 2 | 此会话设置 |
| `GET /api/v2/conversations/{id}/attachments/{messageId}/{index}` | 3 | 由后端代理读取附件，不再把 base64 塞进会话详情 |
| 现有 `/api/*` | — | 第一批不动；第三批 `/api/chat/stream` 改为适配器调用运行服务 |

错误体统一为 `{error: {code, message, retryable}}`，`code` 取第 2.2 节的原因枚举。

## 6. 模块边界

### 6.1 后端

```
Services/Runs/
  RunStatus.cs, RunRecord.cs, RunError.cs      状态、记录、错误模型
  RunIds.cs                                    UUIDv5 派生
  RunFingerprint.cs                            规范化与哈希
  IRunStore.cs                                 创建事务 / CAS 终结 / 心跳 / 读取
  FirestoreRunStore.cs, InMemoryRunStore.cs    后者用于单元测试，模拟相同的 CAS 语义
  RunService.cs                                生命周期编排（替代 ChatOrchestrator 的内部）
  RunCancellationRegistry.cs                   进程内取消令牌，按 runId
  RunReconciler.cs                             租约过期对账
  RunContextSelector.cs                        第 4 节
Api/RunEndpoints.cs                            v2 路由
Api/ChatEndpoints.cs                           旧路由，第三批改为适配器
```

接口变更（内部，非兼容）：

- `IChatModelClient.StreamChatAsync(ChatModelRequest, CancellationToken)`；`GeminiService`、`OpenAICompatibleChatModelClient`、`MockChatModelClient` 都要贯穿令牌。
- `IChatModelClient.DescribeEffectiveConfig()`：返回实际生效的配置，用于 `effective` 与 `meta`。
- `ChatChunk` 增加可选的 `FinishReason`、`Usage`，只在供应商提供时填。

`ChatOrchestrator` 在第一批保留，旧路由继续用它；第三批旧路由切到 `RunService` 后，在第四批删除它和对应测试的旧版本，**只保留一套实现**。

### 6.2 Node 代理（`apps/web/server.mjs`）

- 传递断开：`res.on('close')` 触发上游 `AbortController`。
- 不缓冲 SSE。
- 路由：`/app/*` 提供新前端（SPA 回退到 `index.html`），`/legacy/*` 提供旧前端，`/` 由 `WEB_DEFAULT_UI` 决定直接提供旧界面还是重定向到 `/app/`。
- 其余（`/api/*` 代理、服务间身份令牌）不变。

### 6.3 前端（`apps/web/client/`，Vite + React + TypeScript）

```
src/
  api/        client.ts（带令牌的 fetch）、sse.ts（解析器）、runs.ts、types.ts
  auth/       Firebase 封装，邮箱、Google、重置密码
  features/
    chat/     runMachine.ts（状态机）、useRun.ts、Thread、Composer、Message、Markdown
    conversations/   列表、搜索、分页、重命名、导出、删除
    settings/        模型、思考、联网弹层；检视器设置页（第二批）
    inspector/       运行详情（第三批）
    drafts/          草稿与待确认运行（IndexedDB）
  styles/     tokens.css（视觉稿的色板直接作为变量）、各组件 CSS Modules
```

技术选型与理由（已在上一轮确认 React，这里补充配套）：

| 用途 | 选择 | 理由 |
|---|---|---|
| 框架 | React 19 + TypeScript（严格模式） | 无样式组件库和配套工具最成熟，可访问性不用手写 |
| 构建 | Vite | 已确认 |
| 无样式组件 | Radix UI primitives | 弹层、面板、菜单、标签页的焦点和键盘行为现成可用 |
| 状态 | Zustand（界面与运行状态机）+ TanStack Query（会话列表与详情缓存） | 体积小，运行状态机单独成纯函数便于表驱动测试 |
| 路由 | react-router | 维护活跃，路径 `/app/c/:id` |
| Markdown | react-markdown + remark-gfm + rehype-sanitize，代码高亮按需加载 | 不使用 `dangerouslySetInnerHTML`，把现有 `markdown.js` 的测试用例移植过去 |
| 草稿 | idb-keyval（IndexedDB） | 附件 Blob 不能放 localStorage |
| 测试 | Vitest、Testing Library、Playwright、axe | 见第 9 节 |
| 样式 | CSS Modules + CSS 变量 | 零运行时，直接复用视觉稿 |

依赖原则：用 lockfile 和 `npm ci`，精确版本，不引入未用到的库；新增依赖需要在 PR 里说明理由。

### 6.4 工具链

- **Dockerfile**（`apps/web`）：多阶段。构建阶段 `npm ci && npm run build`，运行阶段只复制 `server.mjs`、旧 `public`、新 `client/dist`。同时把 CI 与镜像的 Node 主版本统一（现为 22 和 24）。
- **CI**：新增 job `web-client`：`npm ci`、类型检查、lint、Vitest、构建、Playwright 端到端（对接 Mock 提供商的本地 API）。后端 job 增加 Firestore 模拟器上的运行存储契约测试。
- **本地开发**：`npm run dev`（Vite，`/api` 代理到 `BACKEND_URL`，默认 `http://localhost:8080`）。后端在 Development 环境下用显式开关启用假用户上下文和 Mock 提供商（D7）。**该开关在非 Development 环境下启动即报错**，并有测试断言。

## 7. 视觉与交互

截图按实际尺寸导出，桌面 1280×800，手机 390×844，浅色为主，深色抽样。

| 状态 | 桌面 | 手机 |
|---|---|---|
| 默认对话 | `d1-default.png`（深色 `…-dark.png`） | `m1-chat.png` |
| 检视器 · 运行 | `d2-inspector-run.png` | `m6-run-details.png` |
| 检视器 · 设置 | `d3-inspector-settings.png` | `m3-settings-sheet.png` |
| 模型选择 | 见 `design-board.html` 状态卡片 | `m2-model-sheet.png` |
| 会话列表 | `d1-default.png` 左栏 | `m4-history-drawer.png` |
| 生成中 | 状态卡片 | `m5-generating.png` |
| **长代码** | `d4-long-code.png`（深色 `…-dark.png`） | `m7-long-code.png` |
| **附件** | `d5-attachments.png` | `m8-attachments.png` |
| **错误** | `d6-errors.png`（深色 `…-dark.png`） | `m9-errors.png`（深色 `…-dark.png`） |

和上一版相比的变化：

- 长代码：行号、横向滚动（右侧渐隐提示）、折叠到约 13 行并显示「展开全部 N 行」、复制、自动换行开关。
- 附件：已发送的图片缩略图和文件卡片；待发送附件可移除；被拒绝的文件给出具体原因（文件 512 KB、图片 4 MB、最多 8 个，数字来自现有校验器）。
- 错误：连接中断（已保存部分内容，提示可直接发送新消息继续）、失败（未产生回复，消息已保存）、离线提示。中断状态**不提供重试**（D5）。
- 对话区默认滚动到最新内容。

## 8. 分阶段交付与验收

每批独立可发布、独立可回滚。**未经你确认，任何一批都不部署线上。**

### 批次 0：地基与风险验证（不改变用户可见行为）

| 项 | 验收 |
|---|---|
| Node 代理传递断开 | 自动化测试：用假上游启动代理，客户端断开后，假上游在 1 秒内观察到请求被取消 |
| 沙箱具备 .NET SDK，或确定只靠 CI | 能在本地运行 `dotnet build` 与现有全部测试，或书面记录只依赖 CI |
| **Google.GenAI 1.26.1 的取消与用量** | 小型验证：令牌取消后 `GenerateContentStreamAsync` 在 2 秒内结束；响应是否带 `UsageMetadata`。**这需要真实凭据发起一次计费调用，由你授权后执行**。若 SDK 不支持取消，退路是丢弃枚举器并让出站 HTTP 请求在连接层被关闭，同样要验证 |
| Firestore 模拟器可用于 CI | CI 中一个契约测试能起模拟器并通过 |
| Vite 项目骨架 | `npm run build` 与 `npm test` 在 CI 通过，Docker 镜像可构建，旧界面不受影响 |

### 批次 1：垂直链路

范围：后端运行服务与 v2 接口，新前端只做**最小壳**：登录、单会话、发送、流式、停止、中断后对账、错误条、读取历史。旧界面和旧接口不动，v2 在 `Runs__Enabled=false` 时返回 404。

验收（每一条都有自动化测试，除最后一条）：

1. **幂等**：同一 `runId` 并发和顺序各发两次，结果只有 1 条用户消息、至多 1 条助手消息、模型调用恰好 1 次、配额预留 1 次；重放返回 `replayed`。
2. 同一 `runId`、不同内容，返回 409 `idempotency_mismatch`；他人的 `runId` 返回 409 且不泄露。
3. **停止**：流中间调用 stop，运行变为 `stopped(user)`，部分内容保存为 `status=partial` 的助手消息，Mock 模型在 2 秒内观察到取消，`activeRunId` 被清除；完成后再停止，返回已完成快照，不改变数据；未知运行 404。
4. **连接中断**：只断开流不调用 stop，运行变为 `interrupted(connection)`，部分内容保存，模型被取消。
5. **宽限期**：流先断开、停止在 2 秒内到达，终态为 `stopped`；超过宽限期则为 `interrupted`。
6. **服务端丢失**：模拟心跳过期，并发多次 `GET /runs/{id}`，结果恰好对账一次，终态为 `interrupted(server_lost)`，内容来自检查点。
7. **失败**：供应商异常、配额超额、安全拦截、空响应分别得到正确的 `statusReason`；无内容时不写助手消息、用户消息保留；已超额时模型没有被调用。
8. **并发**：同一会话第二个运行返回 409 `run_in_progress`；租约过期的旧运行会被先对账再放行。
9. **上下文选取**：覆盖旧消息配对、孤儿用户消息、部分内容、失败无内容、进行中运行、预留字段的夹具，全部通过。
10. **兼容**：现有的 40 个后端测试和 3 个前端测试文件不修改即通过；旧接口行为不变。
11. **前端**：Vitest 覆盖 SSE 解析（跨分片、CRLF、多字节字符）和运行状态机的表驱动测试；Playwright 覆盖「发送、流式、停止」「流被切断、对账后显示已保存的部分」「连点发送恰好一次请求」「流式过程中刷新页面后对账」。
12. **CI 全绿**：dotnet 构建与测试、前端类型检查、lint、单元测试、构建、Docker 构建。
13. **线上冒烟**（需你确认后在 `--no-traffic --tag next` 的修订上进行）：发送、停止、断网、刷新各一次，确认 Firestore 中的 `runs`、`messages`、`conversations.activeRunId` 与预期一致。

### 批次 2：新界面完整化

范围：视觉稿中的布局、模型 / 思考 / 联网弹层、检视器设置页、三层设置接口、会话操作整理（重命名、导出、删除原位确认）、草稿与待确认运行持久化、错误恢复、附件（沿用现有内联格式）、长代码与 Markdown、手机端、失败运行的重试（D5）。

验收：

- 视觉：Playwright 在 1280×800 与 390×844、浅色与深色下截图，与批准的基线逐一比对，差异超过阈值即失败。
- 功能对等清单：邮箱登录、注册、Google 登录、忘记密码；历史分页；重命名、删除、导出 Markdown 与 JSON；最多 8 个附件及各类型限制；搜索三态；各模型的思考选项；内容过滤按账号权限显示；配额错误文案。
- 三层设置：改默认不影响已有会话；改会话设置在重新打开后恢复；运行快照在之后修改设置时保持不变（测试覆盖）。
- 草稿：切换会话、刷新页面、发送失败、断网后都能恢复文字与附件。
- 可访问性：axe 无严重问题；弹层与面板可键盘操作，焦点可回到触发元素。
- 重试：仅对「没有助手消息」的失败运行可用，复用同一条用户消息，不产生重复消息。

### 批次 3：检视器、附件传输、旧接口切换

范围：运行详情页（请求与实际生效分列、浏览器与后端耗时分开、用量未知显示「未知」）；附件下载代理接口，会话详情不再内联 base64；`/api/chat/stream` 改为适配器调用 `RunService`。

验收：检视器所有字段与运行记录一一对应，没有出现过的值一律显示「未知」；会话详情体积在含附件的会话上显著下降（以字节数前后对比为准）；旧界面通过适配器仍可用，断开时会取消模型请求并保存部分内容，这是对旧界面的行为改进，需在发布说明里写明。

### 批次 4：默认切换与清理

范围：`WEB_DEFAULT_UI=next`，`/` 重定向到 `/app/`，旧界面移到 `/legacy/`，观察两周后评估是否移除旧界面、`ChatOrchestrator` 与旧测试。

验收：切换前一周内，新界面可覆盖你的日常使用；回滚演练一次（把环境变量改回，旧界面立刻恢复）。

## 9. 测试策略

- **纯逻辑优先**：状态机、选取器、幂等指纹、SSE 解析都是纯函数，表驱动覆盖。
- **存储契约测试**：同一组用例同时跑 `InMemoryRunStore` 和 `FirestoreRunStore`（模拟器），保证 CAS、事务、重放语义一致。
- **取消测试**：Mock 模型客户端记录是否观察到取消令牌、花了多久，断言时限，而不是只断言结果。
- **端到端**：Playwright 对接 Mock 提供商，用路由拦截模拟断网、切断流、慢响应。
- **不测什么**：不在 CI 里调用真实模型。真实模型只用于批次 0 的一次验证和批次 1 的线上冒烟，都需要你授权。

## 10. 回滚与发布

| 层 | 开关 | 回滚方式 |
|---|---|---|
| v2 接口 | `Runs__Enabled`（默认 false） | 关闭即 404，旧界面不受影响 |
| 旧接口走运行服务 | `Runs__LegacyAdapter`（默认 false，第三批才可打开） | 关闭即恢复使用 `ChatOrchestrator` |
| 默认界面 | `WEB_DEFAULT_UI`（`legacy` 或 `next`） | 改回 `legacy`，不需要重新构建 |
| 整体 | Cloud Run 修订流量 | 把流量切回上一个修订；发布一律先 `--no-traffic --tag next` 验证 |
| 数据 | 无需回滚 | 全部为可选字段或新集合；旧代码忽略它们。`status=partial` 的助手消息在旧界面里表现为普通消息，没有内容的失败运行在旧界面里不可见，两者都无害 |

约束：

- **不删除任何历史数据**。本方案没有删除或改写现有文档的步骤。
- **模型密钥不进浏览器**：新前端只携带 Firebase 令牌，所有模型调用仍由后端发起，密钥仍来自 Cloud Run 环境与 Secret Manager。
- **不未经确认部署**：索引部署、Cloud Run 发布、环境变量变更都需要你逐项确认。
- 首次在生产启用 v2 前，建议做一次 Firestore 托管导出（D8）。

## 11. 风险与未验证项

| 风险 | 状态 | 应对 |
|---|---|---|
| Google.GenAI 1.26.1 是否真正支持取消、是否提供用量 | **未验证**，没有 SDK 与凭据可在沙箱试 | 批次 0 验证；不支持则退到连接层取消，用量显示「未知」 |
| 停止请求落在另一个实例，最长约 1 秒延迟 | 已设计 | 进程内注册表加 1 秒轮询 |
| Cloud Run 实例被回收导致运行失联 | 已设计 | 心跳与租约对账，终态 `interrupted(server_lost)`，内容取检查点（最多丢失约 5 秒内容） |
| 检查点写入频率与 Firestore 单文档写入限制 | 5 秒一次，远低于限制 | 无需处理 |
| 供应商已计费但取消后用量未知 | 无法避免 | 用量标记 `estimate`，不在检视器里冒充精确值 |
| 沙箱无 .NET SDK，后端改动无法本地编译 | 事实 | 批次 0 先解决，否则每个后端改动都要等 CI |
| 限流策略调整影响现有保护 | 低 | 只对读取与停止放宽，创建运行仍是每分钟 30 次 |

## 12. 下一步

等你确认第 0 节的决策与第 7 节的截图。确认后我从**批次 0** 开始，按验收标准逐项完成并给出证据，再进入批次 1。批次 0 里唯一需要你操作的是：授权一次真实模型调用来验证取消与用量。
