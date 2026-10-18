# 聊天工作台重构：实现方案

状态：**批次 0 的本地可验证部分已完成（证据见 [`batch-0-report.md`](batch-0-report.md)）；批次 1 之前的四项修正已写入本文。** 没有部署任何东西，没有改动任何云资源。
视觉稿：[`docs/design/design-board.html`](../design/design-board.html)，实际尺寸截图见 [`docs/design/screenshots/`](../design/screenshots/)。
备份与恢复手册：[`backup-and-restore.md`](backup-and-restore.md)。

## 0. 摘要

1. **运行（Run）是一等对象。** 每次发送先由浏览器生成 `runId`，服务端以它做幂等键，并以它派生所有 ID。「至多一次模型会话」**不靠幂等键承诺**，靠三层：①单赢家的 Firestore 准入事务；②**调用供应商之前的执行权复核**（一个持久化的 `providerCall` 标记，只有仍持有租约的执行者才能把它从 `not_started` 改为 `started`）；③租约与围栏。之后任何路径（重放、对账、重试、崩溃恢复）都没有调用模型的能力，状态不确定时一律不自动重新调用，见 2.3 到 2.4。
2. **终态四种：** `completed`、`stopped`（用户主动）、`interrupted`（连接中断或服务端丢失）、`failed`。终态一经写入不再改变（唯一例外是失败运行上一次性写入的 `retriedByRunId` 链接，见第 4 节）。
3. **停止是显式的。** 浏览器先调用 `POST …/stop`，再中断流。后端取消模型请求，把已生成的部分保存为助手消息（`status=partial`）。取消后供应商连接在毫秒级关闭（已实测，见 2.3.4）。
4. **运行记录独立于消息。** 存在新集合 `runs`，保存请求、实际生效配置、状态、耗时、用量和错误。助手消息只保存内容。
5. **第一批交付是一条垂直链路：** 创建运行、流式、停止、中断、重新查询、幂等，从 API 到一个最小的新前端壳，而不是重写所有页面。旧界面和旧接口在第一批里不动。
6. **回滚靠开关和叠加式数据。** 所有 Firestore 变更都是可选字段或新集合，旧代码读到会忽略。启用 v2 之前必须先完成备份与恢复演练（D8）。
7. **合并到 `main` 会立即触发线上部署**（`deploy-cloud-run.yml` 在 `push` 到 `main` 或 `master` 时无条件构建并部署两个服务），所以这个分支在你明确要求之前不会合并。

### 需要你确认的决策（附默认值）

| # | 决策 | 默认 |
|---|---|---|
| D1 | 新前端路径用 `/app/`，上线后 `/` 重定向到 `/app/`，旧界面移到 `/legacy/` | 同意 |
| D2 | 用户主动停止之外，连接中断一律不继续生成（取消模型请求，保存部分内容） | 同意 |
| D3 | **Web 与 API 的请求超时都设为 300 秒，应用上限 270 秒。** 线上实际值已由你核对：Web 60 秒、API 300 秒，所以现状上限只有约 30 秒。仓库侧已改好（脚本、工作流、文档默认值，以及 `Runs__MaxDurationSeconds = min(Web, API) − 30` 的计算与「小于 60 即报错」，有测试）；**线上变更尚未执行**，命令与顺序见 2.10，由你确认后单独执行 | 仓库侧已完成；线上变更待你确认 |
| D4 | 一个会话同时只允许一个进行中的运行，第二个返回 409 | 同意 |
| D5 | 已失败且没有产生内容的运行允许「重试」；已有部分内容（停止或中断）的运行本阶段**不提供重试**，用户直接发新消息继续，部分内容作为上下文 | 同意（这是与上一版视觉稿的差异，已更新截图） |
| D6 | 旧接口 `/api/chat/stream` 在第三批改为调用新的运行服务（只维护一套内部实现），第四批评估后下线 | 同意 |
| D7 | 本地开发：新增仅开发环境、需显式开关的假用户上下文和 Mock 提供商注册（Mock 客户端现在没有在 DI 中注册，只用于测试） | 同意 |
| D8 | **启用 v2 之前必须先备份并演练恢复**。步骤、命令、验证清单、恢复分级见 [`backup-and-restore.md`](backup-and-restore.md)，其第 7 节是启用门槛。手册里的命令一个都还没运行 | 已按你的要求安排为启用门槛；执行时逐项确认 |

## 1. 已核实的现状事实

下面这些是读代码得到的，方案以它们为前提。

| 事实 | 位置 | 影响 |
|---|---|---|
| 模型客户端接口没有取消令牌：`StreamChatAsync(ChatModelRequest)`。OpenAI 兼容客户端内部有带令牌的私有重载，但公开入口传的是 `CancellationToken.None`。Gemini 的流式调用没有传令牌。**SDK 本身支持**：`GenerateContentStreamAsync(model, contents, config, CancellationToken)`（已在 1.26.1 上反射确认并实测） | `IChatModelClient.cs`、`OpenAICompatibleChatModelClient.cs:64`、`GeminiService.cs:222` | 必须扩展接口并贯穿，这是停止生成的前提 |
| **一次运行里不止一次供应商调用**：`ChatHistoryManager.TrimIfNeededAsync` 在主生成之前会调用 `CountTokensAsync`，超过阈值时还会调用 `GenerateContentAsync` 生成历史摘要；之后 `UpdateTokenCountAsync` 又调用一次 `CountTokensAsync` | `ChatHistoryManager.cs:112-230` | 「至多一次」必须按**模型会话**定义，而不是按 HTTP 请求。这些辅助调用也必须带取消令牌，并且都发生在执行权标记之后（2.3.3），见 2.3.4 |
| 模型客户端是**有状态的**，每次请求新建，`LoadHistoryAsync` 灌入历史 | `ChatOrchestrator.SendAsync` | 上下文选取在加载前完成，客户端本身不改 |
| 先写用户消息，再调模型；失败时不保存已生成内容；用户消息会成为没有回复的孤儿 | `ChatOrchestrator.cs` | 孤儿用户消息现在会进入后续历史，新方案按运行状态排除 |
| 新会话的 `conversationId` 由服务端在请求中生成，浏览器要等 `final` 才知道 | `EnsureConversationAsync` | 重试首条消息无法幂等，新方案改为浏览器生成 |
| 配额在模型调用前预留（事务），之后记录 token，没有释放机制 | `FirestoreChatQuotaService.cs` | 重复提交会重复扣额度，必须在配额之前做幂等 |
| `update` 事件每次发送累计全文，不是增量 | `ChatEndpoints.cs` | v2 改为增量 `delta` |
| Node 代理转发请求时没有传递客户端断开：`fetch` 没有 `signal`，用 `for await` 一直写 | `apps/web/server.mjs` `proxyApi` | 停止和断开不会到达后端，必须修 |
| 全局限流：每个令牌或 IP 每分钟 30 次，作用于所有接口。**实测（真实 ASP.NET 中间件）：命名的端点策略与全局限流叠加，不能放宽；`DisableRateLimiting()` 会移除包括全局在内的全部限制** | `ServiceCollectionExtensions.cs:46`、`RateLimitStackingTests.cs` | 轮询和停止必须在全局限流器内部分类并给独立预算，见 2.9 |
| API 请求超时 300 秒，最小实例 0，CPU 在请求之外会被限制 | `scripts/deploy-cloud-run.sh` | 请求结束后不能指望后台继续生成 |
| **`push` 到 `main` 或 `master` 会触发 `deploy-cloud-run.yml`：构建、并以 `--set-env-vars` 与 `--timeout` 完整重新配置两个服务** | `.github/workflows/deploy-cloud-run.yml:3-8` | 合并 `main` 等于上线。本分支不合并；工作流里的默认值改动会在下一次合并后生效 |
| Firestore 集合：`conversations`、`messages`、`usageQuotas`。没有用户设置集合 | 代码检索 | 用户默认设置需要新集合 |
| 历史按 `uid + conversationId + createdAt` 索引，取最近 N 条 | `firestore.indexes.json` | 新索引只增不删 |
| **请求超时：Web 60 秒，API 300 秒**。仓库默认值（脚本、工作流、`docs/cloud-run.md`）原为 60 与 300，**线上实际值由你在 Cloud Shell 核对，同为 60 与 300**（项目 `zhumyproject`，区域 `asia-east1`）。Node 代理自身没有任何超时设置。仓库默认值现已改为 300 与 300，线上尚未变更 | 部署脚本、工作流、`docs/cloud-run.md`、`server.mjs` | 经过代理的流式生成现在就最长 60 秒，这是已存在的限制，不是新方案引入的。见 2.10 |
| OpenAI 兼容客户端用普通 `HttpClient`，没有重试或弹性处理器；`HttpClient.Timeout` 默认 100 秒，但读取响应头即返回，不限制流式正文。**.NET 的 `SocketsHttpHandler.ResponseDrainTimeout` 默认 2 秒**：取消一个未读完的 HTTP/1.1 响应后，连接还会保持最多 2 秒继续接收 | `OpenAICompatibleChatModelClient.cs:83`、`ServiceCollectionExtensions.cs:82` | 取消后连接会多开约 2 秒（本地假上游实测）。真实服务在这段时间里是否继续生成或计费**未验证**。Gemini 侧已在批次 0 缩短为毫秒级；OpenAI 兼容侧在批次 1 配置同样的处理器 |
| **Gemini SDK 1.26.1 实测（本地假上游，API Key 模式，传输层与 Vertex 模式共用）**：默认配置对 503、429、500、连接重置各只发 1 个请求（不重试）；显式 `RetryOptions.Attempts=1` 同样为 1；开到 3 则确实会重试（用来证明测试不是空转）；取消令牌后枚举在约 4 毫秒内结束；`GenerateContentResponse.UsageMetadata` 存在（含提示、候选、思考、总 token 数） | `GeminiRetryTests.cs`、批次 0 报告 | 「总尝试一次、零自动重试」已固化为代码和测试。**Vertex 模式下的行为仍需第一次真实调用时再确认一次** |
| 沙箱现已安装 .NET SDK 10.0.112（来自 Ubuntu 软件源），现有 40 个测试在本地通过；新增后共 57 个（使用 Firestore 模拟器时全部运行，否则 10 个被标为跳过） | 批次 0 报告 | 后端改动可以本地编译与测试 |

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
| 准入事务本身失败（锁超时、被中止、截止时间） | **视为「未知」，不是「没抢到」**：立即按 `runId` 读回，`leaseOwner` 等于自己则为 `Admitted`，等于别人则为 `Replay`，读不到则返回 503 `retry_with_same_run_id`（可安全用同一个 `runId` 重试）。已在模拟器上复现：50 个并发准入时事务可能整体被中止，读回协议下仍恰好 1 个赢家 |

