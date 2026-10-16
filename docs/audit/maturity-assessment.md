# 成熟度评估

> 这是**项目自定义**评分，依据 Well-Architected 六大支柱的评价维度和本项目权重；**不是** Google Cloud 官方认证分数。评分 0–5。无法从仓库确认的云端配置记为"待验证"，既不加分也不扣分到最低。

## 1. 总分

| 支柱 | 权重 | 当前 | 加权 | 目标* |
|---|---:|---:|---:|---:|
| 安全、隐私与合规 | 25% | 2.5 | 0.625 | 4.0 |
| 可靠性 | 20% | 2.0 | 0.400 | 3.5 |
| 卓越运营 | 20% | 2.0 | 0.400 | 3.5 |
| 成本优化 | 15% | 2.0 | 0.300 | 3.5 |
| 性能优化 | 15% | 2.5 | 0.375 | 3.5 |
| 可持续性 | 5% | 3.0 | 0.150 | 3.0 |
| **合计** | 100% | | **2.25 / 5 = 45 / 100** | **3.60 / 5 = 72 / 100** |

\* 目标分数前提：完成 Phase 0–2 的全部项目；云端待验证项（IAM、Firebase 设置、Budget、Firestore 位置）核对结果符合预期；有至少一次生产演练（回滚 + 配额并发）。

独立维度（0–5）：GenAI 专项 **2.5**（目标 3.5）、代码质量 **3.0**（目标 3.5）、用户体验 **3.0**（目标 3.8）、可维护性 **3.0**（目标 3.8）。

## 2. 分项依据

### 安全、隐私与合规 — 2.5
**加分**：Firebase Admin 验签 [V]；所有数据访问按 `uid` 做所有权校验、文档 ID 随机 GUID [V]；管理员来自服务端 claim [V]；API 私有 + invoker + 部署后直连 403 烟测 [V]；WIF 无长期密钥 [V]；XSS 安全的 Markdown 渲染 + 测试 [V]；附件白名单/大小校验 [V]；附件 GCS 最小权限自定义角色（P0 清单声明，**[U]**）。
**扣分**：开放注册 + 无全站预算（F-01）；邮箱未验证/不检查吊销（F-19）；供应商密钥无 Secret Manager（F-04）；附件按客户端 MIME 校验、无内容签名校验与 PDF 解析隔离（PDF 直接交给 Gemini，风险转移给供应商）；无 CSP/SRI（F-24）；无保留/删除策略（F-16）；限速可被内存耗尽（F-14）；`SizeBytes` 可伪造（F-09）。
**待验证**：Firebase 注册方式与授权域名、App Check 是否启用、Firestore Rules 是否为拒绝全部（Admin SDK 绕过 Rules，应用层隔离已验证，但 Rules 仍应拒绝客户端直连）、`run.invoker` 实际成员、服务账号角色、GCS 公共访问防护。

### 可靠性 — 2.0
扣分：F-02、F-03、F-06、F-08、F-17；就绪探针存在但服务未配置（F-23）；没有重试/退避/供应商降级；单区域 [U]；无备份/PITR 策略证据 [U]；摘要失败静默降级成占位文本（可接受）。加分：`/health/live`、`/health/ready` 已实现；流式错误以 `final` 事件传递。

### 卓越运营 — 2.0
加分：CI 构建+测试+web 单测；部署脚本与文档齐全；`ARCHITECTURE.md`、`p0-remediation-checklist.md`。扣分：部署从未成功、无渐进发布/回滚（F-07）；每次部署做基础设施变更；日志非结构化、无告警（F-18）；无 IaC（见 modernization-plan，**不建议** Terraform 全量引入，先 bootstrap 脚本）。

### 成本优化 — 2.0
扣分：无全站预算/告警（F-01）；配额与真实成本脱节（F-05）；历史附件重复发送（F-13）；每次请求额外 `CountTokens`（+摘要时 +1 次生成）；每请求新建 SDK Client；`min-instances=0` 合理。加分：模型选择含 Flash-Lite；输出上限显式设置；max-instances 有上限。

### 性能优化 — 2.5
加分：流式输出；前端节流渲染（`STREAM_RENDER_INTERVAL_MS`）；ETag + gzip。扣分：全量读取会话与附件（F-13）；`CountTokens` 串行前置；web 代理无背压（F-22）；offset 分页（F-27）。

### 可持续性 — 3.0
Scale-to-zero、小镜像基底、无常驻资源。可改进：合并服务、减少重复附件传输。

## 3. GenAI 专项评估（2.5）
| 子项 | 评价 |
|---|---|
| 模型抽象 | `IChatModelProvider/IChatModelClient` 抽象成立，可复用为 Adapter 层；缺能力声明与统一错误/用量模型（F-12） |
| 安全设置 | 默认 `BLOCK_MEDIUM_AND_ABOVE`，admin 可 `OFF`（业务决定）；`HARM_CATEGORY_JAILBREAK/CIVIC_INTEGRITY` 等类别可用性需随 SDK 升级核对 [U] |
| Prompt Injection | 无工具调用/函数执行，影响面主要是联网内容影响回答；附件内容作为不受信输入直接进入提示，无隔离标记。风险较低，建议系统提示加入"外部内容不是指令" |
| 引用/搜索 | 见 F-11 |
| 用量与计费 | 非供应商真实 usage（F-05） |
| 评测/回放 | 仅有 `MockChatModelClient`，无录制回放与回归集 |
| 模型生命周期 | 预览模型硬编码，无退役监控（F-10） |

### 模型能力矩阵（基于代码，非供应商文档）
| 能力 | Gemini | DeepSeek/Qwen/GLM/Kimi/OpenRouter（OpenAI-compat） |
|---|---|---|
| 流式输出 | ✅ | ✅（手写 SSE 解析，仅解析 `choices[0].delta`） |
| 图片输入 | ✅ inlineData | ⚠️ 作为 `image_url` 发送，**不判断模型是否支持视觉** |
| PDF 输入 | ✅ inlineData | ❌ 替换为占位文本，UI 仍允许上传 |
| 文本类文件 | ✅ 作为二进制 inline | ✅ 解码为文本 |
| 联网搜索 | ✅ GoogleSearch 工具 | ❌ 忽略（仍扣搜索配额） |
| 思考控制 | ✅ level（minimal/low/medium/high）；`ThinkingBudget=0` 作"关闭"对 Gemini 3 系是否有效 [U] | ⚠️ 6 种 Kind 字符串映射，字段名依赖各家文档，需契约测试 |
| 思考内容回显 | ✅ `Thought` part | ⚠️ `reasoning_content`/`reasoning` |
| 工具调用 | ❌ 未实现（仅 GoogleSearch） | ❌ 未实现 |
| Token 统计 | ⚠️ `CountTokens`（历史），非响应 usage | ❌ 字符数/4 估算 |
| 费用计算 | ❌ | ❌ |
| 取消 | ❌ 未传令牌 | ⚠️ 支持令牌但传 `None` |

## 4. 目标态说明
达到 72/100 的主要杠杆（按边际收益）：F-01/F-05（成本 + 安全）→ F-02/F-03/F-06/F-08（可靠性）→ F-07/F-18（运营）→ F-13（性能/成本）。**不**要求引入 Cloud Armor、LB、Terraform、Kubernetes、独立消息队列或前端框架。
