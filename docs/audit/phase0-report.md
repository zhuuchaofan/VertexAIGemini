# Phase 0 实施报告

分支：`phase0-hardening`（基于 `claude`，未合并到 `main`，未创建 PR，未部署，未改动任何云端配置）
日期：2026-10-16

## 1. 对原审查结论的修正

实施前重新读取了五份报告并逐项对照代码。以下结论已被修正，以此为准：

| 原结论 | 修正 | 依据 |
|---|---|---|
| F-10：Gemini 2.5 系列即将退役，需评估 | **项目不依赖 Gemini 2.5。** 全仓库没有 `gemini-2.x` 引用（`mimo-v2.5-pro` 是小米模型，无关）；默认模型 `gemini-3.5-flash`；4 个配置模型都是 3.x，其中 `gemini-3-flash-preview`、`gemini-3.1-pro-preview` 为预览。2026-10-20 的退役不影响现有配置。**3.x 各模型自身的生命周期未能核实**（本环境无法访问官方页面） | `grep` 全仓库；`appsettings.json` |
| F-10：升级 Google.GenAI 到 1.x | **不能直接升级，Phase 0 保持 0.14.0。** 在隔离副本里改为 1.26.1 后无法编译：`HarmCategory`、`HarmBlockThreshold`、`ThinkingLevel`、`FinishReason` 的枚举成员全部改名。0.14.0 在编译层面具备所需字段（`ThinkingLevel.MINIMAL…`、`FinishReason.MAX_TOKENS`、`GoogleSearch`、`GroundingMetadata`），但**未对真实 Vertex 验证** | 隔离编译实验；SDK 反射 |
| F-01：Billing Budget 告警 | 改为：应用层限额（立即生效）+ Cloud Run `max-instances` + **Spend cap budgets**（Vertex AI 与 Cloud Run，有计费延迟，可能超额）。Spend cap **不覆盖**第三方模型供应商。我无法打开官方页面，操作步骤只写到决策点，见 `docs/cost-controls.md` | 你提供的公告信息；页面本身未读到 |
| F-07：部署流水线"从未成功" | 更准确：**GitHub 工作流从未成功**，但 `docs/cloud-run.md` 记录了手动部署（API/web 各 12 个修订版），说明**线上已有环境**。合并到 `main` 会自动触发生产部署工作流 | `docs/cloud-run.md`；`deploy-cloud-run.yml` 触发条件 |
| F-03：取消"全链路传播" | 需要限定：Google.GenAI 0.14.0 **忽略**取消令牌（实验：`WithCancellation` 后流照常跑完 40 块）；跳出循环能关闭上游连接，提前释放 `Client` 不能。因此 Vertex 路径只能在"下一个数据块边界"终止 | 本地假流式服务器实验（API-key 模式 + 自定义 BaseUrl，无云凭据） |
| 新发现（审查时未发现） | **F-29：`Authorization: Bearer not.a.jwt` 触发 500**。Firebase SDK 对畸形令牌抛 Newtonsoft `JsonReaderException`，原代码只捕获两类异常；`UseExceptionHandler("/Error")` 指向不存在的路由造成二次异常。匿名请求即可触发（日志污染与误导性 5xx，**不是**认证绕过）。已修复并加回归测试 | 启动真实 API 进程冒烟时复现 |

## 2. 各问题状态

状态含义：**已解决** = 代码已改且有自动化测试；**部分解决** = 有明确余项；**未解决** = Phase 0 范围外。除非另注明，"已解决"不等于已在真实云环境验证。