- **同一 `runId` 至多一次模型会话，但可能是零次。** 这条保证的机制是 2.3 的准入事务、执行权复核标记和 2.4 的租约，不是幂等键本身。歧义（响应丢了、不知道成没成）只能靠查询状态消除，不会靠静默重新执行消除。
- 双击和多标签：前端按会话维度做进行中锁，服务端靠幂等和「单会话单运行」兜底（D4）。
- 浏览器自动重试：只在**收到首个字节之前**的网络错误上重试，最多 2 次，沿用同一个 `runId`。收到 `run` 事件之后不再自动重试，改走对账。

### 2.2 状态机

```
admit(准入事务) ──► running ──► completed
      │                │  └──► stopped        (用户显式停止，且已观察到取消)
      │                │  └──► interrupted    (连接中断，或租约过期)
      │                └─────► failed         (供应商错误、安全拦截、空响应、超时)
      └─ 配额超额 ──────────► failed(quota)   (由准入事务直接写成终态，没有调用模型)
```

没有 `queued` 状态：运行要么在准入事务里直接成为 `running`（同时获得调用模型的唯一权利），要么直接成为 `failed(quota)`。

`stopping` 不是存储状态，是派生状态：`running` 且 `stopRequestedAt` 非空。终态不可变，唯一写入者通过事务做比较并设置（CAS），谁先写谁赢，其余是无操作。

| 终态 | `statusReason` | 触发 | 助手消息 |
|---|---|---|---|
| `completed` | — | 流正常结束且内容非空 | `status=final` |
| `stopped` | `user` | 收到显式停止，模型调用已取消 | 有内容则 `status=partial` |
| `interrupted` | `connection` | `RequestAborted` 且 2 秒宽限内没有收到停止 | 有内容则 `status=partial` |
| `interrupted` | `server_lost` | 租约过期，由对账器写入 | 用最近一次检查点的内容 |
| `failed` | `quota` / `safety` / `provider_error` / `empty_response` / `timeout` / `validation` / `config` / `permission` / `internal` | 异常或规则拒绝 | 有内容则 `partial`，没有则不写 |

没有内容时不写助手消息。UI 用运行记录渲染错误卡，不依赖消息。

### 2.3 准入事务与「至多一次」的边界

**谁有权调用模型。** 只有一种请求：在 `IRunStore.AdmitAsync` 的**已提交**事务里拿到 `Admitted` 结果、并且随后成功把 `providerCall` 标记改为 `started`（2.3.3）的那个 HTTP 请求，且只在该请求进程内调用。除此之外没有任何路径能调用模型：重放、`GET`、对账器、重试循环、后台任务都不能。结构上用类型强制：模型调用入口只接受 `AdmittedRun`，而这个类型只能由 `AdmitAsync` 构造；测试里用计数的 Mock 客户端断言重放、对账、停止路径的调用次数为 0。

**准入事务（一个 Firestore 事务，内含全部决定）：**

1. 前置（事务之外）：校验认证、请求体、附件格式与大小、`Advanced.Validate`、安全阈值权限，失败直接返回 4xx，不创建运行。**此时不写任何外部存储，包括附件**（原因见 2.3.2）。生成本次请求的 `executorId`（随机 UUID）。
2. 事务内读取：`runs/{runId}`、`conversations/{id}`（可能不存在）、`conversations.activeRunId` 指向的运行、当天的 `usageQuotas` 文档。
3. 事务内决策，**事务回调必须没有副作用**（SDK 会在冲突时重跑回调，所以不调用模型、不上传、不发请求）：

| 读到的情况 | 事务写入 | 结果 |
|---|---|---|
| 运行已存在且 `leaseOwner == executorId` | 无 | `Admitted`（上一次提交已成功、只是应答丢了） |
| 运行已存在，其他情况（同一用户，指纹相同） | 无 | `Replay` |
| 运行已存在，指纹不同或不属于当前用户 | 无 | `Conflict` |
| 会话属于他人 | 无 | `Conflict` |
| 会话有进行中的运行且租约未过期 | 无 | `Busy`（409 `run_in_progress`） |
| 会话有进行中的运行但租约已过期 | 无（对账在事务之外先做，然后重新准入） | 重试准入 |
| 配额超额 | 创建运行（`failed(quota)`，终态，`providerCall=not_started`）、写入用户消息（附件元数据，状态 `failed`）、必要时创建会话 | `Rejected` |
| 其余 | 创建运行（`running`、`providerCall=not_started`、`leaseOwner = executorId`、`admittedAt`、`heartbeatAt`、指纹、`requested`）、写入用户消息（附件**元数据**与 `sha256`，状态 `pending`）、必要时创建会话与 `settings`、设置 `activeRunId`、**在同一个事务里扣减配额** | `Admitted` |

   配额预留与准入在同一事务里，因此不存在「配额扣了但不知道有没有被准入」的窗口。这要求 `IChatQuotaService` 提供可在外部事务内调用的预留方法（现在它自己开事务），见 6.1。
