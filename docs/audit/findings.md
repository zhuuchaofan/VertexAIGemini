# 问题清单（Findings）

审查日期 2026-10-16。证据等级：**[V]** 代码已验证　**[C]** 确认缺陷　**[R]** 合理风险推断　**[U]** 云端未验证。
类型：Bug / 设计风险 / 技术债 / 可选优化 / 业务待定。

**结论先行：未发现 P0（无已确认的跨用户数据泄露、认证绕过或可远程利用的严重漏洞）。** 发现 8 项 P1、若干 P2/P3。Google 官方依据一栏：`docs.cloud.google.com` 在本环境无法访问，故仅写出应核对的官方文档主题，**未引用具体版本号或原文**。

## 汇总

| ID | 级别 | 标题 | 类型 | 证据 |
|---|---|---|---|---|
| F-01 | P1 | 开放注册 + 无全站预算上限 → 费用不可控 | 设计风险 | [R]+[U] |
| F-02 | P1 | web 超时 60s < API 300s，长流式被静默截断且前端显示"成功" | Bug | [C] |
| F-03 | P1 | 取消/断线不向上游传播，客户端离开后仍可能继续产生模型费用 | Bug | [C] |
| F-04 | P1 | 每次部署 `--set-env-vars` 覆盖全部环境变量；供应商密钥无 Secret Manager | 设计风险 | [C] |
| F-05 | P1 | 配额算法不可信：重复计入、失败不退还、估算用 Base64 长度 | Bug | [C] |
| F-06 | P1 | `MAX_TOKENS` 截断被当作成功完成（输出上限含思考 token） | Bug | [C] |
| F-07 | P1 | 部署流水线从未成功；发布即 100% 流量，无渐进/回滚 | 设计风险 | [C]+[U] |
| F-08 | P1 | 失败请求留下孤儿用户消息、丢失已生成部分，历史出现连续 user | Bug | [C] |
| F-09 | P2 | 附件 `SizeBytes` 由客户端提供，可绕过附件字节配额 | Bug | [C] |
| F-10 | P2 | Google.GenAI 0.14.0（最新 1.26.1）；2 个预览模型硬编码 | 技术债 | [C] |
| F-11 | P2 | 搜索：`force` 仅为提示词；引用取自 groundingChunks，未用 groundingSupports/搜索建议 | 设计风险 | [C] |
| F-12 | P2 | OpenAI-compatible 能力差异被 UI 掩盖（搜索/PDF/思考） | 设计风险 | [C] |
| F-13 | P2 | 打开会话/重载历史时全量读取消息并从 GCS 载入所有附件 | 性能/成本 | [C] |
| F-14 | P2 | 限速：进程内、按原始 Authorization 分区（可被垃圾头撑爆内存）、信任 XFF | 设计风险 | [C] |
| F-15 | P2 | 请求体上限未显式配置；Base64 多份拷贝 × 并发 20 × 1Gi | 设计风险 | [R] |
| F-16 | P2 | 删除会话非原子、串行；无账号级删除/数据保留策略；孤儿对象 | 设计风险 | [C] |
| F-17 | P2 | 同会话并发发送无串行化/幂等键 | 设计风险 | [R] |
| F-18 | P2 | 日志非 JSON、无 trace 关联、无告警/SLO | 技术债 | [C] |
| F-19 | P2 | 邮箱未验证即可使用；`EmailVerified:true` 硬编码；未检查 token 吊销 | 设计风险 | [C] |
| F-20 | P2 | 测试缺口：无 Firebase/Firestore 模拟器、无代理/SSE 截断/配额并发测试 | 技术债 | [C] |
| F-21 | P2 | 前端：无停止按钮/AbortController、401 与断网处理缺失、长列表无分页渲染 | 体验 | [C] |
| F-22 | P2 | Web 代理加固：无超时/AbortSignal/背压/请求头白名单，令牌获取失败静默放行 | 设计风险 | [C] |
| F-23 | P3 | 容器：root 运行、无用 HEALTHCHECK（Cloud Run 不使用）、apt 安装 curl | 技术债 | [C] |
| F-24 | P3 | 无 CSP；Firebase SDK 从 CDN 动态导入且无 SRI | 设计风险 | [C] |
| F-25 | P3 | 客户端提供的 `StorageObjectName` 被接收（现被覆盖，仍应剥离） | 纵深防御 | [C] |
| F-26 | P3 | 管理员安全阈值默认 `OFF`；admin 端点经公网代理可达 | 业务待定 | [V] |
| F-27 | P3 | 游标分页按 `updatedAt` 有并列风险；offset 分页按跳过文档计费 | 技术债 | [C] |
| F-28 | P3 | 标题无长度限制；Markdown 导出原样写入内容；`.agent/skills` 为 Blazor 过时规范 | 技术债 | [V] |

