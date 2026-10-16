# 现代化改造计划

原则：安全、简单、低成本、可回滚；每个阶段可独立发布。本计划**不包含**对业务代码的改动（本轮仅审查）。工作量为 1 人估算。

## 架构决策摘要
| 决策 | 推荐 | 不推荐及原因 |
|---|---|---|
| 前后端部署 | Phase 1 保留并加固；**Phase 2 合并为单个 Cloud Run 服务** | Firebase Hosting rewrite（SSE 时长风险 [U]）；LB+Cloud Armor（月固定成本与复杂度超出规模） |
| 数据库 | **保留 Firestore**，修读取模式（分页、附件分离） | 无成本实测前不换库（审查要求）。估算见下 |
| 模型层 | 复用现有 Provider/Client 抽象，加 Capabilities 与统一 usage | 另建通用 Adapter 框架/引入 LiteLLM 等网关：多一个服务要运维 |
| 前端 | 保持原生 JS 模块化 | 迁移 React/Vue/Svelte：现有痛点是行为缺失而非框架缺失 |
| IaC | 先 `scripts/bootstrap`（幂等 gcloud） | 全量 Terraform：单项目单环境收益低；若出现第二环境再引入 |

## Phase 0 — 紧急风险（约 3–4 天，上线前必做）
| 项 | 范围 | 验收 | 回滚 |
|---|---|---|---|
| 预算与熔断 F-01 | Billing Budget+告警；全站日预算计数；关闭自助注册或白名单；要求邮箱已验证 F-19 | 超预算返回明确错误；告警邮件到达 | 配置开关 `Quota:GlobalDailyBudget=0` 即关闭 |
| 超时对齐 F-02 | web 超时=300s；SSE 15s 心跳；前端检测缺失 `final` | 120s 流完整；断开提示 | 恢复旧值（环境变量） |
| 取消传播 F-03 | RequestAborted 全链路；代理 AbortController；停止按钮 | 断开后上游调用终止（集成测试） | 回退提交；无数据迁移 |
| 密钥/部署 F-04 | Secret Manager + `--update-env-vars/--set-secrets` | 连续两次部署后供应商仍可用 | 旧修订版 |
| 截断检测 F-06 | 处理 `MAX_TOKENS`/`finish_reason` | UI 显示"被截断" | 功能开关 |
前置条件：确认 GitHub 变量/机密已配置（用户侧）；Firebase 注册策略决定。

## Phase 1 — 稳定上线（约 1 周）
- F-07：`--no-traffic` + 候选标签烟测 + 切流；bootstrap 脚本抽离基础设施；Artifact Registry 清理。验收：坏镜像不切流；一键回滚命令写入 runbook。
- F-05：配额改为"次数+真实 usage"；UTC→可配置时区；失败按已消耗结算；并发测试。验收：50 并发不超限；单元+模拟器测试。
- F-08/F-17：消息状态字段、失败保留部分内容、`clientRequestId` 幂等。
- F-09/F-25：入口剥离 `SizeBytes/StorageObjectName`；F-15：`MaxRequestBodySize`、总附件上限；F-14：限速修复。
- F-23：非 root、探针（`/health/live` startup、`/health/ready` liveness 视需要）、`--cpu-boost`。
- F-18：JSON 日志 + 日志指标 + 5xx/延迟告警。
回滚：每项独立提交；Cloud Run 修订回退；Firestore 新字段向后兼容（读取时默认值）。

## Phase 2 — 可维护性与成本优化（2–3 周）
- 合并为单服务（删除 `apps/web/server.mjs`、invoker 绑定、metadata token 逻辑）：`UseStaticFiles` + ETag；回归 SSE；保留旧 web 服务一个发布周期以便回滚。
- F-13：会话分页、附件元数据+受控下载、历史重放仅保留最近附件。预期显著降低 Firestore 读取与 GCS 出站。
- F-10：升级 Google.GenAI 至 1.x（先在分支跑 mock+真实烟测）；默认模型切换为 GA；模型目录加 `retireAfter` 与每周 CI 存活检查。
- F-11/F-12：groundingSupports 行内引用、搜索建议渲染、能力矩阵下发并在前端禁用不支持项。
- F-16：BulkWriter 删除、GCS Lifecycle、`usageQuotas` TTL、账号删除脚本。
- F-20：Firebase Auth Emulator + Firestore Emulator 集成测试；录制回放测试；Playwright 冒烟。
- 摘要持久化（写入 `historySummary`）以减少重复摘要费用。

## Phase 3 — 长期增强（按需）
Cloud Armor/App Check（出现真实滥用时再上；App Check 的 reCAPTCHA Enterprise 有免费额度，可优先评估）；多供应商故障转移；按美元核算的计费；对话全文检索（Firestore 无原生全文，可用外部检索，需成本数据）；第二环境与 Terraform；CSP（F-24）；Firestore 备份/PITR 与演练。

## 成本估算（明确假设，仅供量级判断）
假设：10 个活跃用户，每人每天 30 次请求，平均每次输入 6k token（含历史）、输出 1k token，使用 Flash 级模型；Cloud Run scale-to-zero。
- Firestore：每次请求约 1 次会话读 + 40 条历史读 + 3–4 次写 ≈ 45 reads / 5 writes；每天 300 次 ≈ 13.5k reads、1.5k writes，处于免费额度量级内（免费额度以官方定价页为准 [U]）。F-13 修复前打开会话时的全量读取是主要放大项。
- Cloud Run：两个服务，请求少，大概率落在免费额度内；流式长连接按实例活跃时间计费，取消传播（F-03）直接影响账单。
- 模型：成本由 token 主导，**应以官方 Vertex AI 定价页当日单价乘以 usage 计算**；本报告不引用具体单价。
- 固定成本：Artifact Registry 存储（需清理策略）、Secret Manager 少量。
不确定性：模型单价、搜索 grounding 的计费规则、Firestore 所在区域与跨区出站、实际使用模式。