4. 事务提交后，如果结果是 `Admitted`：立即启动心跳（租约从准入时刻开始计时），然后依次：附件落盘（2.3.2）、执行权复核（2.3.3）、选取上下文、开始流式。**在执行权复核成功之前不会产生任何供应商请求。**
5. 若这时请求令牌已被取消（浏览器在准入期间断开）：不调用模型，直接终结为 `interrupted(connection)` 或 `stopped`，没有内容；`providerCall` 仍是 `not_started`，所以配额会被退还（2.5）。

**准入事务本身失败或应答丢失。** 事务抛出任何异常（锁超时、被中止、截止时间）都按「**未知**」处理，立即读回运行文档：`leaseOwner == executorId` 则是上一次提交成功，判为 `Admitted`；是别人则 `Replay`；读不到则返回 503，客户端用同一个 `runId` 重试。已在 Firestore 模拟器上复现并固化为测试：50 个并发准入时，事务可能整体以 `Transaction lock timeout` 中止，读回协议下仍恰好 1 个赢家，并且「应答丢失」的重试被识别为 `Admitted` 而不是 `Replay`（否则没人执行）。

### 2.3.1 崩溃与不确定状态的处理边界

规则：**状态不确定时，只做「对账为终态」，永远不自动再次调用模型。** 想再来一次必须是用户的显式操作，产生新的 `runId`（失败且无内容时见第 4 节的重试规则），界面要写明上一次「可能已向模型发出请求，并可能已计费」。

有了 `providerCall` 标记，对账器可以区分两种完全不同的情况：**`not_started` 是「从未调用过供应商」的确定性证据**（标记先于任何供应商请求写入），`started` 才表示「可能已调用」。

| 崩溃或故障点 | 持久化状态 | 处理 | 供应商调用 | 数据后果 |
|---|---|---|---|---|
| P0 准入事务提交之前 | 无 | 客户端用同一 `runId` 重发，会被正常准入 | 0 或 1（后续那次） | 无 |
| P1 已准入，附件落盘或标记写入之前死亡 | `running`，`providerCall=not_started`，心跳过期 | 对账器写 `interrupted(server_lost)`，**并退还配额**（确定没调用过） | **0（确定）** | 用户消息保留，附件状态 `pending` 视为不可用 |
| P1' 标记已写为 `started`，供应商请求发出之前死亡 | `started` | 对账器写 `interrupted(server_lost)`，不退还 | 0 或 1（不确定，按已调用处理） | 配额已扣 |
| P2 流式期间进程死亡 | `started`，最近检查点 | 对账器写 `interrupted(server_lost)`，用检查点内容写部分助手消息 | 1（进程死亡后连接随之断开；服务端是否还在生成取决于供应商，未验证） | 丢失检查点之后至多约 5 秒的内容 |
| P3 模型已生成完，终结事务提交前死亡或失败 | 同 P2 | 同 P2（终结事务先重试 15 秒） | 1 | 完整回复的尾部可能丢失，只保留检查点内容，运行里标注 `server_lost` |
| P4 终结事务已提交，给客户端的应答丢了 | 终态 | 客户端 `GET` 对账 | 1 | 无 |
| P5 重复点击、代理或网络层重复发送 | 已有运行 | `Replay`，不执行 | 不增加 | 无 |
| P6 两个实例同时收到同一 `runId` | 事务串行化 | 只有一个 `Admitted`，另一个 `Replay`（事务失败者读回） | 1 | 无 |
| P7 停止请求落在另一个实例 | `stopRequestedAt` | 执行实例 1 秒内轮询到并取消 | 不变 | 无 |
| P8 执行实例与 Firestore 失联（网络分区、长时间暂停） | 执行实例看不到自己的租约 | 见 2.4：自己先停（自我围栏），对账器在租约过期后接管 | 不变 | 同 P2 |
| P9 对账器已经终结，旧执行实例（僵尸）恢复运行 | 终态 | 它的下一次心跳或终结事务发现已终态，**取消模型调用并丢弃输出**，向客户端发送真实终态 | 不变 | 无 |
| P10 标记事务本身失败（Firestore 故障） | 未知 | 读回：已是 `started` 且租约属于自己则继续；仍是 `not_started` 则最多重试 3 次（5 秒内）；仍失败就终结为 `failed(internal)`，**不调用供应商**，退还配额 | 0 | 无 |
| P11 附件落盘失败 | `pending` | 删除本次已写的自己的对象，附件状态置 `failed`，终结为 `failed(storage)`，**不调用供应商**，退还配额 | 0 | 用户消息保留，可重试 |

没有任何一行会导致第二次模型会话。

### 2.3.2 附件：覆盖与清理

**问题。** 现有存储实现用 `chat-attachments/{conversationId}/{messageId}/{index}.ext` 作为对象名，并且 `UploadObjectAsync` 没有任何前提条件，同名对象会被直接覆盖。旧流程里 `messageId` 每次都是随机的，所以没出过事；新方案里 `messageId` 由 `runId` 派生，如果仍然「先上传、后准入」，会出现三种危险：

1. 同一个 `runId` 带着不同的附件内容重放，在被识别为 `idempotency_mismatch` 之前就**覆盖了原运行的附件**；
2. 重放或被拒绝的请求（`Conflict`、`Busy`、配额超额）已经写了对象；
3. 请求带着别人的 `conversationId`，在所有权校验之前就写进了别人的路径。

**规则（四条，缺一不可）：**

1. **先准入，后落盘。** 附件只在 `Admitted` 之后由唯一的赢家上传。重放、冲突、忙、配额拒绝都不触碰存储。
2. **对象名带内容哈希，创建时带「不存在才写」前提。** 名称改为 `chat-attachments/{conversationId}/{messageId}/{index:D2}-{sha256前16位}{ext}`，上传用 `ifGenerationMatch=0`。不同内容得到不同的名字；已存在则返回 412，此时如果对象的 `sha256` 与元数据一致就视为成功（同一赢家的重复上传），否则失败。**任何路径都不会覆盖已存在的对象。**
3. **准入事务只写元数据**（文件名、类型、大小、`sha256`、对象名、状态 `pending`），落盘成功后把消息的附件状态更新为 `stored`，失败则为 `failed`。读取时：状态不是 `stored` 的附件显示为「不可用」，并且**不会被发给模型**；一个已终态的运行里仍是 `pending` 的附件视为 `failed`。读取对象时校验 `sha256`。
4. **清理的边界。** (a) 自己写的对象，在自己失败时（P11）尽力删除；(b) 删除会话仍按现有逻辑，按消息里的元数据逐个删除，`NotFound` 视为成功（现有实现已经如此）；(c) 不依赖对象生命周期规则自动删除，因为元数据引用的对象不能被误删；(d) 崩溃后可能留下「元数据引用了但从未写入」的情形，读取时按不可用处理，没有无主对象；(e) 批次 3 提供一个只读的审计脚本，对比桶内对象和消息元数据，**只报告不删除**。

**测试（批次 1，与实现同时提交）：**