---

## P1

### F-01 开放注册 + 无全站预算上限
- **证据**：配额仅按用户计（`Services/Quota/FirestoreChatQuotaService.cs`，默认 100 请求/日、20 万 token/日）；无全站上限、无 Billing Budget 配置（仓库无任何 budget/告警资源）；Firebase 注册是否开放为云端设置 **[U]**。前端有"注册"流程（`app.js` 中 `email-already-in-use` 处理）。
- **当前行为**：任何人可注册任意数量账号，每号享受独立配额；`max-instances` 10×并发 20。
- **风险条件**：Firebase Auth 启用邮箱/密码或 Google 且未限制域名/邀请。
- **影响**：N 个账号 × 100 次/日 × 含联网搜索与大上下文，费用线性放大；无自动熔断。
- **官方依据（待核对）**：Well-Architected 成本优化支柱（预算与告警、配额控制）；Billing Budgets 文档。
- **方案**：①创建 Billing Budget + 50/90/100% 告警（只读检查见验证计划）；②API 层新增**全站日预算**文档（事务计数），超限返回明确错误；③个人项目选择"邀请制/白名单邮箱"或 Firebase blocking function/`allowlist` 集合；④必要时 Identity Platform 关闭自助注册。
- **备选**：保持开放但降低默认配额并要求邮箱已验证（见 F-19）。
- **工作量/风险**：1–2 天 / 低。
- **验证**：用测试账号并发打满全站上限，确认拒绝；Budget 告警邮件到达。

### F-02 超时不一致，长流式被静默截断
- **证据**：web 超时默认 `60s`（`deploy-cloud-run.yml:53`），API `300s`。Cloud Run 请求超时针对整个请求时长（**[U]** 官方行为请核对 Request timeout 文档；搜索摘要一致）。前端 `readEventStream` 在流结束但无 `final` 事件时直接返回，随后显示"API 已连接"（`app.js` 约 930–945 行）。
- **当前行为**：生成超过 60 秒（Pro + high 思考 + 搜索很常见）时连接被切断；UI 保留半截内容并显示成功；服务端仍可能继续生成（见 F-03），消息也未保存（F-08）。
- **方案**：web 超时 ≥ API 超时（同设为 300s，或合并服务）；SSE 每 15s 发送 `: ping` 心跳；前端检测"未收到 final"并显示"连接中断，已保留部分内容 / 重试"。
- **工作量**：0.5 天 / 低。**验证**：模拟 90s 流，确认完整接收；人为断开，UI 提示中断。

### F-03 取消与断线不向上游传播
- **证据**：`ChatEndpoints.StreamChatAsync` 未使用 `context.RequestAborted`；`ChatOrchestrator.SendAsync` 无 `CancellationToken`；`GeminiService.cs:266` 的 `GenerateContentStreamAsync` 未传取消令牌；OpenAI 客户端传入 `CancellationToken.None`（`OpenAICompatibleChatModelClient.cs` 的 `StreamChatAsync(request)`）；`server.mjs:65` 的 `fetch` 无 `signal`，`:91` 的 `for await` 不监听 `res.close`；前端无 `AbortController`。
- **行为**：用户关闭页面/断网后，API 在下一次写入失败前继续消费模型输出；Kestrel 写入失败后的 `final` 写入还会再抛异常。无"停止生成"按钮。
- **影响**：断线后仍付费；`finally` 中仍结算 token。
- **方案**：全链路传递 `RequestAborted`（endpoint → orchestrator → provider → SDK）；web 代理 `req.on('close')` 触发 `AbortController`；前端增加停止按钮；取消后按已生成内容保存并标记 `cancelled`。
- **工作量**：1–2 天 / 中（涉及接口签名）。**验证**：集成测试——读取 3 个 chunk 后断开，断言上游枚举器被 Dispose、模型调用停止、消息以 cancelled 保存。

