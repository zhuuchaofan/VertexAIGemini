# 验证计划

所有命令均**只读**（`describe/list/get`），除标注"本地"外需要对应 GCP 权限。把 `$PROJECT`、`$REGION`、`$API`、`$WEB` 换成实际值。不得把输出中的密钥写入文档。

## A. 本地构建与测试（本轮因缺少 dotnet SDK 未能执行）
```bash
dotnet restore VertexAI/VertexAI.csproj && dotnet build VertexAI/VertexAI.csproj --no-restore
dotnet restore VertexAI.Tests/VertexAI.Tests.csproj && dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj
dotnet list VertexAI/VertexAI.csproj package --vulnerable --include-transitive
dotnet list VertexAI/VertexAI.csproj package --outdated
(cd apps/web && npm test)        # 本轮已通过：10 项
```

## B. 云端配置核对（把"待验证"变成"已验证"）
| 目的 | 命令 | 期望 |
|---|---|---|
| API 不允许匿名 | `gcloud run services get-iam-policy $API --region=$REGION --project=$PROJECT` | 无 `allUsers`/`allAuthenticatedUsers`；invoker 仅 web SA |
| API ingress | `gcloud run services describe $API --region=$REGION --format='value(metadata.annotations["run.googleapis.com/ingress"])'` | 与设计一致（`all` 亦可，因有 IAM） |
| 超时/并发/实例 | `gcloud run services describe $API ... --format='yaml(spec.template.spec.timeoutSeconds,spec.template.spec.containerConcurrency,spec.template.metadata.annotations)'`，对 `$WEB` 同样执行 | web 超时 ≥ API 超时（F-02） |
| 环境变量/Secret | `gcloud run services describe $API --format='yaml(spec.template.spec.containers[0].env)'` | 无明文供应商密钥；使用 `secretKeyRef` |
| 运行 SA 角色 | `gcloud projects get-iam-policy $PROJECT --flatten=bindings --filter='bindings.members:serviceAccount:<runtime-sa>' --format='table(bindings.role)'` | 仅 aiplatform.user、datastore.user、自定义存储角色、secretAccessor |
| 无 SA 密钥 | `gcloud iam service-accounts keys list --iam-account=<sa> --managed-by=user` | 空 |
| 桶安全 | `gcloud storage buckets describe gs://$BUCKET --format='yaml(iamConfiguration,lifecycle_config,soft_delete_policy,location)'` | UBLA 开启、public access prevention=enforced、有 lifecycle |
| Firestore 位置/规则/索引 | `gcloud firestore databases describe --project=$PROJECT`；`gcloud firestore indexes composite list --project=$PROJECT` | 位置与 Cloud Run 区域相近；3 个索引 READY |
| 备份 | `gcloud firestore backups schedules list --database='(default)' --project=$PROJECT` | 有计划，或明确接受无备份 |
| 预算 | `gcloud billing budgets list --billing-account=<id>` | 存在带告警阈值的预算 |
| Firebase 注册/域名 | Firebase 控制台 → Authentication → Settings（无只读 gcloud 等价命令） | 授权域名仅含 web 域；注册策略符合 F-01 决定 |
| 修订/流量 | `gcloud run revisions list --service=$API --region=$REGION`；`gcloud run services describe $API --format='value(status.traffic)'` | 可见回滚目标修订 |
| 模型存活 | 官方 model-versions 页 + Vertex release notes；对每个配置模型做 1 次最小 `generateContent` | 无 404/退役提示 |

## C. 回归测试体系（最小但实用）
| 域 | 测试 | 工具 |
|---|---|---|
| 认证授权 | ①无/过期/伪造 token → 401；②用户 A 的 token 访问用户 B 的会话 GET/PATCH/DELETE/导出 → 404 且数据未变；③非 admin 访问 admin → 403；④`chat/stream` 带他人 `conversationId` → 不写入、不泄露 | Firebase Auth Emulator + Firestore Emulator + `WebApplicationFactory` |
| 流式 | 正常结束有 `final`；中途断开 → 上游被取消、消息 `cancelled`；上游 500 → `final.succeeded=false`；`MAX_TOKENS` → `truncated`；超 60s 流不被截断（本地代理+假上游） | 假 `IChatModelClient` + node:test |
| 附件隔离 | 他人对象名/伪造 `StorageObjectName`/`SizeBytes` 被忽略；删除会话清理对象；超限/非法 MIME 拒绝；Base64 炸弹/超大体 413 | Emulator + 假 `IChatAttachmentStore` |
| 配额并发 | 50 并发请求总和 ≤ 限额；失败退还/结算；日界线边界；全站预算 | Firestore Emulator，`Task.WhenAll` |
| 模型适配 | 每家供应商 `Thinking.Kind` → 请求体快照（契约/黄金文件）；SSE 回放解析 | 录制回放（JSON fixtures） |
| 搜索引用 | 最新信息题→有 grounding 且引用映射到句子；关闭搜索→无引用；搜索失败→降级提示；不支持模型→UI 禁用 | 回放 fixtures + 真实烟测（定时、少量） |
| 前端 | 取消按钮、缺失 `final` 提示、401 重新登录、长列表分页 | node:test + Playwright（`/opt/pw-browsers/chromium`） |