- 同一 `runId` 不同附件内容重放：返回 409，`Fake` 对象存储的写入次数为 0，原对象字节不变。
- 他人的 `conversationId`、`Busy`、配额超额：对象存储写入次数为 0。
- 同名对象已存在且哈希一致：视为成功；哈希不一致：失败，原对象不变。
- 准入后、落盘前崩溃：附件为 `pending`，对账后不可用，模型调用 0 次，配额退还。
- 落盘失败：不调用模型，自己写的对象被删除，运行 `failed(storage)`。
- 删除会话：元数据引用的对象全部被删除，包括从未写入的（`NotFound` 容忍）。
- 重试（第 4 节）复用原消息的附件，不重新上传，不产生新对象。

### 2.3.3 执行权复核

**问题。** 准入成功不等于仍然有权调用。从准入到真正发出供应商请求之间有一段时间（附件落盘、选取上下文），期间进程可能被暂停、租约可能过期、对账器可能已经把运行终结为 `interrupted(server_lost)`。如果执行者不重新确认就调用，就会出现「运行已经终态，供应商却被调用了」的僵尸调用。

**做法。** 在**第一个供应商请求之前**，执行者在一个事务里做复核，并同时留下持久化证据：

```
事务：读 runs/{runId}
  条件：status == running  且  leaseOwner == 我的 executorId  且  providerCall == not_started
  写入：providerCall = started，providerCallStartedAt = 服务端时间
成功 → 才允许创建任何供应商请求
失败 → 终结（若运行已被别人终结则什么都不写），供应商调用 0 次
```

- 该事务与终结事务、对账事务互相串行化。无论谁先到：执行者先成功，则对账器随后看到 `started`（按「可能已调用」处理）；对账器先终结，则执行者的复核失败，**供应商调用 0 次，且文档里是 `not_started` 作为证据**。
- 「供应商请求」包括主生成流、历史摘要调用和令牌计数调用（2.3.4）。**它们都在标记之后。**
- 标记只能被租约持有者从 `not_started` 改为 `started` 一次；第二次尝试一定失败。
- 事务本身失败按 P10 处理。
- 已在 Firestore 模拟器上作为契约测试固化：只有租约持有者能设置、只能设置一次；对账器先赢则调用 0 次且标记仍是 `not_started`；复核与终结并发 10 轮，**「复核成功当且仅当最终标记为 started」始终成立**。

### 2.3.4 供应商调用的定义与取消

- **按「模型会话」定义「至多一次」。** 一次运行至多发起一次**模型会话**：一个主生成流，加上该流所需的有限辅助调用（令牌计数、必要时一次历史摘要，见第 1 节事实）。我们的代码从不重复发起主生成。运行里记录 `providerCalls: {generate, summarize, countTokens}`（我们自己的计数），检视器如实显示，不显示的就是没有。
- **辅助调用也带取消令牌**，并且受同一个时长上限约束；用户停止时它们立即取消，不会在停止后继续生成摘要。
- **SDK 只尝试一次，零自动重试。** 所有 Gemini 客户端使用 `GeminiHttpOptions`：`RetryOptions.Attempts = 1`。实测：对 503、429、500、连接重置，三类调用（流式生成、非流式生成、令牌计数）各只发 1 个请求；同一套测试在 `Attempts=3` 时会看到多次请求，所以测试不是空转。
- **取消后立即关闭供应商连接。** .NET 默认会在丢弃未读完的 HTTP/1.1 响应时继续接收最多 2 秒（`ResponseDrainTimeout`），这是客户端侧的行为。**本地假上游实测**：默认处理器在取消后 2004 毫秒才关闭连接、假上游又写了 64 个分片；`ResponseDrainTimeout = 0` 后在 1 到 3 毫秒内关闭。**这只证明连接关闭行为，不能据此断言真实 Google 服务在这段时间内继续生成或计费**，那需要真实调用才能测。所有 Gemini 客户端共用这个处理器；OpenAI 兼容客户端在批次 1 用 `ConfigurePrimaryHttpMessageHandler` 做同样配置。
- **尚未验证的部分：** 以上都是针对本地假上游在 API Key 模式下测得的，传输层与 Vertex 模式共用，但真实 Vertex 端点（HTTP/2 与否、认证头、`UsageMetadata` 实际内容）要在第一次真实调用时再确认一次。这次调用需要你授权，到时会单独说明。

### 2.4 流式期间

- 取消令牌是三者的联合：`RequestAborted`、停止信号、服务端时长上限（`Runs__MaxDurationSeconds`，见 2.10）。
- **停止检查**：每 1 秒读一次 `runs/{id}.stopRequestedAt`，同时检查进程内的取消注册表（同实例时立即生效）。多实例下停止请求可能落在另一个实例，所以必须经 Firestore 传递。
- **心跳**：每 5 秒在一个小事务里写 `heartbeatAt`（服务端时间戳），并在内容有变化时写检查点 `partial{content, thinking, seq, updatedAt}`（上限 512 KB）。事务先检查 `status == running && leaseOwner == 自己`，不满足就说明租约已丢（被对账器终结），立即取消模型调用并丢弃后续输出（围栏）。心跳绝不写入终态文档。
- **租约**：`heartbeatAt` 超过 30 秒视为失联。时钟用 Firestore 服务端时间戳比较，实例之间的时钟偏差（毫秒级）远小于租约。
- **自我围栏**：如果执行实例连续无法成功续约，在租约剩余不足 10 秒（即距上次成功心跳约 20 秒）时**自己先取消模型调用**，不等对账器，这样僵尸实例不会长时间继续消耗供应商额度。
- **服务端时长上限**：见 2.10，到达上限按 `failed(timeout)` 终结。
- SSE 注释行 `: ping` 每 15 秒一次，防止中间代理断开空闲连接。

### 2.5 终结事务与取消后的持久化

- 终结用 `CancellationToken.None` 并设 15 秒上限，**不能**用已取消的请求令牌，否则取消后写不进去。
- 终结者有两种：持有租约的执行实例，和租约已过期后的对账器；二者用同一个事务，以 `status == running` 为条件，只有一个会写成功。
- 单个事务内：读运行（已终态则无操作）；写助手消息（确定的 ID，有内容才写，`status=final|partial`）；写运行终态字段（状态、原因、耗时、用量、错误、`output` 统计）并清除 `partial`；更新会话的 `updatedAt`、`tokenCount`，并在 `activeRunId == runId` 时清除它。
- Token 记录**在同一个终结事务里**写入 `usageQuotas`（增量），所以只会记录一次，并且和终态原子一致，不会出现「状态已终态但 token 没记」或「记了两次」。已完成沿用现有计数，中断、停止、失败按已生成内容估算，运行里标记 `usage.source = "estimate"`。对账器写终态时同样走这条路径。**配额退还只看 `providerCall`：** 终结时 `providerCall == not_started`（确定没有调用过供应商，例如 P1、P10、P11、准入后立即取消）则在**同一个终结事务**里按运行上记录的 `quotaReservation{requests, estimatedTokens, searches, attachmentBytes}` 原样退还；`started`（可能已调用，供应商可能已计费）则不退还。准入时就被配额拒绝的运行没有预留，也无需退还。

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

现有全局限流每个令牌每分钟 30 次，作用于所有接口。**实测了真实的 ASP.NET 限流中间件**（`RateLimitStackingTests`）：

| 做法 | 结果 |
|---|---|
| 给轮询和停止接口加命名策略 `runs-control`（120 次） | **不能放宽**：命名策略与全局限流叠加，第 4 次仍被全局限流拒绝 |
| 给轮询和停止接口加 `DisableRateLimiting()` | 全局限流也被绕过，**完全不限流**，轮询变成无上限 |
| 在全局限流器内部按路由分类，控制类走独立分区和更大预算 | 控制类 10 次（测试用小数）全部通过，且用完后不影响其他接口的预算；创建运行仍受原限制 |