### F-04 部署覆盖环境变量；供应商密钥管理缺失
- **证据**：`deploy-cloud-run.yml:170` 使用 `--set-env-vars`（会替换服务上全部环境变量）；`appsettings.json` 要求 `DEEPSEEK_API_KEY/MOONSHOT_API_KEY/...` 来自环境变量，但流水线从不设置，也没有 `--set-secrets`。
- **影响**：手工配置的供应商密钥会在下次部署时被清空，对应供应商静默变为"未启用"（`IsConfigured` 过滤）。如果为了持久改成明文 env，则密钥落入 Cloud Run 修订版配置与控制台可见。
- **方案**：密钥放 Secret Manager，`--update-secrets`/`--set-secrets` 挂载；非敏感变量用 `--update-env-vars`；运行 SA 仅授予具体 secret 的 `secretAccessor`。
- **官方依据**：Cloud Run "Configure secrets" 文档；安全支柱（Secret 管理）。
- **工作量**：0.5 天 / 低。**验证**：`gcloud run services describe` 对比部署前后 env/secrets。

### F-05 配额算法不可信
- **证据**：
  - `FirestoreChatQuotaService.cs:136`：`EstimatedTokens + ActualTokens > DailyTokenLimit`。预留时加入估算值，请求结束再累加实际值，**同一请求被计两次**。
  - `ChatOrchestrator.cs:172` 估算 = (消息字符 + 附件 **Base64 长度**)/4：一个 512KB PDF ≈ 17.5 万"token"，几乎耗尽 20 万日额度；一张 >600KB 图片即被拒（前端压缩到 ≈700KB 上限，边界附近）。
  - 预留后失败/取消/配额之外的错误**不退还**；`RecordTokenUsageAsync` 的实际值（OpenAI 客户端为字符数/4 估算；Gemini 为历史 `CountTokens`）不是供应商返回的 `usageMetadata`。
  - 日界线用 UTC（`TodayDateKey`），与用户感知日界不一致。
- **影响**：用户体验上莫名被限额；成本上无法反映真实花费（搜索、思考 token、图片单价均未体现）。
- **方案**（个人项目最小可行）：以"请求次数 + 供应商返回的真实 token（`usage_metadata`）"为准：预留只占 `requests`/`searches`；结束后按真实 token 结算（事务内 `FieldValue.Increment`）；失败按实际已消耗结算；日界线改为 `Asia/Shanghai` 或可配置。成本折算见 modernization-plan。
- **备选**：只用请求次数（最简单，但对长上下文不公平）；按美元折算（最准，需维护单价表）。
- **工作量**：1–2 天 / 中。**验证**：并发 50 请求不超限；失败请求配额变化符合预期；单元测试覆盖。

### F-06 `MAX_TOKENS` 被当作成功
- **证据**：`GeminiService.cs:517` `MaxOutputTokens = 8192` 固定；Gemini 的输出上限包含思考 token，high 思考可能耗尽预算；`IsBlockedFinishReason` 不含 `MAX_TOKENS`，只要 `responseText` 非空即视为成功（约 340–346 行）。OpenAI 客户端同样 `max_tokens=8192` 且不检查 `finish_reason`。
- **影响**：回答被截断但标记 `succeeded=true` 并持久化。
- **方案**：读取 `FinishReason`/`finish_reason`；`MAX_TOKENS` 返回 `truncated=true` 事件，UI 显示"回答被截断 · 继续生成"；提高上限或让用户选择；对思考模型单独设预算。
- **工作量**：0.5–1 天 / 低。**验证**：模拟器/回放返回 MAX_TOKENS，断言事件与 UI。