| 编号 | 状态 | 做了什么 | 余项 / 限制 |
|---|---|---|---|
| F-01 费用失控 | 部分解决 | 全站日上限（请求/Token/搜索，Firestore 事务内与用户配额一并检查）；每用户/每实例并发上限；访问白名单与可选"必须验证邮箱"（后端强制）；`max-instances` 默认收紧为 api=3/web=2 | **默认仍是开放注册**（白名单为空）；Spend cap 与第三方供应商额度需你在控制台设置；并发上限仅单实例有效；全站计数文档是写热点（个人规模足够） |
| F-02 超时不一致 | 已解决 | web 默认 60s→300s；代理整体上限 295s；应用层 240s 生成上限；部署前校验 web≥api；SSE 15s 心跳 | 未在真实 Cloud Run 验证 |
| F-03 取消传播 | 部分解决 | 浏览器→代理→API→OpenAI 兼容供应商均传播取消并关闭连接（有测试，含真实浏览器端到端）；前端停止按钮；写失败视为取消 | Vertex 路径仅在块边界终止（见上）；静默思考期间取消后，上游可能继续到下一块；预热阶段 `CountTokens`/摘要不会被中止；GFE 对容器中途断开的表现未验证 |
| F-04 `--set-env-vars` | 已解决（代码） | 全部改为 `--update-env-vars`/`--update-secrets`；Secret Manager 接入方案与迁移步骤见 `docs/secrets.md`；测试守护 | 需要你创建密钥并设置 `API_SECRETS`；"明文→密钥引用"的类型冲突错误文案未实测 |
| F-05 配额算法 | 部分解决 | 结算改为"释放估算、记入实际"，消除重复计数；任何路径只结算一次；服务端按解码长度估算（PDF 512KB 不再≈17.5 万 token）；到达模型前失败不占 Token | 实际用量仍来自 `CurrentTokenCount`（Gemini=历史 CountTokens，OpenAI 兼容=字符数/4），非供应商 usage（Phase 1）；UTC 日界线未改；搜索/附件失败不退还 |
| F-06 截断当成功 | 已解决（代码） | 识别 `MAX_TOKENS` / `finish_reason=length`，状态 `truncated`，前端提示 | Gemini 路径依赖 SDK 的 `FinishReason` 在真实响应里被填充（编译层面存在，**未对真实 Vertex 验证**） |
| F-08 失败语义 | 已解决 | 终止状态 `completed/truncated/cancelled/failed`；部分回复保存规则；失败回复不喂回模型；连续 user 回合合并；单次保存与结算 | 无 |
| F-09 SizeBytes 可伪造 | 已解决 | 配额估算不再读取客户端 `SizeBytes` | `StorageObjectName` 入口剥离（F-25）未做 |
| F-19 账号 | 部分解决 | `/api/auth/status` 返回真实 `emailVerified`；白名单要求已验证邮箱；同一请求只验签一次 | 前端没有"发送验证邮件"流程；未启用吊销检查 |
| F-22 代理加固 | 部分解决 | 取消、背压、整体超时、方法限制、中途失败断开连接、令牌缺失 502、不信任客户端 `x-serverless-authorization` | 路径白名单未做 |
| F-29 畸形令牌 500 | 已解决 | 见上 | 无 |
| F-10 模型与 SDK | 部分解决 | `Stage`/`RetireDate` 配置化；退役模型自动下架；未知/已退役模型显式报错（不再静默回退到第一个模型）；上游 404 映射为可读错误；预览模型前端标识 | SDK 未升级（单独迁移）；3.x 生命周期需你核对官方页面后填写 `RetireDate` |
| F-11/F-12 搜索引用、能力矩阵 | 未解决 | — | 留待 Phase 2；Gemini 的 `force` 仍只是提示词 |
| F-13/14/15/16/17/18/20/21 等 | 未解决 | — | Phase 1/2 |

### 能力验证（要求："验证普通聊天、流式、图片/PDF、思考、联网搜索"）
**无法在本环境完成。** 没有 GCP 凭据，无法调用 Vertex AI。已做到的是：
- 编排器、流式、取消、截断、失败、配额的行为由自动化测试覆盖（使用假模型客户端与假上游流）；
- 图片/PDF 的请求构造由既有 `GeminiPartFactory` 测试覆盖；
- 思考参数映射（含 Gemini level 与 6 家兼容供应商）由既有目录/客户端测试覆盖；
- **以上均不证明真实 Vertex/供应商会接受请求。** 请部署候选修订版后，用 `docs/audit/verification-plan.md` 中的"模型冒烟"清单逐项人工验证；对不兼容项，系统的行为是显式报错（未知模型）或显式提示（截断/失败），不静默降级。

## 3. 提交（均在 `phase0-hardening`，保留独立历史）
```
585d471 fix(auth): 畸形 Bearer 令牌返回 401 而非 500，并提供 JSON 500 兜底
d66aa60 fix(deploy): 合并式配置、无流量候选发布、冒烟失败自动回滚与 Secret Manager 接入
b8fef01 feat(stream): 流式生成的取消、超时、终止状态与部分回复保存
4bac40f feat(cost): 全站日上限、并发上限、访问白名单与配额结算修复
75a01eb feat(models): 配置化模型生命周期并显式拒绝未知/已退役模型
```
（按组提交：模型 → 费用 → 流式 → 部署 → 认证修复；报告与测试告警清理另见分支末尾的文档提交。）

## 4. 构建与测试结果（本环境实际执行）
| 项 | 结果 |
|---|---|
| `dotnet build VertexAI.slnx` | 成功，0 警告 0 错误（dotnet SDK 10.0.112 经 apt 安装） |
| C# 测试（自写运行器） | **68 项全部通过**（基线 32 项） |
| Node 单测 `npm test` | **32 项全部通过**（基线 10 项；新增代理 10 项、状态机 12 项） |
| 浏览器端到端 `test-e2e/stream.e2e.mjs` | **6 项通过**（真实 Chromium + 真实 `server.mjs`/`proxy.mjs` + 脚本化假后端；覆盖完成/截断/失败/连接中断/停止并关闭上游/无页面错误）。需要 Playwright，不在 `npm test` 内 |
| 部署脚本测试 `scripts/test/cloud-run-release.test.sh` | 全部通过（假 `gcloud`，不接触云端）；已在 CI 新增 `scripts-test` 作业 |
| 真实 API 进程冒烟 | 用本地生成的**假**服务账号密钥启动：`/health/live` 200、`/api/workspace/config` 200、无令牌/畸形令牌 401、无未处理异常。证明 DI 与中间件管线接线正确；**不证明** Firebase/Firestore/Vertex 的真实行为 |
| 变异验证 | 对 4 处关键逻辑做了"破坏后测试应失败"的检查（竞速取消、畸形令牌 catch、`--set-env-vars` 回归、回滚逻辑），均被测试捕获，已还原 |
| 未执行 | GitHub Actions（分支未触发 CI，见下）；Firestore/Firebase 模拟器测试（仓库没有，Phase 2）；真实 Cloud Run / Vertex 验证 |