所以采用第三种：全局限流器的分区函数里把 `GET /api/v2/runs/{id}` 和 `POST /api/v2/runs/{id}/stop` 归为「控制类」，同一个令牌哈希下独立计数，每分钟 120 次（客户端对账轮询间隔 1、1、2、2、4 秒，单个运行每分钟不超过约 30 次）。`POST /api/v2/runs`、会话列表等其余接口保持每分钟 30 次，**没有被放宽**。分类函数是纯函数，批次 1 里直接替换测试里的复刻版本并对真实实现重跑这三条测试。

### 2.10 超时：两个 Cloud Run 服务都要够

一次生成要同时受两个服务的请求超时约束：浏览器到 Web 服务的请求，和 Web 代理到 API 的请求，**二者中较小的那个**决定实际上限。Node 代理本身没有设置任何超时。

**已核实的线上值**（2026-10-17，由你在 Cloud Shell 对项目 `zhumyproject`、区域 `asia-east1` 查询）：`vertex-ai-web` = 60 秒，`vertex-ai-api` = 300 秒。与仓库原默认值一致，所以**现状上限约 30 秒**（60 减去 30 秒收尾余量），较长的高思考级别回复现在就会被截断。

**目标：Web 与 API 都是 300 秒，应用上限 270 秒。**

仓库侧（已完成，有测试，没有执行任何部署）：

- `scripts/deploy-cloud-run.sh`、`.github/workflows/deploy-cloud-run.yml`、`docs/cloud-run.md`：`WEB_TIMEOUT` 默认值 60 秒改为 300 秒。
- 脚本与工作流计算 `Runs__MaxDurationSeconds = min(WEB_TIMEOUT, API_TIMEOUT) − 30`，写入 API 的环境变量；**小于 60 时在任何构建或变更之前报错退出**；无法解析的超时值（如 `5m`）同样报错。当前默认值得到 270。
- 镜像更新路径（保留线上配置）**不做这个校验**，也不会改变线上超时，避免默认值挡住只更新镜像的发布。
- `scripts/test-deploy-cloud-run.py` 新增 4 条测试：默认值得到 270 且 Web 为 300；Web 60 秒被拒绝且只做了一次服务查询；`5m` 被拒绝；镜像更新不带任何 `--timeout`。该文件共 10 条测试全部通过。
- 应用侧在批次 1 读取 `Runs__MaxDurationSeconds`，到点自行终结为 `failed(timeout)`，已生成内容照常保存，这样超时被正确归类，而不是被 Web 层掐断后看起来像连接中断。

**线上变更（尚未执行，需要你确认，我不会自行运行）。** 顺序很重要：先改 Web，再改 API 的环境变量，应用在批次 1 之后才读它。

```bash
# 1) 把 Web 提到 300 秒（会创建新修订并默认接收全部流量）
gcloud run services update vertex-ai-web --project=zhumyproject --region=asia-east1 --timeout=300

# 2) 核对
gcloud run services describe vertex-ai-web --project=zhumyproject --region=asia-east1 --format='value(spec.template.spec.timeoutSeconds)'
gcloud run services describe vertex-ai-api --project=zhumyproject --region=asia-east1 --format='value(spec.template.spec.timeoutSeconds)'
```

需要你知道的两点：

- **代价**：Web 服务是公开的（`--allow-unauthenticated`），超时变长后每个连接最多可占用 300 秒。你说是个人或内部使用，我认为可以接受。
- **合并 `main` 会重新部署**：工作流在 `push` 到 `main` 时以 `--timeout="${WEB_TIMEOUT}"` 完整重配两个服务，所以合并后线上也会按仓库默认值（300 秒）落地；在你确认合并之前它不会发生。

仍待验证：「Cloud Run 请求超时是否计入流式响应的整个持续时间」按官方文档应当包含；在无流量的标签修订上用慢速假上游实测一次，确认后再把 270 秒写死。即使 Web 层先掐断，API 也能通过 2.7 的断开传播收到取消，运行会终结为 `interrupted(connection)`，不会遗留悬空的 `running`。

## 3. Firestore 字段

所有变更只增不改。旧文档没有这些字段，读取时用默认值。

### 3.1 新集合 `runs/{runId}`

