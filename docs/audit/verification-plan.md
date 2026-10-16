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