### F-07 部署流水线从未成功；无渐进发布
- **证据**：`Deploy Cloud Run` 在"Validate deployment configuration"因缺少仓库变量/机密失败（用户侧配置，已知）；`gcloud run deploy` 直接 100% 切流；烟测在切流**之后**才运行（`deploy-cloud-run.yml` 末段）；每次运行还重复执行 bucket 创建、IAM 绑定、索引创建，意味着部署 SA 需要较大权限（bucket IAM、run IAM、datastore 索引）；Artifact Registry 无清理策略；构建依赖默认 Cloud Build 身份 **[U]**。
- **影响**：首次上线风险高；坏版本直接影响全部用户；回滚靠手工。
- **方案**：①先用 `--no-traffic --tag=candidate` 部署，对 candidate URL 烟测后 `update-traffic --to-latest`；失败不切流；②将"一次性基础设施"（bucket、IAM、索引）从每次部署移到 `scripts/bootstrap`；③拆分 CI 部署 SA 权限；④Artifact Registry 增加清理策略；⑤`--cpu-boost` 缩短冷启动。
- **官方依据（待核对）**：Cloud Run "Rollbacks, gradual rollouts, and traffic migration"；运营卓越支柱。
- **工作量**：1–2 天 / 低–中。**验证**：故意部署坏镜像，确认流量不切换。

### F-08 失败请求的持久化语义
- **证据**：`ChatOrchestrator.cs` 先写 user 消息（约 72–80 行），模型消息只在成功后写（约 123–131 行）；异常路径只返回错误，不写入已生成的部分；`final` 事件在失败时前端用 `errorMessage` **替换**已显示内容（`app.js` final 处理）。
- **影响**：历史出现连续 user；重载会话后看不到失败/部分回答；Gemini 要求角色交替时，历史拼接可能产生 400。
- **方案**：失败/取消时写入带 `status: failed|cancelled|truncated` 的 model 消息（保留部分内容）；加载历史时合并或跳过未完成回合；前端失败时保留已生成内容并追加错误提示。
- **工作量**：1 天 / 低。

## P2

### F-09 附件 `SizeBytes` 客户端可控
`ChatAttachment` 直接反序列化请求体（`Services/Chat/ChatAttachment.cs`），`EstimateAttachmentBytes`（`ChatOrchestrator.cs:178`）优先采用 `SizeBytes`。传 `sizeBytes:1` 即可绕过附件字节配额。**修复**：忽略客户端 `SizeBytes/StorageObjectName`，用解码后真实长度。0.25 天 / 低。

### F-10 SDK 与模型生命周期
- `Google.GenAI` 固定 **0.14.0**；NuGet 当前最新为 **1.26.1**（2026-10-14，已核对 nuget.org）。0.x → 1.x 跨越大版本，需评估破坏性变更；官方 SDK 与 Gemini 3 特性（thinking、grounding 字段）同步在新版本。
- 配置含 `gemini-3-flash-preview`、`gemini-3.1-pro-preview` 两个**预览**模型；搜索摘要显示 `gemini-3-pro-preview` 已被 3.1 Pro 替代并停用，且 Gemini 2.5 系列在 Vertex 上将于 2026-10-16～20 退役（来源互相不一致，须以官方 model-versions 页面为准）。预览模型无稳定性/SLA 保证，且无退役监控。
- Firebase JS SDK 固定 11.10.0 经 CDN 加载（`app.js:232`）。
- **方案**：升级到 1.x 并在 CI 加"模型存活"检查（见验证计划）；默认模型改 GA 稳定版，预览模型标注"预览"；订阅 Vertex release notes；把模型目录移到可热更新的配置并记录 `retireAfter`。1–2 天 / 中。

### F-11 搜索与引用
- 证据：`force` 模式仅在消息前添加一句提示（`WebSearchInstructionAugmenter.cs`）——**不保证实际执行搜索**；auto 模式只是挂载 `GoogleSearch` 工具。引用仅取 `GroundingChunks[].Web`，不读 `GroundingSupports`（文本片段↔来源映射）与 `WebSearchQueries/SearchEntryPoint`；引用列表是**本次响应所有候选来源**，不等于"被引用的来源"，且按 URI 去重；URI 为 Google 重定向链接。
- 影响：可能展示与具体句子无关的引用；搜索未执行时用户无感知；Google Search Grounding 的使用条款通常要求展示 Search Suggestions（**[U]** 以官方 Grounding with Google Search 文档为准）。
- 方案：解析 `groundingSupports` 生成行内角标 `[1][2]`；渲染 `searchEntryPoint.renderedContent`（按条款）；在 `force` 模式下，若响应无 grounding 元数据则返回"未检索到来源"提示而非静默；UI 显示"本次是否执行了联网搜索"。1–2 天 / 中。
- 自动测试：最新信息题（应有 grounding）、关闭搜索（应无）、搜索失败（降级提示）、不支持搜索的模型（UI 禁用）。