```
schemaVersion        1
uid, localUserId
conversationId, turnId, attempt (1), trigger ("send"), retryOfRunId?
userMessageId, assistantMessageId
status               running | completed | stopped | interrupted | failed
statusReason         user | connection | server_lost | quota | safety | provider_error |
                     empty_response | timeout | validation | config | permission | internal
fingerprint          sha256
requested            { providerId, modelName, presetId, customPrompt?, searchMode,
                       thinking{enabled?, level?, budget?},
                       advanced{temperature?, topP?, maxOutputTokens?, safetyThreshold?} }
effective            { 同上形状；服务端解析后的值；不适用的参数不写 }   （meta 事件写入）
attachments          [{fileName, mimeType, sizeBytes, sha256, storageObjectName}]   （不含 base64）
timing               { createdAt, startedAt?, firstOutputAt?, endedAt? }    （均为服务端时间）
usage?               { inputTokens?, outputTokens?, source: "provider" | "estimate" }
finishReason?        供应商给出时才写
error?               { code, message, retryable }
output               { chars, thinkingChars, citations }
stopRequestedAt?, heartbeatAt, leaseOwner
providerCall         not_started | started        （执行权复核标记，2.3.3）
providerCallStartedAt?
providerCalls?       { generate, summarize, countTokens }   （我们自己的计数，终结时写入）
quotaReservation     { requests, estimatedTokens, searches, attachmentBytes }   （退还时按它原样回滚）
retriedByRunId?      （仅失败且无内容的运行，由重试的准入事务一次性写入，见第 4 节）
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
attachments 元素新增： sha256, state（pending | stored | failed；缺省按 stored 处理，保证旧消息不受影响）
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

### 4.1 重试复用用户消息的规则

重试是「同一个用户消息上的又一次运行」，**不是新的用户消息**。服务端实现与界面在批次 2；但字段（`conversations.lastRunId`、`runs.retryOfRunId / attempt / trigger`、`retriedByRunId`）和下面的并发契约在批次 1 就按此设计，避免返工。

**请求形状：** `POST /api/v2/runs`，体为 `{runId, conversationId, retryOfRunId, settings}`，**不带 `message` 和 `attachments`**（带了则 400）。消息和附件一律取自已存储的原用户消息。

**准入事务内依次检查（同一个事务，原子）：**

| # | 条件 | 不满足时 |
|---|---|---|
| 0 | 新 `runId` 的运行已存在 → 按重放处理（先于下面所有检查，所以重放永远幂等） | `Replay` |
| 1 | 来源运行存在、属于当前用户、属于该会话 | 404 |
| 2 | 来源运行已是终态 | 409 `source_not_terminal` |
| 3 | 来源运行**没有助手消息**（本阶段只有失败或停止或中断且无内容的运行可重试，D5） | 409 `source_has_content` |
| 4 | 来源运行是会话里最新的运行：`conversations.lastRunId == 来源 runId` | 409 `not_latest_turn` |
| 5 | 来源运行的 `retriedByRunId` 为空 | 409 `already_retried`，并返回已有后继的 `runId` |
| 6 | 原用户消息的附件全部为 `stored` | 409 `attachments_unavailable` |

**同一个事务里写入：**

- 新运行：`trigger=retry`、`retryOfRunId`、`attempt = 来源.attempt + 1`、`turnId` 沿用、**`userMessageId = 来源的 userMessageId`（复用，不由新 `runId` 派生）**、`assistantMessageId = v5(新runId:assistant)`、`requested` 取自本次请求（用户可以先换模型再重试）、`quotaReservation`。
- 来源运行：只写一个字段 `retriedByRunId = 新runId`。**这是终态运行上唯一允许的修改，一次性写入。**
- 会话：`lastRunId`、`activeRunId`、必要时刷新 `settings`。
- **不写用户消息，不上传附件。**

**执行：** 与普通运行完全相同（执行权复核、心跳、终结），区别只是：用户消息与附件取自存储（对象按 `sha256` 校验），上下文用选取器在「本轮之前的有效轮次」上计算。

**可见性：** 对话里用户消息只出现一次；其下显示最新一次尝试的状态与结果。更早的失败尝试不单独成卡，出现在检视器的运行列表里（「第 N 次运行」及其错误）。会话详情接口返回该会话全部运行摘要。

**与以后的重新生成、编辑重发的关系：** 共用 `trigger / attempt / retryOfRunId` 字段。第 3 条「无内容」的限制将来由「选中版本」规则取代，本阶段 API 触达不到。

**测试：**

| 层 | 测试 | 状态 |
|---|---|---|
| 契约（Firestore 模拟器） | 12 个并发重试同一个失败运行：恰好 1 个后继，来源的 `retriedByRunId` 指向它，后继复用同一 `userMessageId` 且 `attempt=2`，其余得到 `already_retried` 或「未知」后读回；重放赢家的 `runId` 是幂等的 | **已通过** |
| 契约 | 来源仍在运行或已有内容时拒绝 | **已通过** |
| 服务（批次 2） | 重试后消息总数不变（没有新用户消息）；第二次重试返回 409 `already_retried`；有部分内容时 409 `source_has_content`；之后又有新一轮时 409 `not_latest_turn`；原消息附件被复用且存储写入次数为 0；`attempt` 递增；配额只对新运行预留一次；请求里带 `message` 时 400；用户先换模型再重试，运行的 `requested` 是新模型 | 随实现提交 |
| 选取器 | 第一次尝试（失败无内容）被排除，第二次成功后该轮有效，用户消息只出现一次 | 随实现提交 |

## 5. API 增量

所有新接口在 `/api/v2/` 下，认证同现有（Firebase Bearer）。所有权校验失败一律返回 404。

| 方法与路径 | 批次 | 说明 |
|---|---|---|
| `POST /api/v2/runs` | 1 | 创建运行，返回 SSE（第 2.8 节）。普通发送：`runId`、`conversationId`、`message`、`attachments`、`settings`。重试（批次 2）：`runId`、`conversationId`、`retryOfRunId`、`settings`，规则见 4.1。准入失败的错误码：`idempotency_mismatch`、`conflict`、`run_in_progress`、`retry_with_same_run_id`（503）、`source_not_terminal`、`source_has_content`、`not_latest_turn`、`already_retried`、`attachments_unavailable` |
| `POST /api/v2/runs/{runId}/stop` | 1 | 幂等。返回当前运行快照；未知返回 404 |
| `GET /api/v2/runs/{runId}` | 1 | 快照，必要时对账 |
| `GET /api/v2/conversations/{id}` | 1 | 在现有字段之上增加 `runs[]`（全部运行摘要，含 `attempt`、`retryOfRunId`）、`settings`、消息的 `runId` 与 `status`、附件的 `state`；同时对账 `activeRunId` |
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
  FirestoreRunStore.cs                         唯一的存储实现。协议测试直接在 Firestore 模拟器上运行真实的
                                               RunService 与存储，只替换模型，不再维护一份内存版本
  RunService.cs                                生命周期编排（替代 ChatOrchestrator 的内部）
  RunCancellationRegistry.cs                   进程内取消令牌，按 runId
  RunReconciler.cs                             租约过期对账（只写终态，不持有任何模型客户端）
  RunContextSelector.cs                        第 4 节
Api/RunEndpoints.cs                            v2 路由
Api/ChatEndpoints.cs                           旧路由，第三批改为适配器
```

接口变更（内部，非兼容）：

- `IRunStore.AdmitAsync(...)` 返回 `Admitted(AdmittedRun) | Replay | Conflict | Busy | Rejected`；`AdmittedRun` 只能由它构造，模型调用入口只接受它（见 2.3）。
- `IChatQuotaService` 增加可在外部 Firestore 事务内调用的预留方法和「在终结事务内记录用量」的方法；现有独立事务版本保留给旧路径，第四批随旧路径一起删除。

- `IChatAttachmentStore.SaveAsync`：对象名带内容哈希，上传带 `ifGenerationMatch=0`，不覆盖任何已存在对象（2.3.2）；调用方只有 `RunService` 的 `Admitted` 分支。
- `ChatHistoryManager` 的令牌计数和摘要调用接受取消令牌，并计入 `providerCalls`（2.3.4）。
- `OpenAICompatibleChatModelClient` 的 `HttpClient` 用 `ResponseDrainTimeout = 0` 的处理器（2.3.4）。Gemini 侧的 `GeminiHttpOptions` 已在批次 0 完成。
- 限流：把 `AddVertexRateLimiting` 里的分区函数抽成可测试的纯函数，加入「控制类」分类（2.9）。
- 会话文档新增 `lastRunId`，每次准入时在事务里更新（4.1 第 4 条依赖它）。
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

### 批次 0：地基与风险验证（本地可验证部分已完成，不改变用户可见行为）

证据、命令与输出见 [`batch-0-report.md`](batch-0-report.md)。

| 项 | 状态 | 证据 |
|---|---|---|
| Node 代理传递断开 | **已完成** | 先在未修改的代理上复现（3 个失败），修复后 5 个断开与直通测试全部通过；Web 全部测试 21/21 |
| 沙箱具备 .NET SDK | **已完成** | 从 Ubuntu 软件源安装 10.0.112；现有 40 个测试在本地通过 |
| Gemini SDK 一次调用一个请求、取消可用、用量字段存在 | **已完成（本地假上游）** | 三类调用 × 四种失败各 1 个请求；`Attempts=3` 会看到多次，证明测试有效；取消 4 毫秒内结束；`UsageMetadata` 存在。**真实 Vertex 端点仍待一次授权调用确认** |
| 取消后立即关闭供应商连接 | **已完成并修复** | 本地假上游：默认处理器 2004 毫秒后才关闭、假上游多写 64 个分片；`ResponseDrainTimeout=0` 后 1 到 3 毫秒。现已用于所有 Gemini 客户端，测试固化。真实服务侧的效果未验证 |
| Firestore 模拟器与契约测试 | **已完成** | 通过 npm 的 firebase-tools 取模拟器（`gcloud` 组件下载被网络策略拒绝，没有重试）；10 个契约测试在模拟器上连续通过，包括复现并处理了「50 个并发准入整体锁超时」 |
| 限流叠加的实测 | **已完成** | 3 个测试固化了真实行为（2.9） |
| Vite 项目骨架 | **已完成** | 类型检查、lint、Vitest、构建通过；`npm ci` 在干净目录复现；Dockerfile 的每一步在干净目录里逐步复现并从 `/app/` 取到页面；CI 增加 `web-client`、`api-firestore-emulator`、`web-docker` 三个 job |
| 超时的仓库侧变更与校验 | **已完成** | 脚本与工作流默认值、`Runs__MaxDurationSeconds` 计算与报错、10 条脚本测试通过。**线上变更未执行**（2.10） |
| 备份与恢复手册 | **已完成** | [`backup-and-restore.md`](backup-and-restore.md)，命令参数已对照本机 `gcloud --help`；**一个命令都没运行** |
| 真实 Vertex 调用验证取消与用量 | **待你授权** | 到时单独说明范围与成本 |
| `docker build` 在真实 Docker 里执行 | **待 CI** | 沙箱里 Docker 守护进程不可用，只做了逐步复现；`web-docker` job 会真正构建并冒烟。分支推送不会触发 CI（`ci.yml` 只在 `pull_request` 与 `main` 上触发），需要开 PR 才会运行 |
| 无流量标签修订上实测「请求超时包含流式持续时间」 | **待你授权** | 涉及部署，到时单独说明 |