**Actions 触发检查**：`deploy-cloud-run.yml` 仅在 push 到 `main`/`master` 或手动触发时运行；`ci.yml` 仅在 push 到 `main`/`master` 与 pull_request 时运行。推送 `phase0-hardening` 不会触发任何工作流，因此不会部署，**CI 也不会在该分支自动运行**（创建 PR 后才会）。⚠️ **合并到 `main` 会触发生产部署工作流**——请在合并前先完成下面的上线前验证。

## 5. 需要你在 Google Cloud / GitHub 中手动完成的操作
我没有执行任何一项。涉及真实费用、权限或不可逆变更，需你确认后自行执行：
1. **Spend cap**（Billing）：为 Vertex AI 与 Cloud Run 分别创建封顶预算；先读官方页面确认触顶行为（尤其 Cloud Run），见 `docs/cost-controls.md`。
2. **第三方供应商**：在各自后台设置月度/预付费上限，或不配置其 Key。
3. **访问策略**：决定是否启用白名单（个人使用建议启用）。设置仓库变量 `ACCESS_ALLOWED_EMAILS` / `ACCESS_ALLOWED_UIDS` / `ACCESS_REQUIRE_VERIFIED_EMAIL`；或在 Firebase 控制台关闭自助注册。
4. **Secret Manager**：按 `docs/secrets.md` 创建密钥、授权运行服务账号、迁移现有明文变量、设置 `API_SECRETS`。
5. **GitHub 变量/机密**：补齐 `GCP_PROJECT_ID`、`WIF_PROVIDER`、`GCP_DEPLOY_SERVICE_ACCOUNT`、`CLOUD_RUN_SERVICE_ACCOUNT`、`FIREBASE_AUTH_DOMAIN`、`FIREBASE_API_KEY`、`FIREBASE_APP_ID`（工作流此前因缺少这些而失败）。
6. **模型生命周期**：对照官方 model-versions 页面，为需要的模型在配置里填写 `Stage`/`RetireDate`。
7. **Cloud Run 服务超时**：如服务曾手工设置过 web=60s，需在下次发布时确认已变为 300s（脚本默认已改）。
8. 确认合并时间：合并 `main` = 触发生产部署。

## 6. 上线前仍需验证
- [ ] 用候选修订版（`--no-traffic`）做真实验证：普通聊天、流式、图片、PDF、思考档位、联网搜索（`auto/force/off`）、截断（把一个长回答的输出上限调小做演练）。
- [ ] 真实 Vertex 取消：发起长生成后点停止，观察 Vertex 侧用量/日志，确认终止时机在可接受范围内。
- [ ] 经 Cloud Run 前端断开连接的表现（浏览器是否收到错误）；缺少 `final` 的兜底已有，但需实测。
- [ ] 全站配额：Firestore 事务（两个文档）在真实 Firestore 下的并发行为；`usageQuotaGlobal` 的写入权限（运行服务账号需 `datastore.user`，已用于 `usageQuotas`，应已具备）。
- [ ] 访问策略：白名单内外账号各一，分别得到 200/403；未验证邮箱行为。
- [ ] 发布流程：在测试服务上完整演练"候选→切流→冒烟失败→自动回滚"。
- [ ] `docs/secrets.md` 的迁移步骤在测试服务上走一遍（含类型冲突报错的实际文案）。
- [ ] 旧会话兼容：`messages` 新增可选 `status` 字段，旧文档读取为 completed（有映射代码，**未对真实 Firestore 数据验证**）。

## 7. 行为变化（升级前请知晓）
- SSE `final` 事件新增 `status`；`update` 事件新增 `conversationId`；`GET /api/conversations/{id}` 的消息新增 `status`。旧前端缓存页面忽略这些字段仍可工作；已更新缓存版本号。
- 未知/已下线的 `modelName` 现在返回明确错误，而不是静默使用该供应商的第一个模型。
- 配额：Token 不再重复计数；PDF/图片的预估大幅下降；全站上限默认开启（500 请求/日、200 万 Token/日、150 搜索/日）——**请按实际用量调整**，否则可能比以前更早触顶。
- 本地运行 web 服务且没有 metadata 服务时，必须设置 `DISABLE_SERVERLESS_AUTH=true`，否则代理返回 502（以前会静默无认证转发）。
- 新增 Firestore 集合 `usageQuotaGlobal`（可选字段 `status` 加在 `messages`）；无迁移、无删除。
- 默认 `max-instances` 收紧为 api=3、web=2；如果线上承载更高并发，请设置仓库变量覆盖。