### F-12 供应商能力差异被掩盖
`OpenAICompatibleChatModelClient`：搜索模式被忽略（但仍扣 `searches` 配额）；PDF 被替换成"[无法直接读取]"文本；图片作为 `image_url` 发送而无视模型是否支持视觉；思考靠 `Thinking.Kind` 字符串 switch。**方案**：在 `ChatModelOption` 增加 `Capabilities {streaming, vision, pdf, webSearch, thinking, usage}`，`/api/workspace/config` 下发，前端按能力禁用控件，后端对不支持项返回 400 而非静默降级。1 天 / 低。（能力矩阵见 maturity-assessment.md。）

### F-13 会话读取与附件重载成本
- `GetConversationAsync`（`FirestoreConversationStore.cs:64`）无分页读取全部消息，并对每条消息从 GCS 下载全部附件、Base64 回灌到 API 响应；`GetHistoryAsync` 对最近 40 条同样重载附件并**再次发送给模型**，图片重复计费且延迟高。
- **方案**：详情接口返回附件元数据 + 受控下载端点（带所有权校验、短时 Signed URL 或流式代理）；消息分页（最近 N 条 + 向上加载）；历史重放只携带最近一次附件，其余用占位文本。1–2 天 / 中。

### F-14 限速
`ServiceCollectionExtensions.cs:60`：分区键为 `Authorization` 头 SHA-256；无效/随机头会为每个请求创建新分区（限速器在鉴权之前运行），内存无界增长且不受限；无头时直接信任 `X-Forwarded-For`；窗口计数为每实例本地。**方案**：先鉴权后限速（或鉴权失败共用一个分区）；使用 `ForwardedHeaders` 的受信代理配置；Pre-auth 层在 web 代理做每 IP 令牌桶；删除无引用的 `AuthRateLimiter`。0.5 天 / 低。

### F-15 请求体与内存
Kestrel 默认请求体上限约 30MB，Cloud Run 对 HTTP/1 请求上限 32MiB **[U]**。8 张×4MB 图片的 Base64 JSON 远超；`ChatAttachmentValidator.TryGetDecodedLength` 为每个附件分配 `byte[base64.Length]`，之后 `GeminiPartFactory` 再解码一份，并存在 JSON 字符串、请求对象多份拷贝。API 1Gi × 并发 20 存在 OOM 风险。**方案**：`MaxRequestBodySize` 设为 ≈12MB；总附件上限；用 `Base64.GetMaxDecodedFromUtf8Length` 计算长度而非分配；评估并发降到 8–10。0.5 天 / 低。

### F-16 删除与保留策略
`DeleteConversationAsync` 逐条串行删除文档，附件 `DeleteAsync` 若抛非 404 异常会中断（会话文档最后删，可重试，但无补偿）；`usageQuotas` 永久保留；无账号级"删除我的数据"；附件保存成功但消息写入失败会留下孤儿对象；桶无 Lifecycle 规则（工作流创建桶时未设置）。**方案**：`BulkWriter`/分批删除 + 失败重试；GCS Lifecycle（未关联孤儿 N 天清理）与 Firestore TTL（`usageQuotas`）；提供账号删除脚本。1 天 / 低。

### F-17 并发与幂等
同一会话并发提交没有锁/序号；无客户端请求 ID，网络重试会重复写入与重复扣费。**方案**：前端生成 `clientRequestId`，后端按（会话,requestId）做幂等；对同会话使用 Firestore 事务序号或拒绝并发。1 天 / 中。

### F-18 可观测性
Serilog 控制台文本格式（`Program.cs`），Cloud Logging 无法获得 `severity` / `logging.googleapis.com/trace`；没有请求耗时、首 token 延迟、供应商错误率、配额拒绝的指标与告警；Node 服务仅 `console.log`。**方案**：JSON 控制台格式（含 severity、trace）；记录首字节时间/总时长/token/供应商；Cloud Monitoring 日志指标 + 5xx、延迟、Budget 告警。1 天 / 低。