### 批次 1：垂直链路

范围：后端运行服务与 v2 接口，新前端只做**最小壳**：登录、单会话、发送、流式、停止、中断后对账、错误条、读取历史。旧界面和旧接口不动，v2 在 `Runs__Enabled=false` 时返回 404。

验收（每一条都有自动化测试，除最后一条）：

1. **幂等**：同一 `runId` 并发和顺序各发两次，结果只有 1 条用户消息、至多 1 条助手消息、模型调用恰好 1 次、配额预留 1 次；重放返回 `replayed`。
2. 同一 `runId`、不同内容，返回 409 `idempotency_mismatch`；他人的 `runId` 返回 409 且不泄露。
2a. **至多一次的故障注入**（真实的 `RunService` 与 `FirestoreRunStore` 在 Firestore 模拟器上，只有模型是脚本化的，并计数调用次数）：
    - 同一 `runId` 100 个并发准入：恰好 1 个 `Admitted`，其余 `Replay`，模型调用恰好 1 次。
    - P1：准入提交后、模型调用前注入崩溃：对账后终态为 `interrupted(server_lost)`，**模型调用 0 次**，之后任意重放、`GET`、再次对账都不再产生调用。
    - P2/P3：流式中途和终结前注入崩溃：终态由对账器写入，内容来自检查点，模型调用恰好 1 次。
    - 应答丢失：准入事务提交后模拟 SDK 重跑回调，结果为 `Admitted` 而不是 `Replay`，仍只调用一次。
    - P9：对账器先终结，僵尸执行实例随后恢复：它的终结事务为无操作，模型调用被取消，输出被丢弃，不改动终态。
    - 租约丢失围栏：心跳事务发现已被终结，模型调用在 2 秒内被取消。
    - 自我围栏：模拟持续无法续约，执行实例在约 20 秒内自己取消。
    - 准入时配额超额：只写 `failed(quota)` 与用户消息，模型调用 0 次，配额没有被重复扣减。
    - 终结事务里记录用量：同一运行无论终结者是谁，`usageQuotas` 只增加一次。
    - 架构测试：重放、对账、停止三条路径的代码里不引用模型调用入口（以计数 Mock 与类型约束双重断言）。
2b. **执行权复核**（同上，真实服务与存储）：对账器先终结则供应商调用 0 次且标记仍是 `not_started`，退还配额；复核与终结并发，标记为 `started` 当且仅当复核成功；标记事务失败按 P10 读回；`not_started` 的运行被对账时配额按 `quotaReservation` 原样退还，`started` 的不退还。底层的三条契约测试**已通过**。
2c. **附件**：2.3.2 末尾列出的 7 条测试（覆盖、他人会话、同名已存在、准入后崩溃、落盘失败、删除会话、重试复用）。
2d. **限流**：用真实的分区函数重跑 `RateLimitStackingTests` 的三条，另加：创建运行仍是每分钟 30 次；控制类用完预算后创建运行不受影响。三条行为测试**已通过**。
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

### 8.1 批次 1 实现状态与偏差

一条可运行的完整链路已在本地（Firestore 模拟器 + 模拟模型）跑通：浏览器 → Node 代理 → API（RunService + FirestoreRunStore）→ 模型。

已实现：准入事务（重放/冲突/忙/配额）、`providerCall` 标记、租约与心跳、停止（含 2 秒断开宽限）、终态 CAS 写入、对账器（只写终态）、SSE v2（run/delta/citations/done/end）、`Runs__Enabled` 默认关闭、运行控制路由的限流分类、前端状态机与控制器（双击保护、待决运行恢复）。

偏差与推迟（如实）：

| 项 | 状态 |
|---|---|
| v2 带附件 | 推迟到批次 1b；v2 目前拒绝附件 |
| `meta`/`usage`/`finishReason`/`providerCalls`/`effective`/设置端点 | 推迟；用量未写入 |
| 失败运行的重试 | 只有数据设计与契约测试，未实现 |
| 新对话首条消息的地址 | 端到端测试发现：地址在运行结束才设置，运行中刷新会丢失视图。已改为服务端确认运行后立即设置 |
| 断网（离线）模拟 | Playwright 的离线开关不会切断已建立的流，端到端改为「关闭标签页后在新标签页查看」；前端单元测试覆盖 `networkFailed` 路径 |
| Firestore 客户端 | 生产代码改为仅当设置了 `FIRESTORE_EMULATOR_HOST` 才连接模拟器，否则连生产（端到端测试发现原来始终要求凭据） |

测试：后端 94 项（带模拟器）/ 60 项通过 + 34 项跳过（无模拟器）；前端单元 29 项；端到端 6 项。真实 Vertex 调用、线上超时修改、无流量标签修订、备份恢复均未执行。

### 8.2 阶段 2 实现状态与偏差（把骨架做成日常可用的工作台）

已实现并有自动化测试：对齐视觉稿的桌面/手机界面（浅色/深色）、模型/思考/联网选择、Markdown 与长代码块与复制、思考与引用展示（引用现在随回复保存）；三层设置（我的默认、此会话、不可变的运行快照）与按模型能力显示的设置面板；v2 附件（准入后落盘、内容哈希命名、创建即不覆盖、失败不调用模型并清理、历史带附件、按需读取并校验哈希、无存储桶时内联）；运行记录保存实际配置、结束原因、用量、供应商调用次数；会话重命名、导出、删除；草稿恢复；失败原位保留已生成内容；失败重试；另一供应商（OpenAI 兼容）走同一条链路；登录页的注册、Google、重置密码；axe 可访问性检查。

偏差与限制（如实）：

| 项 | 说明 |
|---|---|
| 真实 GCS | 附件的 `ifGenerationMatch=0` 创建即不覆盖只在内存假存储里测过，真实桶的行为未验证；e2e 的 API 也没有桶（走内联路径） |
| 真实供应商 | OpenAI 兼容供应商只用假上游验证了我们这一侧；OpenAI 兼容供应商的用量只在流里带了 `usage` 时才记录，不会为了拿用量去改请求（否则可能被不支持的供应商拒绝），其余标为估算 |
| Firebase 登录 | 注册、Google、重置密码的逻辑与错误文案有测试，真实流程未运行（需要项目和授权域名） |
| 完整检视器 | 数据已保存，界面未做 |
| 并发准入 | 同一进程内对同一 runId 串行；单次准入对存储的等待有上限（默认 20 秒），之后读回并按「无法确认，用同一 runId 重试」处理。此前在模拟器里约三分之一的次数会卡住 240 秒，已修复；模拟器与线上的锁行为是否一致未验证 |
| 附件重试 | 附件没有保存成功的那一轮不能重试（内容已不在服务端），需要重新发送 |
| 导出 | JSON 导出仍会读取所有附件内容 |

测试数量：后端（带模拟器）128 项；前端单元 79 项；端到端 35 项 + 可访问性 3 项；均通过。

## 9. 测试策略

