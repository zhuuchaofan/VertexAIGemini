# 费用控制

目标：把"开放注册 + 按量计费的模型"这一组合的最坏情况限制在可接受范围内。费用保护分三层，**越靠上越可靠**：

| 层 | 机制 | 谁强制 | 说明 |
|---|---|---|---|
| 1 | 应用层限额：单用户日配额、**全站日上限**、并发上限、访问白名单 | 后端（ASP.NET） | 立即生效，无计费延迟，覆盖**所有**模型供应商 |
| 2 | Cloud Run `max-instances` / 并发 | Cloud Run | 限制最大并行度，从而限制峰值速率 |
| 3 | Google Cloud 费用封顶 / 预算告警 | Google Cloud Billing | 兜底；有计费延迟，只覆盖 Google Cloud 服务 |

前端只负责展示，任何限额都以后端为准。

## 1. 应用层限额（已实现，见 `Quota` / `Access` 配置）

| 配置项（环境变量写法 `Quota__X`） | 默认 | 含义 |
|---|---|---|
| `DailyRequestLimit` / `DailyTokenLimit` / `DailySearchLimit` / `DailyAttachmentBytesLimit` | 100 / 200000 / 30 / 25MB | 单用户每日（UTC）上限 |
| `GlobalDailyRequestLimit` / `GlobalDailyTokenLimit` / `GlobalDailySearchLimit` | 500 / 2,000,000 / 150 | **全站**每日上限，0 表示不限。所有用户、所有实例共享，在同一个 Firestore 事务里与用户配额一起检查 |
| `MaxConcurrentChatsPerUser` / `MaxConcurrentChatsPerInstance` | 2 / 12 | 同时进行的流式对话数（**仅单实例内**有效，跨实例由日限额与 max-instances 约束） |
| `Access__RequireVerifiedEmail` | false | 仅接受已验证邮箱 |
| `Access__AllowedEmailsCsv` / `AllowedEmailDomainsCsv` / `AllowedUidsCsv` | 空 | 访问白名单，逗号分隔。**非空即启用**，未命中返回 403 |

要点与限制：
- 全站计数存放在新集合 `usageQuotaGlobal`（文档 ID=日期），不影响现有 `usageQuotas` 及管理员报表。
- 全站文档是热点文档：Firestore 对单文档的持续写入约每秒 1 次；个人/小规模用量远低于此，事务冲突时会自动重试。若将来并发明显升高，需要改为分片计数。
- 令牌结算：预留时记入"估算"，请求结束后**释放估算并记入实际**，同一请求不会被计两次；请求在到达模型前失败，则不占用令牌。实际值目前来自模型客户端的 `CurrentTokenCount`（Gemini 为 CountTokens，OpenAI 兼容供应商为字符数/4 估算），**不是供应商返回的 usage**——精确结算留在 Phase 1。
- 搜索和附件的计数在预留时一次性记入，失败不退还（避免被用来绕过限额）。
- 白名单匹配邮箱时**必须邮箱已验证**（Firebase 允许任何人用未验证的任意邮箱注册）。当前前端没有"发送验证邮件"的流程，所以对邮箱/密码用户，建议用 `AllowedUidsCsv`，或让白名单用户使用 Google 登录。
- 开放注册的风险：`RequireVerifiedEmail=false` 且白名单为空时，任何人可注册并使用单用户额度；全站上限保证总量有界，但单个恶意用户可以在一天内用完全站额度（拒绝服务）。个人使用建议配置白名单。

调参建议：先用管理员接口 `GET /api/admin/quotas/daily` 观察几天实际用量，再把全站上限设为"你能接受的一天最大花费 ÷ 单次请求平均成本"。

## 2. Cloud Run 限制（仓库默认值已收紧，见部署脚本）
- `API_MAX_INSTANCES` 3、`WEB_MAX_INSTANCES` 2（原 10/5），`API_CONCURRENCY` 20。峰值并发 ≈ 3×20=60。
- 个人用量可进一步降到 1–2 个实例。

## 3. Google Cloud 费用封顶（Spend cap budgets）— **需要你在控制台手动完成**

> 说明：Google Cloud 于 2026-10-13 宣布 Spend cap budgets 对部分服务正式可用，支持 Vertex AI 和 Cloud Run（来自你提供的信息）。我在当前环境**无法访问 docs.cloud.google.com**，没有读到官方页面原文，所以下面的步骤只给方向和决策点；具体菜单位置、字段名、支持的服务清单、触发后的行为，请以官方文档为准：
> https://docs.cloud.google.com/billing/docs/how-to/budgets-spend-caps

必须知道的事实（来自公告摘要，仍需对照官方页核实）：
- 费用封顶与普通"预算告警"不同：告警只通知，封顶会限制服务继续产生费用。
- 计费数据有延迟，封顶仍**可能超额**；它是兜底，不是实时闸门——所以第 1 层必须保留。
- **只覆盖 Google Cloud 服务。** 经 OpenAI 兼容接口调用的 DeepSeek、Kimi、Qwen、智谱、OpenRouter 等第三方模型费用**不在其中**，必须在各供应商后台设置额度/充值上限；不使用的供应商不要配置 API Key（没有 Key 的供应商不会被启用）。

建议配置（分别对应两项服务）：

| 对象 | 建议 | 决策点 |
|---|---|---|
| Vertex AI（Gemini） | 为该项目创建一个**仅针对 Vertex AI** 的封顶预算。金额 = 你愿意承担的月上限；同时开 50%/90%/100% 通知 | 触顶后 Gemini 调用会失败，应用会显示"服务暂时不可用/模型不可用"——请确认你接受这种降级 |
| Cloud Run（web + api） | 单独一个预算（或同一预算内按服务拆分）。金额通常远小于 Vertex AI | **触顶对 Cloud Run 意味着什么**（是否整站停止服务）请在官方页确认，再决定数额，避免因小额封顶让站点意外下线 |
| 其他服务（Firestore、Cloud Storage、Secret Manager、Artifact Registry） | 先用**普通预算告警**，不要封顶 | 封顶可能影响数据读写 |

操作清单（你来做；我不会修改任何计费配置）：
1. 在 Billing 账号下，按官方页面创建 Spend cap budget，范围限定到本项目与上面的服务。
2. 把通知发给你本人邮箱；确认测试通知能收到。
3. 在 Vertex AI 与 Cloud Run 预算上分别设置金额，记录在你的运维笔记里。
4. 在各第三方供应商后台设置月度限额或预付费上限。
5. 部署后，用只读命令核对：`gcloud billing budgets list --billing-account=<ID>`（输出里应能看到预算与阈值）。

## 4. 上线前自查
- [ ] 已决定是否使用访问白名单（个人使用建议启用）
- [ ] `GlobalDaily*` 已按承受能力设置
- [ ] Vertex AI / Cloud Run 费用封顶已创建并收到测试通知
- [ ] 第三方供应商已设上限或未启用
- [ ] 连续两天观察 `/api/admin/quotas/daily`，用量与预期相符