### F-19 账号与令牌
`VerifyIdTokenAsync(idToken)`（`FirebaseUserContext.cs:32`）未启用吊销检查；不要求 `email_verified`；`AuthEndpoints.cs:27` 把 `EmailVerified` 硬编码为 `true`（对前端展示是误导）。**方案**：对管理接口使用 `checkRevoked: true`；普通对话要求已验证邮箱（Google 登录天然已验证）；返回真实 `email_verified`。账号关联风险（同邮箱不同提供商）取决于 Firebase "每邮箱一个账号" 设置 **[U]**。0.5 天 / 低。

### F-20 测试体系
C# 测试为自写 `Program.cs` 运行器（≈1500 行/25 项，覆盖编排、校验、目录、端点的内存假实现），**没有** Firestore/Firebase 模拟器、配额并发、存储所有权、SSE 取消与截断、供应商回放测试；Web 只有 markdown/sse 单测，无代理测试、无浏览器 E2E。（本环境无法运行 C# 测试。）方案见 verification-plan.md。2–4 天 / 低。

### F-21 前端体验
无停止/重试按钮、无 `AbortController`；`apiFetch` 不处理 401（令牌过期/吊销）；离线无提示；会话详情一次性渲染全部消息（无虚拟化/分页）。**不需要迁移框架**：原生 JS 已拆分为模块，问题集中在四个行为（取消、重试、401、分页），均可在现有结构内解决。1–2 天。

### F-22 Web 代理加固
`server.mjs`：无上游超时与 `AbortSignal`；`res.write` 忽略背压；转发全部请求头（含 `cookie`、`x-forwarded-*`）；对任意 `/api/*` 路径与方法透传；`getServerlessIdentityToken` 失败返回 `null` 后**仍然转发**（API 返回难以诊断的 403）；令牌缓存无并发去重。**方案**：路径白名单、`pipeline()` 处理背压、超时与取消、令牌失败返回 502。（若采用单服务方案则整体移除。）0.5 天 / 低。

## P3

- **F-23 容器**：`VertexAI/Dockerfile` 以 root 运行，`apt-get install curl` 仅为 `HEALTHCHECK`（Cloud Run 忽略 Docker HEALTHCHECK）；应改用非 root（`USER app`）、删除 curl、在服务上配置 startup/liveness 探针指向 `/health/live`。`apps/web/Dockerfile` 同理（`USER node`）。
- **F-24 CSP/SRI**：服务端只设 `nosniff`/`referrer-policy`/`x-frame-options`，无 CSP；Firebase SDK 动态 import 无 SRI。建议在真实浏览器验证后上线 `Content-Security-Policy`（允许 `www.gstatic.com`、`*.googleapis.com`、`*.firebaseapp.com`），或改为自托管并锁版本。
- **F-25 StorageObjectName**：客户端字段被反序列化；当前 `SaveAsync` 会覆盖，且验证要求 `Base64Data` 非空，未发现可利用路径；仍应在入口处剥离，避免未来改动引入任意对象读写/删除（`DeleteAsync` 使用库内值）。
- **F-26 管理员阈值**：`AdminThreshold` 默认 `OFF`（`GeminiModels.cs:29`），这是业务决定；admin 端点通过公网 web 可达，仅靠 custom claim；建议记录审计日志并考虑 `checkRevoked`。
- **F-27 分页**：游标仅用 `updatedAt`，同一时间戳多条会漏/重（`StartAfter(Timestamp)`）；offset 分页按被跳过文档计读费用。建议游标含文档 ID。
- **F-28 杂项**：标题无长度上限（`ConversationEndpoints.UpdateTitleAsync`）；`.agent/skills` 中 Blazor 规范与现架构不符；`ARCHITECTURE.md` 待办 1–4 仍有效。

## 已核查但**未**发现问题的项
- 跨用户读/写/删：`GetConversation/AddMessage/UpdateTitle/Delete/UpdateTokenCount/GetHistory` 均校验 `uid`；他人 `conversationId` 传入 `chat/stream` 时历史为空、消息不写入，无泄露（仅浪费配额）。
- Markdown 渲染注入：DOM 构建，链接限 http/https/mailto/tel；已有测试。
- 静态文件路径穿越：`normalize` + 前缀检查。
- 仓库中未发现硬编码密钥（Firebase Web API Key 本身按设计是公开的）。