CI 建议：PR 跑 A 与 C（模拟器）；`main` 合并后跑部署候选烟测；每周定时作业跑"模型存活 + 依赖漏洞 + SDK 版本"检查。

## D. 上线门槛（Go/No-Go）
- [ ] Phase 0 全部完成并有测试
- [ ] 表 B 全部通过或有书面豁免
- [ ] 一次完整"部署→候选烟测→切流→回滚"演练
- [ ] Budget 告警已收到测试邮件


## E. Phase 0 新增检查

### E1. 费用与访问（只读核对）
| 目的 | 命令 / 做法 | 期望 |
|---|---|---|
| 费用封顶存在 | 按 `docs/cost-controls.md` 在 Billing 控制台核对；`gcloud billing budgets list --billing-account=<ID>` | Vertex AI 与 Cloud Run 各有预算；告警邮箱可达 |
| 实例上限 | `gcloud run services describe $API --region=$REGION --format='value(spec.template.metadata.annotations["autoscaling.knative.dev/maxScale"])'`（web 同理） | api≤3、web≤2（或你的覆盖值） |
| 全站计数在写入 | 发送若干请求后，Firestore 控制台查看集合 `usageQuotaGlobal`（文档 ID=当天 yyyyMMdd） | `requests` 递增，`estimatedTokens` 在请求结束后回落，`actualTokens` 递增 |
| 访问策略 | 白名单内外各用一个账号请求 `GET /api/auth/status` | 内：200；外：403 |
| 并发上限 | 同一账号同时发起 3 个长对话 | 第 3 个收到"当前有多个对话正在生成" |

### E2. 流式（候选修订版上人工验证）
| 场景 | 期望 |
|---|---|
| 生成 >60s（高思考档位） | 完整结束，无截断；SSE 中可见 `: ping` |
| 生成中点击"停止" | UI 显示"已停止生成"；刷新后该回复仍在（状态 cancelled）；Cloud Run 请求日志中该请求提前结束；Vertex 用量在可接受时间内停止增长 |
| 生成中关闭标签页 | 服务端日志出现 "caller cancelled"；已生成内容被保存 |
| 把输出上限调小做演练 | 提示"回答被截断"，状态 truncated |
| 暂停上游/断网 | UI 显示"连接中断"，不显示成功 |
| 超过 240s | 状态 failed，提示"生成时间过长"，保留已生成内容 |

### E3. 模型冒烟（逐模型，候选修订版）
对每个配置的 Gemini 模型各做一次：普通问答、流式、一张小图片、一个小 PDF、思考档位切换（含 `minimal/low/medium/high` 与 Pro 不可关闭）、联网搜索 `auto/off/force` 并检查引用。记录不兼容项；系统应给出显式错误（例如未知模型）或显式提示，不应静默降级。OpenAI 兼容供应商同理（图片支持因模型而异，PDF 目前被替换为占位文本——属 F-12，未解决）。

### E4. 发布流程演练（测试服务）
1. 部署一个会让冒烟失败的镜像，确认：新修订版带 `candidate` 标签、旧修订版仍承载流量、冒烟失败后流量保持/回到旧修订版、命令以非零退出。
2. 部署一个正常镜像，确认两个服务均切到新修订版，`candidate` 标签被移除。
3. 修改服务上的一个手工环境变量后再次部署，确认该变量仍在（合并式配置）。

### E5. 本地可重复的检查（本轮已执行）
```bash
dotnet build VertexAI.slnx && dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj --no-build
(cd apps/web && npm test)
bash scripts/test/cloud-run-release.test.sh
PW_ROOT=$(npm root -g) node apps/web/test-e2e/stream.e2e.mjs     # 需要 Playwright 与 Chromium
```