- **纯逻辑优先**：状态机、选取器、幂等指纹、SSE 解析都是纯函数，表驱动覆盖。
- **协议测试接入真实实现**：运行生命周期的测试驱动真实的 `RunService`、`FirestoreRunStore`、配额服务与会话存储（Firestore 模拟器），只把模型换成脚本化的假模型；HTTP 层的测试再叠加真实的接口与 Kestrel。底层的 Firestore 原语契约测试（单赢家、比较并设置、围栏）单独保留，用来证明模拟器与真实 Firestore 共有的语义。
- **取消测试**：Mock 模型客户端记录是否观察到取消令牌、花了多久，断言时限，而不是只断言结果。
- **端到端**：Playwright 对接 Mock 提供商，用路由拦截模拟断网、切断流、慢响应。
- **本地运行**：`dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj`（模拟器测试会标为跳过）；带模拟器：`firebase emulators:exec --only firestore --project demo-contract-tests --config VertexAI.Tests/firebase.emulator.json "dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj --no-build"`（需要 Java 21）。
- **事务争用是常态，不是例外**：模拟器上 50 个并发事务曾整体 `Aborted`。所有依赖事务的协议都必须有「失败即未知、读回确认」的路径，并且测试要覆盖它。
- **不测什么**：不在 CI 里调用真实模型。真实模型只用于批次 0 的一次验证和批次 1 的线上冒烟，都需要你授权。

## 10. 回滚与发布

| 层 | 开关 | 回滚方式 |
|---|---|---|
| v2 接口 | `Runs__Enabled`（默认 false） | 关闭即 404，旧界面不受影响 |
| 旧接口走运行服务 | `Runs__LegacyAdapter`（默认 false，第三批才可打开） | 关闭即恢复使用 `ChatOrchestrator` |
| 默认界面 | `WEB_DEFAULT_UI`（`legacy` 或 `next`） | 改回 `legacy`，不需要重新构建 |
| 整体 | Cloud Run 修订流量 | 把流量切回上一个修订；Web 先作为 `--no-traffic --tag next` 候选验证，**API 新修订部署时立即承接生产流量**（回滚：`rollback api`，见发布手册 5.2） |
| 数据 | 无需回滚 | 全部为可选字段或新集合；旧代码忽略它们。`status=partial` 的助手消息在旧界面里表现为普通消息，没有内容的失败运行在旧界面里不可见，两者都无害 |

约束：

- **不删除任何历史数据**。本方案没有删除或改写现有文档的步骤。
- **模型密钥不进浏览器**：新前端只携带 Firebase 令牌，所有模型调用仍由后端发起，密钥仍来自 Cloud Run 环境与 Secret Manager。
- **不未经确认部署**：索引部署、Cloud Run 发布、环境变量变更都需要你逐项确认。
- **启用 v2 的门槛：先备份、演练恢复、演练回滚**，清单见 [`backup-and-restore.md`](backup-and-restore.md) 第 7 节。恢复分级：先关开关或切回修订（不恢复数据）；只丢个别文档就恢复到临时库再针对性复制；直接对生产库导入是最后手段，因为它覆盖同 ID 文档却不删除之后新建的文档，会让会话元数据与消息不一致。
- **合并 `main` 即上线**：`deploy-cloud-run.yml` 在 `push` 到 `main` 时无条件构建并以 `--set-env-vars`、`--timeout` 完整重配两个服务。本分支在你明确要求之前不合并；合并前需要确认仓库变量 `WEB_TIMEOUT`、`API_TIMEOUT` 以及上线时机。

## 11. 风险与未验证项

| 风险 | 状态 | 应对 |
|---|---|---|
| Gemini SDK 的取消与重试行为 | **已在本地假上游验证**（取消 4 毫秒、默认不重试、已固定为 1 次） | 真实 Vertex 端点上再确认一次，需你授权 |
| 取消后连接多开约 2 秒 | 连接关闭行为已用假上游实测并修复（Gemini）；OpenAI 兼容待批次 1。**真实服务是否因此多生成或多计费未验证** | `ResponseDrainTimeout=0`；真实影响由真实调用验证计划测量 |
| 一次运行含多次供应商调用（计数、摘要） | 已识别 | 全部在执行权标记之后、共用取消令牌、计入 `providerCalls` |
| 事务争用导致准入整体失败 | **已复现** | 失败即未知、读回确认、503 可用同一 `runId` 重试 |
| 附件被覆盖或写入他人路径 | **已识别并设计规避**（2.3.2） | 先准入后落盘、内容哈希命名、`ifGenerationMatch=0`，7 条测试 |
| 僵尸执行者在运行终结后仍调用供应商 | 已设计（2.3.3） | 执行权复核标记，3 条契约测试已通过 |
| 停止请求落在另一个实例，最长约 1 秒延迟 | 已设计 | 进程内注册表加 1 秒轮询 |
| Cloud Run 实例被回收导致运行失联 | 已设计 | 心跳与租约对账；若恰在终结事务前失败，完整回复的尾部可能丢失（P3），已标明 |
| Web 超时 60 秒小于 270 秒上限 | **线上值已核实为 60，仓库侧已改为 300，线上变更待你确认** | 2.10 |
| 合并 `main` 会自动部署 | 事实 | 不合并；合并前确认变量与时机 |
| 请求超时是否计入流式持续时间 | 按文档应当包含，**未实测** | 无流量标签修订实测，需你授权 |
| 可能零次调用（P1'）且配额已扣 | 设计取舍 | 只有 `started` 才不退还；`not_started` 确定没调用，退还 |
| 取消后真实用量未知 | 无法在取消时得知 | 用量标记 `estimate`，不在检视器里冒充精确值；真实调用验证计划会测量取消后的实际计量 |
| 容器 | **已在真实 Docker 里构建并冒烟**（API、Web 两个镜像；Production 镜像 + Firestore 模拟器，`scripts/container-smoke.sh`） | CI `containers` job；沙箱需要注入代理根证书才能构建，CI 与 Cloud Build 用仓库里的原始 Dockerfile。冒烟不含已登录请求（生产镜像按设计拒绝开发用伪用户），也不含真实 Firebase、Vertex、GCS |
| 分支推送不触发 CI | 事实 | 开 PR 前所有验证都是本地做的，结果见批次 0 报告 |
| 限流策略调整影响现有保护 | 低 | 只对读取与停止放宽，创建运行仍是每分钟 30 次 |

### 8.3 完整交付状态（部署就绪）

| 项 | 状态 | 证据 |
|---|---|---|
| 完整运行详情检视器 | 已完成 | 单元（11）、e2e（5，含 axe、手机底部弹层、刷新后、失败与重试链接）、截图 `docs/design/screenshots/impl/deliver/`；`RunSnapshot` 增加 `responseId` |
| 新旧界面切换 | 已完成 | Web `WEB_DEFAULT_UI`（`legacy`/`next`）、`/legacy` 永远可用；代理测试 3 条；容器冒烟覆盖两种模式 |
| 容器构建与冒烟 | 已完成 | `scripts/container-smoke.sh`；CI `containers` job |
| 部署脚本 | 已完成 | `scripts/release-cloud-shell.sh`（计划、备份、索引、构建、部署（API 立即承接生产流量，Web 为无流量候选）、授权域名、启用 v2、验证候选修订、切流量、切界面、回滚、状态；发布记录按项目+区域+服务+提交隔离；索引按完整定义逐项核验并在启用前重查），15 条离线契约测试；`deploy-cloud-run.sh` 与工作流增加 `RUNS_ENABLED`、`WEB_DEFAULT_UI` |
| 发布手册 | 已完成 | [`cloud-shell-release.md`](cloud-shell-release.md)：命令顺序、配置、备份回滚、界面切换、真实验证清单与未验证项 |

## 12. 下一步

1. 你确认第 0 节的决策与第 7 节的截图。
2. 需要你单独授权、我不会自行执行的事项：真实 Vertex 调用（取消与用量）；线上把 Web 超时提到 300 秒；无流量标签修订上的超时实测；备份与恢复演练；任何部署或合并。
3. 确认后进入批次 1，按第 8 节验收逐项完成并给出证据。
