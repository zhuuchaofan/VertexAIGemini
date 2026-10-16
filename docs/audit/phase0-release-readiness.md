# Phase 0 上线前验收报告（Release Readiness Review）

日期：2026-10-16（第二轮复审后更新）　分支：`phase0-hardening` → `main`　PR：[#1（草稿，保持 Draft）](https://github.com/zhuuchaofan/VertexAIGemini/pull/1)

> **第二轮复审更新**：GitHub 上的实际变更复查发现了本报告第一版没发现的问题（误提交的模拟器日志、`crr_promote` 错误传播缺陷、切流前修改 IAM、令牌缺失时跳过候选检查）。证据、修复与测试见第 13 节；第 1、2、8 节已按新事实重写。

## 结论：**CONDITIONAL（有条件）**

代码层面：没有已知的未修复阻塞缺陷；两轮验收共发现并修复 12 个明确缺陷（第 9、13 节），均有回归测试。**第一版报告对"无大文件、无无关改动"和"候选流程不影响线上"的结论不准确，已更正。**
**但不能直接上线生产**，因为下列事项在本环境**无法验证**或**需要你做决定/配置**。在它们完成之前，不应合并到 `main`（合并即触发生产部署工作流）。

### 阻止生产部署的具体问题
| # | 问题 | 性质 | 解除条件 |
|---|---|---|---|
| B1 | **真实 Vertex AI / Cloud Run 行为一概未验证**：模型调用、图片/PDF、思考档位、联网搜索、取消后上游是否停止、经 GFE 断线的表现、旧会话读取 | 无法在本环境验证 | 在候选修订版上按第 8 节用例逐项通过 |
| B2 | **访问策略默认是开放的**：`ACCESS_*` 全空时任何人可注册并使用；仅靠每用户/全站日限额兜底 | 需要你决定并配置 | 设置白名单或关闭自助注册，并验证 200/403 |
| B3 | **没有任何 Google Cloud 费用封顶/预算**（仓库里没有，也无法读取云端） | 需要你在控制台配置 | 按第 6 节最小方案配置并确认通知可达 |
| B4 | **GitHub 部署变量/机密缺失**（工作流此前因此失败）。~~合并 `main` 会自动部署~~ **已解决：部署工作流现在只支持手动触发**；另需配置 `CRR_IDENTITY_TOKEN_SA`（见 B7） | 需要你配置 | 补齐变量 |
| B5 | **`gemini-3-flash-preview` 可能已被弃用**（Gemini API 侧已公告弃用并指向后继；Vertex 侧状态未能核实）；`MaxOutputTokens=8192` 在高思考档位下可能频繁触发"回答被截断" | 需要真实调用确认 | 真实调用确认；必要时下架该模型或调大输出上限 |
| B6 | **Secret Manager 迁移未执行**；现有明文供应商 Key（若有）与新的 `--update-secrets` 可能冲突 | 需要你操作 | 按 `docs/secrets.md` 在测试服务上走一遍 |
| B7 | **私有 API 候选无法被验证，除非配置令牌签发**：现在取不到身份令牌时发布会**停止**。需要一个探测用服务账号 + `serviceAccountTokenCreator` + 仓库变量 `CRR_IDENTITY_TOKEN_SA`（或显式接受风险） | 需要你操作（IAM） | 按 `docs/cloud-run.md` 配置并在测试服务演练 |

满足 B1–B6 后可升级为 **READY**。

---

## 1. 变更范围审查（phase0-hardening 与 main 的全部差异）
| 项 | 结果 |
|---|---|
| 范围 | `main..phase0-hardening`：约 70 个文件，无删除、无重命名。**更正**：第一版写"无二进制/大文件"是错的——提交 `88c0eb0` 误带入了 `VertexAI.Tests/firestore-debug.log`（25,819 行，约 2.5 MB，Firestore 模拟器调试日志）。现已从跟踪中移除（见第 13.1 节）。该文件仍存在于 PR 的历史提交 `88c0eb0` 中，PR 合并结果（树）不再包含它 |
| 目录分布 | `VertexAI/`（后端）、`VertexAI.Tests/`、`apps/web/`（代理+前端+测试）、`scripts/`、`docs/`、`.github/workflows/` |
| 无关改动 | 除上述日志外未发现（`.idea`、依赖版本、Dockerfile 均未改动；`Google.GenAI` 仍为 0.14.0）。已新增 `scripts/test/repo-hygiene.sh` 在 CI 中自动拦截 |
| 数据结构 | 仅新增：集合 `usageQuotaGlobal`；`messages.status` 可选字段。无迁移、无删除、无新增复合索引（`status` 过滤在内存中完成） |
| 部署副作用 | 部署脚本与工作流被重写为共用 `scripts/lib/cloud-run-release.sh`；**行为变化**：配置合并而非替换、先 0% 流量候选再切流、冒烟失败自动回滚、前置校验（超时分层、密钥格式、认证绕过开关）。每次部署仍会重置 `--cpu/--memory/--concurrency/--timeout/--min/max-instances/--service-account/--port` 为脚本值（既有行为，手工改过的这些值会被覆盖） |
| 默认值变化 | web 超时 60→330s、API 300s 不变、`max-instances` api 10→3 / web 5→2、全站日上限默认开启（500 请求/2M Token/150 搜索） |
| 行为变化 | 未知模型返回错误（不再静默回退）；`final`/`update`/会话消息新增字段；本地跑 web 需 `DISABLE_SERVERLESS_AUTH=true`，且该开关在 Cloud Run 上被拒绝 |

## 2. GitHub Actions 触发条件与权限
| 检查 | 结果 |
|---|---|
| `deploy-cloud-run.yml` 触发 | **现在仅 `workflow_dispatch`（手动）**。原先还有 `push` 到 `main`/`master`；已删除，未增加 GitHub Environment，未改动 WIF/OIDC 配置。PR 与普通推送从来不会运行它 ✅ |
| `ci.yml` 触发 | `push` 到 `main`/`master` 与 `pull_request`；已将权限收紧为 `contents: read`，无任何云端凭据 ✅ |
| 实际观察 | 创建 PR 后，该分支上**只出现了 `CI` 工作流**（`pull_request` 事件），没有 `Deploy Cloud Run` ✅ |
| CI 中的脚本测试 | 使用假 `gcloud`（PATH 覆盖），不接触云端 ✅ |
| 权限风险 | `deploy-cloud-run.yml` 具有 `id-token: write`（WIF 所需）。部署 SA 的实际权限无法读取（B 类待验证）。工作流不使用 `environment:`，因此没有人工审批闸门 |
| 本轮已做 | 首次上线期间部署仅手动触发。恢复自动部署的方法写在工作流顶部注释里。`repo-hygiene.sh` 在 CI 中断言该工作流没有 `push/pull_request/schedule` 触发、没有任何工作流执行 `git push/commit`、`ci.yml` 只读且不含云端认证 |
| 仍建议（未做） | 给仓库配置分支保护/部署审批。注意为 job 添加 `environment:` 会改变 OIDC subject，可能让现有 WIF 条件失效，需要连同云端 WIF 条件一起评估 |

### CI 结果
PR #1（草稿）创建后 CI 自动运行。对包含全部代码改动的提交 `88c0eb0`（[run #28](https://github.com/zhuuchaofan/VertexAIGemini/actions/runs/38872000799)）：

| 作业 | 结果 |
|---|---|
| build-test（.NET 构建 + 75 项服务测试） | ✅ 成功 |
| web-test（Node 单测 34 项） | ✅ 成功 |
| scripts-test（发布脚本测试，假 gcloud） | ✅ 成功 |
| firestore-emulator-test（Firestore 模拟器，87 项） | ✅ 成功（约 2 分钟） |

**第二轮复审后的 head `25aa8ea`（[run #31](https://github.com/zhuuchaofan/VertexAIGemini/actions/runs/38875919679)）**：build-test、web-test、scripts-test（含发布脚本测试与仓库卫生检查）、firestore-emulator-test 四个作业**全部成功**。没有需要修复的 CI 失败。注意：CI 不运行浏览器端到端脚本，也不接触任何云端。PR 仍为 Draft；部署工作流本身没有出现在 PR 的任何运行里。

## 3. 模型生命周期、区域与 Standard PayGo
官方页面（`docs.cloud.google.com`）在本环境无法直接打开；以下来自对官方页面的**搜索摘录**，凡标"未核实"的需你对照官方页面。

| 模型 | 状态与生命周期 | 区域 | 与代码配置的一致性 |
|---|---|---|---|
| `gemini-3.5-flash`（默认） | GA，发布 2026-05-19，**退役日期 ≥ 2027-05-19** | 模型页列出 **global** 端点；Standard PayGo 含 global、多区域 us/eu 及若干单区域 | ✅ 与 `Location=global` 兼容；思考档位 MINIMAL/LOW/MEDIUM/HIGH，默认 MEDIUM，与配置一致 |
| `gemini-3.1-flash-lite` | GA，发布 2026-05-07，退役 ≥ 2027-05-07 | global、us/eu 多区域等 | ✅ 四档，默认 MINIMAL，一致 |
| `gemini-3.1-pro-preview` | 预览；Gemini API 页无关停日期；Firebase 文档称预览模型仅在 global | global | ✅ LOW/MEDIUM/HIGH，默认 HIGH，一致；预览无 SLA |
| `gemini-3-flash-preview` | 预览；**Gemini API 侧已公告弃用并指向 `gemini-3.6-flash`**（Vertex 侧状态**未核实**） | 未核实 | ⚠️ 风险（B5） |
| `gemini-2.5-*` | 2026-10-20 退役 | — | ✅ 项目不使用 |

补充：
- Gemini API 页显示 `gemini-3.5-flash` 已弃用并路由到 `gemini-3.6-flash`；这是 Gemini API（非 Vertex）的公告。Vertex 页给的是 GA 且 ≥2027-05-19。**两套生命周期不同，后继模型存在**，建议关注而不是现在迁移。
- **实际调用位置**：代码使用 `VertexAI:Location`，默认 `global`（`appsettings.json`），部署脚本**不覆盖**它，所以线上除非手工设置了 `VertexAI__Location`，就是 global。SDK 0.14.0 对 `global` 的主机映射已**本地验证**为 `https://aiplatform.googleapis.com`（`us-central1` → `us-central1-aiplatform.googleapis.com`，`eu` → `eu-aiplatform.googleapis.com`）。
- ⚠️ **数据驻留**：global 端点无法控制或知道处理请求的区域。若有区域合规要求，应改用多区域/单区域端点（如 `eu`），并确认所选模型在该位置可用。
- **不能仅因模型 ID 存在就认为线上成功**：还取决于项目是否启用了 Vertex AI API、模型访问/配额、组织策略、服务账号 `roles/aiplatform.user`。这些**无法验证**，已列入第 8 节用例 A1。
- 已修复：Gemini 3.x 上"关闭思考"此前发送 `thinkingBudget=0`（3.x 不推荐、3.1 Pro 不可关闭）；现在改为发送最低档位（`MINIMAL`，Pro 为 `LOW`），并有测试。UI 本来就不提供 "off"，只有手工构造的请求会触发。

## 4. 费用保护的真实生效条件
| 检查 | 结论 | 证据 |
|---|---|---|
| 白名单默认行为 | **默认开放**：`Access` 列表全空即不限制（B2） | `AccessPolicy` 单测；`appsettings.json` |
| `ACCESS_ALLOWED_EMAILS/UIDS/DOMAINS` 是否传到运行环境 | ✅ 仅当变量被设置时，发布库把它们作为 `Access__AllowedEmailsCsv` 等传入 `--update-env-vars`（`#` 分隔符，值含逗号安全）；.NET 环境变量绑定后生效 | 假 gcloud 测试断言命令行；C# 测试用同名环境变量绑定并验证 200/403；真实 API 进程启动冒烟 |
| 是否由后端执行 | ✅ `AccessPolicyMiddleware` 覆盖 `/api/*`（不区分大小写，`/api/workspace/config` 除外）；前端不参与 | 6 条路由 + 大小写测试 |
| 注意：合并语义 | 变量**取消设置**不会移除服务上已有的值（`--update-env-vars` 不删除）；要放宽/撤销策略需手工 `--remove-env-vars` | 设计所致；已在发布日志里警告"未设置任何访问策略" |
| 白名单与未验证邮箱 | 白名单匹配邮箱时**必须邮箱已验证**，避免他人注册他人邮箱绕过 | 单测。前端无"发送验证邮件"流程，邮箱/密码用户请用 UID 白名单或 Google 登录 |
| 全站限额事务原子性 | ✅ **在 Firestore 模拟器中实测**：40 个并发用户、上限 5 → 恰好放行 5，计数器 = 5；被拒绝者不改动自己的用户计数（跨两个文档原子） | `EmulatorTests` |
| 并发请求处理 | 同时到达 ≤10 个全部正常；40 个同时到达时放行数仍严格等于上限，冲突耗尽的请求 **fail-closed** 并返回"当前请求较多，请稍后重试"（原为笼统错误）。⚠️ 模拟器延迟不代表生产；真实 Firestore 的冲突表现**未测** | 同上 |
| 并发上限 | 每用户 2、每实例 12，**仅单实例内存**；跨实例由日限额和 `max-instances=3` 约束 | 单测 |
| 异常后的额度释放 | ✅ 预留后的任何路径只结算一次：到达模型前失败 → 0 Token；取消/失败 → 至少"估算 + 已产出"；成功 → 模型计数。结算失败被记录但不抛出，预留会保留到次日（偏保守） | 单测 + 模拟器 |
| 缺陷（已修） | 预留与结算此前各自取"当天"，**跨 UTC 午夜的请求会把估算释放到第二天**；现在日期在请求开始时确定 | 单测（变异验证）+ 模拟器 |
| 未覆盖 | 进程被强杀时预留残留（偏保守）；第三方供应商费用不在任何 Google 封顶内；Firestore 安全规则未验证 | — |

## 5. 超时关系
```
生成上限（应用）          240s   Chat__MaxGenerationSeconds
  +30s 清理（保存回复/结算/final）
API Cloud Run 超时        300s
  +30s
web 代理上游上限          315s   = web 超时 - 15s（仅兜底挂死的上游）
web Cloud Run 超时        330s
```
- 部署前强制校验 `生成上限 ≤ API-30s`、`web ≥ API+30s`；`Chat__MaxGenerationSeconds` 与 `UPSTREAM_TIMEOUT_MS` 显式写入服务，不依赖默认值（假 gcloud 测试覆盖拒绝场景）。
- 发现并修复：OpenAI 兼容供应商的 `HttpClient` 沿用 100s 默认超时，会提前掐断首 token 很慢的推理模型；现改为无限，由生成上限统一控制。
- SSE 心跳 15s，远小于任何空闲超时。
- 限制：Kestrel 无请求总时长限制；`CountTokens`/摘要调用在取消时被放弃等待但不会被中止；Vertex 流式取消仅在数据块边界（SDK 限制，已实验验证）。

## 6. 认证绕过开关、服务账号、Secret 与"意外覆盖"
| 检查 | 结论 |
|---|---|
| `DISABLE_SERVERLESS_AUTH` 在生产启用？ | 部署脚本/工作流/Dockerfile 均未设置 ✅。**新增三道防线**：①`server.mjs` 在 Cloud Run（`K_SERVICE`/`K_REVISION` 存在）上拒绝启动；②发布前若部署环境设置了该变量则中止；③发布前检查 web 服务现有配置，含该变量则中止并给出修复命令（均有测试，且变异验证） |
| 后端是否有认证开关/模拟提供商 | 无；`MockChatModelClient` 未在 DI 中注册 ✅ |
| 服务账号 | ⚠️ 工作流中 `WEB_SERVICE_ACCOUNT` 回退到 `CLOUD_RUN_SERVICE_ACCOUNT`：未单独配置时 **web 与 API 共用同一服务账号**，web 因而拥有 Vertex/Firestore/Storage 权限（违背最小权限）。建议单独创建仅有 `run.invoker`（调用 API）的 web 服务账号。实际 IAM **无法读取** |
| Secret Manager 引用 | `API_SECRETS` 必须匹配 `ENV=名称:版本`；原始值被拒绝且**不写入日志**（测试验证）。未配置时不改动现有挂载 |
| 意外覆盖现有配置 | 环境变量与密钥为合并语义 ✅；但每次部署仍会重置 CPU/内存/并发/超时/实例数/服务账号/端口（既有行为），`crr_ensure_api_access` 会移除 API 的 `allUsers` 调用权限（有意） |
| 迁移冲突 | 变量已存在为明文时再挂载同名密钥，gcloud 可能报类型冲突；`docs/secrets.md` 已给出一次性移除步骤，**报错文案未实测** |

## 7. Spend cap：对 Vertex AI 与 Cloud Run 的适用性与最小方案
依据（官方"Manage spend cap budgets"页面的**搜索摘录**，页面本身在本环境无法打开；细节以官方页为准）：
- 可用的服务含 **Vertex AI（`aiplatform.googleapis.com`，现称 Gemini Enterprise Agent Platform）**、**Cloud Run（`run.googleapis.com`）**、Gemini API、Cloud Run functions。
- **一个预算只能作用于一个项目 + 一个服务**；不能设在结算账号/文件夹/组织/标签层级；周期为自然月（每月 1 日起）。
- 触顶后：该服务的使用在本预算周期内被**暂停**，直到手动解除或下个周期自动解除。
- **不是实时闸门**：存在延迟，超出部分照常计费；按估算的标价总成本（不含抵扣与折扣）。
- Firebase 的页面另有一张更窄的清单（App Hosting、AI Logic 等），与本项目无关，不要混淆。

**对当前项目是否适用：无法确认**（需要你的项目号、结算账号类型与权限；我读不到）。只读核对：控制台"Billing → Budgets"创建界面中是否出现 spend cap 选项，且上述两项服务可选。

**最小化配置方案**
| 对象 | 建议 | 原因 |
|---|---|---|
| Vertex AI | **创建 spend cap**，额度 = 你愿意承担的月上限的 1.0–1.5 倍（它只是兜底，不是主要手段） | 触顶只会让 Gemini 调用失败；网站仍可访问，OpenAI 兼容供应商不受影响（应用显示明确错误）。费用主要来自这里 |
| Cloud Run | **先不设封顶，只设普通预算告警**（50%/90%/100%）+ 依赖 `max-instances`（api=3/web=2）。若一定要封顶，额度取预期月成本的 **≥10 倍**，只作"失控"断路器 | 触顶会**暂停整个项目的 Cloud Run**（web 和 API 同时下线），等于整站不可用；而 Cloud Run 在个人规模成本很低，已由实例上限限制了最坏情况 |
| 其它服务（Firestore、Storage、Secret Manager、Artifact Registry） | 仅告警 | 封顶可能影响数据读写 |
| 第三方模型供应商 | 各自后台设限，或不配置 Key | Google 封顶**不覆盖** |
预期的整站行为：Vertex 触顶 → Gemini 报错、其它供应商仍可用；Cloud Run 触顶（若设置）→ 整站不可用，需手动解除。**应用层全站日上限（立即生效）仍是第一道防线。**

## 8. 候选修订版流程与真实云环境验收用例
### 8.1 候选版流程（已实现并重写，用有状态的假 gcloud 测试；未真实执行）
完整说明与"切流前改变线上资源的操作清单"见 `docs/cloud-run.md`。要点：
1. **预检（只读）**：记录两个服务正在承载流量的修订版（从 JSON 解析 `percent==100` 的条目，不依赖 `traffic[0]`）；API 若对 `allUsers` 开放则停止。
2. **部署候选**：现有服务 `--no-traffic --tag=candidate`，且**不带**会改写服务级 IAM 的 `--allow/--no-allow-unauthenticated`（仅新建服务时带）。
3. **候选验证（切流前）**：标签 URL 检查 web；用 ID 令牌检查私有 API 候选 `/health/ready`。**取不到令牌 = 停止发布**（不再跳过）；显式 `CRR_ALLOW_UNVERIFIED_API_CANDIDATE=true` 才可放行并留下醒目警告。失败时：未切流、未改任何访问权限。
4. **授权**：仅当缺少时才**新增** web→API 的 `run.invoker`，紧挨切流之前；回滚时只撤销本次新增的授权。
5. **切流并核实**：`--to-latest` 失败 → 发布失败；退出码为 0 也要核对服务确实在承载新修订版，否则发布失败。
6. **冒烟**：仅在两个切换都被确认后执行。
7. **回滚**：所有"尝试过"切流的服务（含失败/结果不明的）切回原修订版并核实；回滚失败 → 非零退出 + 明确提示"生产可能仍在新修订版" + 手工命令。
- **未验证的假设**：`candidate---` 标签 URL 的拼接规则；标签 URL 是否接受以服务 URL 为受众的令牌；`--min/max-instances` 是否随候选立即生效。请在测试服务上演练（用例 G1–G5）。
- 普通代码推送、PR、CI 均不会修改生产（第 2 节）。

### 8.2 真实环境验收用例
前置：在**候选修订版**（`--no-traffic`）或独立测试服务上执行；准备白名单内/外各一个账号。回滚统一用：
`gcloud run services update-traffic <svc> --to-revisions=<上一修订版>=100`（候选版无流量时无需回滚，删除标签即可）。

| 编号 | 用例 | 步骤 | 预期 | 失败时 |
|---|---|---|---|---|
| A1 | 模型可调用与区域 | 默认模型发"你好" | 流式出字，`final.status=completed`；Cloud Run 日志无 404/403；记录调用端点为 global | 检查 Vertex API 启用、SA 角色 `aiplatform.user`、模型访问；不要切流 |
| A2 | 每个配置模型各问一次 | 逐个选择 4 个 Gemini 模型 | 均成功；预览模型成功与否如实记录；失败的模型 `Stage`/`RetireDate` 标注或下架 | 下架该模型后再上线 |
| B1 | 图片 | 上传小 PNG/JPG 并提问 | 回答基于图片内容；附件在会话中可见 | 记录；若失败，检查 MIME/大小限制与 SDK |
| B2 | PDF | 上传 <500KB PDF 并提问摘要 | 回答基于 PDF；Token 估算不会立即触顶 | 同上 |
| C1 | 思考模式 | 对 Flash 依次选 minimal/low/medium/high，对 Pro 选 low/high | 均成功；有思考内容时显示；`minimal` 不报错 | 记录不兼容的档位并在配置中移除 |
| C2 | 联网搜索 | auto/off/force 各问"今天的新闻" | auto/force 有引用；off 无引用；注意 force 仅为提示词 | 记录（已知限制 F-11） |
| D1 | 长时间流式输出 | 高思考档位 + 长回答，>60s | 完整结束；SSE 中可见 `: ping`；不被 60s 切断 | 检查两个服务 `timeoutSeconds` 是否为 300/330 |
| D2 | 截断 | 要求极长回答 | 出现"回答被截断"提示与 `truncated` 状态；刷新后仍可见 | 若频繁出现，调大输出上限（B5） |
| D3 | 用户取消 | 生成中点"停止" | UI"已停止生成"；刷新后部分回复仍在（cancelled）；记录 Vertex 用量停止增长的时间 | 记录延迟（SDK 限制：块边界） |
| D4 | 断线 | 生成中关闭标签页/断网 | 服务端日志"caller cancelled"；部分回复已保存；再打开会话可见 | — |
| D5 | 上游中断显示 | （测试服务）让上游中断 | UI 显示"连接中断"，不显示成功 | 检查 GFE 行为（未验证项） |
| D6 | 超过 240s | 构造超长生成 | 状态 failed，提示"生成时间过长"，保留内容 | 检查 `Chat__MaxGenerationSeconds` 是否生效 |
| E1 | 模型错误 | 发送一个不存在的模型名（改请求） | 明确提示"所选模型…已不可用"，不静默回退 | — |
| E2 | 额度触顶（用户） | 将测试用户 `DailyRequestLimit` 临时设为 2，发 3 次 | 第 3 次提示"今日请求次数已用尽" | 恢复配置 |
| E3 | 额度触顶（全站） | 测试服务把 `QUOTA_GLOBAL_DAILY_REQUEST_LIMIT` 设为 3 | 第 4 次起提示"站点今日使用量已达上限"；Firestore `usageQuotaGlobal` 计数 =3 | 恢复配置 |
| E4 | 并发上限 | 同账号同时开 3 个长对话 | 第 3 个提示"当前有多个对话正在生成" | — |
| F1 | 白名单限制 | 白名单外账号登录并发消息；白名单内账号同样 | 外：`/api/*` 403 且前端显示原因；内：正常 | 立即撤销策略配置 |
| F2 | 旧会话读取 | 打开升级前创建的会话 | 消息完整、状态按 completed 显示、可继续对话 | 若报错，回滚修订版 |
| F3 | 消息保存 | 发送→刷新→检查 | 用户消息与回复各一条，无重复；`messages.status` 仅非完成时存在 | — |
| G1 | 候选验证失败 | 在测试服务发布一个 `/health/ready` 会失败的 API 镜像 | 发布在**切流前**停止；两个服务仍在旧修订版；IAM 无任何变化；候选保留 0% 流量供排查 | 删除候选标签 |
| G1b | 冒烟失败回滚 | 候选能过验证但切流后冒烟失败（例如临时让 web 首页 500） | 两个服务自动回到旧修订版并核实；脚本非零退出 | 手工 `update-traffic --to-revisions` |
| G1c | 切流失败 | 在测试服务临时撤销部署身份的 `run.admin` 后发布 | 发布失败并报告"Traffic switch FAILED"，不会宣布成功 | 恢复权限 |
| G2 | 配置合并 | 手工给服务加一个环境变量，再部署 | 该变量仍在 | — |
| G3 | 认证绕过防护 | 给测试 web 服务手工加 `DISABLE_SERVERLESS_AUTH=true` 再发布 | 发布被拒绝并给出修复命令 | — |
| G4 | Secret 迁移 | 对测试 API 执行 `docs/secrets.md` 步骤 | 供应商 Key 以 `secretKeyRef` 出现，对话可用 | 报错文案记录回文档 |
| G5 | 私有 API 候选令牌 | 配置 `CRR_IDENTITY_TOKEN_SA` 后发布；再取消该变量发布一次 | 前者候选 API 被检查（日志有 `candidate check … /health/ready -> 200`）；后者发布**停止**并说明原因 | — |
| G6 | 公开的 API | 在测试服务上给 API 临时添加 `allUsers` 调用权限后发布 | 预检阶段停止并打印手工修复命令，**不会自动移除** | 移除该授权 |

## 9. 本轮验收中发现并修复的缺陷（均有回归测试）
1. 配额预留与结算日期不一致（跨 UTC 午夜）。
2. 全站计数事务冲突耗尽时返回笼统错误；现 fail-closed + 明确提示，重试次数可配置。
3. 超时无分层余量（web=api=300）与模型 `HttpClient` 100s 默认超时。
4. 本地认证绕过开关无生产防护。
5. `API_SECRETS` 无格式校验（可能把真实 Key 当作值传入并进入日志）。
6. 候选版本在切流前未被实际测试。
7. Gemini 3.x 关闭思考使用已不推荐的 `thinkingBudget=0`。

## 10. 验证证据汇总
| 项 | 结果 | 范围 |
|---|---|---|
| `dotnet build VertexAI.slnx` | ✅ 0 警告 0 错误 | 本地 |
| C# 测试（无模拟器） | ✅ 75 项通过 | 本地 |
| C# 测试 + Firestore 模拟器 | ✅ 87 项通过（含并发原子性、所有权、状态读写、旧消息兼容、编排器端到端） | 本地，`demo-*` 项目，不触达真实云 |
| Node 测试 | ✅ 34 项通过 | 本地 + CI |
| 浏览器端到端（真实 Chromium + 代理 + 假后端） | ✅ 6 项 | 本地，未进 CI |
| 发布脚本测试（有状态假 gcloud：模拟当前/最新修订版、IAM、切流） | ✅ 116 项检查 | 本地 + CI |
| 仓库卫生检查 `repo-hygiene.sh`（日志/超大文件/凭据/自动部署触发/CI 写权限） | ✅ 14 项；变异验证 4 类（已跟踪日志、2 MiB 文件、私钥、push 触发）均被拦截 | 本地 + CI |
| 变异验证 | ✅ 竞速取消、畸形令牌 catch、`--set-env-vars` 回归、回滚、候选闸门、绕过防护、日期键；第二轮新增：原始 `crr_promote`、去掉切流核实、只回滚"成功切流"的服务、IAM 授权前移、令牌缺失时跳过检查，共 5 种 | 本地 |
| 真实 API 进程冒烟（假凭据） | ✅ 200/401、大小写路径、无未处理异常 | 本地 |
| 真实 Vertex / Firebase / Cloud Run / IAM / Billing | ❌ **无法验证** | — |

## 11. 对 Phase 0 文档的一致性核对
- 发现并已修正的不一致：`phase0-report.md` 中的超时数值（295s/300s）与测试数量已过时（已加指向本报告的说明并更新）；`cloud-run.md` 的 web 手工部署示例超时（300s→330s）；`cost-controls.md` 缺少 `TransactionMaxAttempts` 与并发表现说明（已补）。
- 仍需注意：`docs/audit/findings.md`、`maturity-assessment.md` 的基线部分保持原样（描述实施前状态），以 `phase0-report.md` 和本报告为准。

## 12. 尚未解决的风险
- **R1**：Vertex 流式取消只在块边界终止（SDK 0.14.0 限制），静默思考期间取消后仍可能继续计费一小段；升级 SDK 需单独迁移（1.x 枚举全部改名）。
- **R2**：全站计数文档是单点热点；真实 Firestore 冲突行为未测，规模上去后需分片计数。
- **R3**：实际用量仍非供应商 `usage`（Gemini 为历史 CountTokens，兼容供应商为字符数/4），搜索/附件失败不退还。
- **R4**：`MaxOutputTokens=8192` 含思考 token，高档位下"被截断"可能常见（现已显式提示）。
- **R5**：搜索 `force` 仅为提示词；引用未与句子对应；兼容供应商无搜索/PDF 却在 UI 可选（F-11/F-12）。
- **R6**：会话读取仍全量加载消息与附件（F-13）；删除非原子（F-16）。
- **R7**：web 与 API 默认共用服务账号；无账号级删除/数据保留策略。
- **R8**：日志非 JSON、无告警（F-18）；UTC 日界线。
- **R9**：Firestore 安全规则、IAM、Billing 状态完全未知。

---

## 13. 第二轮复审：发现、证据、修复与测试

### 13.1 误提交的 Firestore 模拟器日志
| 项 | 结论 |
|---|---|
| 是什么 | `VertexAI.Tests/firestore-debug.log`：**Firestore 模拟器的调试日志**，25,819 行 / 约 2.5 MB。我在 `VertexAI.Tests/` 目录里运行 `firebase emulators:exec`，模拟器把日志写入当前目录，随后被 `git add -A` 带入提交 `88c0eb0`。我第一版报告"无大文件"的结论是在这次运行之前做的，没有复查 |
| 内容 | 859 段 `Transaction lock timeout` 堆栈（来自我故意制造的并发冲突测试，属预期）+ 15 条 INFO + 模拟器端口信息 |
| 敏感信息扫描 | 私钥/PEM、`private_key`、`AIza…`、`ya29.`、JWT、`Bearer`、`Authorization`、`api_key`、`secret`、`password`、`token`、邮箱、`sk-…`、`ghp_`、`client_email`、`GOOGLE_APPLICATION…`、测试用户/文档内容：**全部 0 命中** |
| 唯一的环境信息 | 第 1 行是沙箱的 `JAVA_TOOL_OPTIONS`：本机代理 `127.0.0.1:34349`、信任库路径、no-proxy 主机列表（含沙箱工具链所用的内部服务域名和私有网段）。**不含凭据**，属低风险的运行环境信息披露 |
| 是否需要轮换 | **不需要**：没有密钥、令牌或账号信息。代理端口是沙箱本地回环地址 |
| 处理 | `git rm --cached` + 删除文件；`.gitignore` 增加 `*.log`、`firestore-debug.log`、`firebase-debug.log`、`ui-debug.log`、`pubsub-debug.log`、`.firebase/`、`firebase-export-*/`；新增 `scripts/test/repo-hygiene.sh` 并加入 CI（拦截已跟踪的日志/导出、>1 MiB 的文件、私钥与常见令牌）。**未重写远端历史** |
| 复验 | 按 CI 的方式从仓库根目录再次运行模拟器测试（87 项通过），日志出现在 `./firestore-debug.log`，`git status` 不显示它（被忽略）。CI 的任何工作流都没有 `git add/commit/push`（hygiene 脚本断言），因此 CI 不会提交文件 |
| 遗留 | 该文件仍在 PR 的历史提交 `88c0eb0` 里，可通过历史访问。因其不含凭据，我建议**不**为此重写历史；若你希望从历史中彻底清除，需要你决定后用 squash 合并（合并结果的树里已没有它）或重写分支历史 |

### 13.2 `crr_promote()` 的错误传播缺陷
- **复现**（本地，假 gcloud 让 `--to-latest` 失败）：`crr_promote returned: 0 (want non-zero: --to-latest failed)`。
- **根因**：函数最后一条命令是 `update-traffic --remove-tags … || true`，它决定了函数的退出码；而调用处写成 `crr_promote … && promoted+=(…) || failed=1`，处于 `errexit` 被禁用的上下文，第一条命令的失败被完全忽略。
- **后果**：切流失败被记为成功 → 随后冒烟测试对着**旧修订版**运行并通过 → 发布被宣布成功，但新修订版从未承载流量；若是部分切流（API 成功、web 失败），已切流的服务不在回滚列表里的情形也会出现。
- **修复**：①`--to-latest` 失败立即 `return 1`；移除候选标签改为尽力而为（失败只记录，不影响结果）；②切流后**核实**服务确实承载最新修订版（退出码为 0 也不算数）；③回滚范围改为所有"尝试过"切流的服务（失败或结果不明的也要恢复），按相反顺序恢复并核实；回滚失败 → 非零退出并打印手工命令；④冒烟只在两个服务的切换都被核实后才执行；⑤"当前承载流量的修订版"改为从 JSON 里取 `percent==100` 的条目（原来读 `traffic[0]`，带标签的 0% 条目可能排在前面）。
- **测试**（有状态的假 gcloud，会真实改变"正在承载的修订版"）：API 切流失败；web 切流失败且 API 已切（部分成功）→ API 被恢复；结果不明（命令报错但已生效）→ 被恢复；命令返回 0 却什么都没变 → 失败；旧版本 web 冒烟成功不被当作发布成功；移除标签失败不影响好的发布、也不掩盖失败的切流；冒烟失败回滚；回滚本身失败；首次部署无回滚目标。
- **变异验证**：恢复原始 `crr_promote` → 4 项以上失败；去掉切流核实 → 4 项失败；只回滚"成功"的服务 → 2 项失败。

### 13.3 候选发布是否真的不影响线上
**不是**（第一版的结论不准确）。切流前会改变线上资源且不随流量回滚恢复的操作有：
1. `crr_ensure_api_access()` 在**候选验证之前**对 API 做 `add-iam-policy-binding` 和 `remove-iam-policy-binding … allUsers`（服务级 IAM，立即生效）。
2. `gcloud run deploy --allow-unauthenticated / --no-allow-unauthenticated`：即使带 `--no-traffic`，也会立即改写服务级 IAM（在现有服务上可能重新打开或关闭公开访问）。
3. 部署前置步骤每次都对附件桶重复 `add-iam-policy-binding`。
4. 创建仓库/桶/索引（仅在缺失时创建，幂等且只增）。
5. 服务级伸缩参数是否随候选立即生效：**未核实**。

**修复**：
- 删除 `crr_ensure_api_access`。改为：只读预检（发现 `allUsers`/`allAuthenticatedUsers` 就**停止并给出手工命令**，不再静默移除）→ 候选验证通过后、切流前，**仅在缺失时新增** web→API 的 `run.invoker`，并记录；回滚时只撤销本次新增的授权，原有授权不动。
- 现有服务的部署不再带 `--allow/--no-allow-unauthenticated`，仅新建服务时带。
- 桶授权改为"缺失才添加"（`crr_ensure_bucket_access`），工作流与脚本共用。
- **测试**：现有服务无服务级 IAM 参数；授权已存在 → 零 IAM 调用；授权缺失 → 在最后一次部署之后、第一次切流之前新增一次；候选失败 → **零** IAM 变更且零流量变更；切流失败 → 撤销本次新增的授权，而原有授权保持；API 公开 → 在构建/部署之前停止且不改任何东西；web-only 发布不触碰 API 的 IAM；桶授权幂等。
- **变异验证**：把授权前移到候选验证之前 → 3 项失败。

### 13.4 取不到私有 API 候选的身份令牌，应该跳过还是阻止？
**应该阻止（已改）。** 理由：
- 跳过意味着风险最高的部分（API 修订版）在无任何验证的情况下进入生产，只靠切流后的冒烟兜底；
- 切流后的冒烟里"直连 API 必须 403"检查的是 IAM 层，根本不会触达容器；只有 `/api/workspace/config` 经 web 转发会触达新 API，但那已经是对真实用户暴露之后。
- 实际约束：WIF 联合凭据不能直接签发 ID 令牌，所以需要 `CRR_IDENTITY_TOKEN_SA`（见 `docs/cloud-run.md`）。这是一个需要你做的 IAM 配置（B7）。
- 保留显式出口 `CRR_ALLOW_UNVERIFIED_API_CANDIDATE=true`（日志里有醒目警告），web-only 发布不需要令牌。
- 测试：无令牌 → 发布停止、零流量零 IAM 变更、提示支持的修复方式；显式放行；web-only 不需要；通过模拟身份令牌签发时使用正确的受众。变异验证：恢复"跳过"行为 → 2 项失败。

### 13.5 部署工作流改为仅手动触发
仅修改 `on:`（删除 `push`），保留 `workflow_dispatch` 及其 `deploy_target` 输入；未添加 GitHub Environment，未改动 WIF/OIDC 配置。PR CI（`ci.yml`）不受影响，并在本轮重新运行（结果见 CI 小节）。

### 13.6 第二轮的测试结果汇总
| 项 | 结果 |
|---|---|
| `dotnet build VertexAI.slnx` | ✅ 0 警告 0 错误 |
| C# 测试 | ✅ 75 项（无模拟器）/ 87 项（Firestore 模拟器，按 CI 方式从仓库根目录运行） |
| Node 测试 | ✅ 34 项 |
| 发布脚本测试（有状态假 gcloud） | ✅ 116 项检查 |
| 仓库卫生检查 | ✅ 14 项 |
| 浏览器端到端 | ✅ 6 项 |
| 变异验证 | ✅ 本轮新增 5 种脚本变异 + 4 种仓库卫生变异，均被捕获 |

## 14. 是否已具备开始"候选环境验收"的条件

**代码和流水线：具备。** 发布脚本、部署工作流和仓库卫生已通过自动化测试与 CI；工作流只能手动触发，PR 与合并都不会部署。

**云端环境：尚不具备。** 开始第 8.2 节用例之前，你需要先完成（我没有也不会替你操作）：
1. 一个可用于验收的 Cloud Run 环境：**独立的测试服务**（推荐，复制一份服务名/项目）或现有项目里的 `--no-traffic` 候选。首次部署的服务不受候选保护，所以不要把首次创建直接放在生产服务上。
2. GitHub 仓库变量/机密（`GCP_PROJECT_ID`、`WIF_PROVIDER`、`GCP_DEPLOY_SERVICE_ACCOUNT`、`CLOUD_RUN_SERVICE_ACCOUNT`、`FIREBASE_AUTH_DOMAIN`、`FIREBASE_API_KEY`、`FIREBASE_APP_ID`），以及 B7 的令牌签发配置（`CRR_IDENTITY_TOKEN_SA`）。
3. 访问白名单（B2）与 Spend cap / 预算告警（B3）已配置，避免验收期间产生不受控费用。
4. （如使用）Secret Manager 迁移（B6）。
完成后，在 GitHub Actions 里手动运行 `Deploy Cloud Run`，先对测试环境，按 8.2 逐项验收。

## 15. 状态一览（截至第二轮复审）

### 已彻底解决（代码 + 自动化测试 + CI）
- 误提交的模拟器日志：已移除、已忽略、CI 拦截；无凭据，无需轮换。
- `crr_promote` 错误传播；切流后核实；回滚范围与回滚核实；回滚失败的告警。
- 切流前的 IAM 变更（服务级 IAM 标志、`allUsers` 的静默移除、授权前移、桶授权重复写入）。
- 取不到私有 API 候选令牌时跳过检查（现为停止）。
- 部署工作流的自动触发（现为仅手动）；CI 只读；CI 无云端认证、无写仓库步骤。
- 第一轮的 6 个缺陷（配额跨日、冲突提示、超时分层、绕过防护、密钥格式、候选闸门）与思考关闭映射。

### 需要真实 Google Cloud 验证
- B1 全部：Vertex 调用与区域、图片/PDF、思考档位、搜索、取消后上游停止、断线表现、旧会话读取。
- 候选标签 URL 的拼接规则；标签 URL 是否接受以服务 URL 为受众的令牌；`--min/max-instances` 是否随候选立即生效。
- `update-traffic --to-latest` 后 `status.traffic` 的真实形态（脚本已按"带标签的 0% 条目可能在前"处理，用 JSON 解析 `percent==100`）。
- `--no-allow-unauthenticated` 之外，现有服务不带该参数时的访问策略是否保持不变。
- 真实 Firestore 在高并发下全站计数的冲突表现；Firestore 安全规则；IAM 实际授权；Billing / Spend cap 的适用性。
- Secret 迁移的类型冲突报错文案；`CRR_IDENTITY_TOKEN_SA` 的 IAM 配置是否足以签发令牌。

### 仍属 Phase 1 的已知项（本轮未扩大范围）
- 把"创建仓库/桶/索引"从每次发布移到一次性 bootstrap；分片计数；供应商真实 usage 结算；JSON 日志与告警；会话分页；web 与 API 使用不同服务账号。

## 16. 第三轮：候选验收与正式发布完全分离

> 本节取代第 8.1、14 节中关于"单一发布流程"的描述。操作步骤见 [`docs/staging-and-release-guide.md`](../staging-and-release-guide.md)，机制说明见 [`docs/cloud-run.md`](../cloud-run.md)。

### 16.1 本轮改动
| 需求 | 实现 | 证据 |
|---|---|---|
| prepare / promote 两种模式，默认 prepare | `CRR_MODE` 默认 `prepare`，`CRR_ENV` 默认 `staging`；`crr_run_prepare` 的代码路径不含 `update-traffic` | 假 gcloud 测试：prepare 全流程"无任何改变流量/IAM 的调用"；7 个变异测试（删除各项保护后测试必然失败） |
| promote 必须明确确认 | 口令 `promote:<env>:<IMAGE_TAG>`（绑定环境与提交），缺失/错误在任何 gcloud 调用前失败；生产另需 `CRR_ACCEPTANCE_REF` | 同上；工作流步骤也被测试执行（`scripts/test/deploy-workflow.test.sh`） |
| promote 只提升已 prepare 的同一提交 | 按服务判定 `absent/mismatch/live/prepared`；缺失/不匹配拒绝；已生效则幂等 | 测试覆盖 absent、mismatch、live、部分 prepare、`all` 但 API 未 prepare |
| staging 隔离 | 独立项目；服务名 `-staging`、桶名含 `staging`、Firestore/Firebase/桶 ≠ 生产值；生产拒绝 staging 命名；生产不创建服务；staging 必须有访问白名单 | 假 gcloud 测试 + 工作流步骤测试（19 项） |
| 候选 web ↔ 候选 API 链路 | `BACKEND_URL` 指向 API 的服务 URL；标签 URL 无 `BACKEND_AUDIENCE` 时 web 拒绝启动（`resolveBackendAudience`）；生产 promote：切 API → 再检查候选 web 与新 API → 切 web；staging：完整业务验收跑在已 promote 的真实前后端上 | Node 测试 36 项；链路复查测试 |
| 冒烟 ≠ 业务验收 | prepare 日志明确打印 `NOT business acceptance`；业务验收是独立脚本 `scripts/acceptance/staging-acceptance.mjs` 与独立的手动工作流 | 验收脚本 10 项测试（假服务器 + 假 Firebase 登录接口，含失败注入） |
| 云端配置指南 | `docs/staging-and-release-guide.md` | — |
| 修正 `cloud-run.md` | 删除"push 自动部署""自动移除 allUsers"等；标注手工命令绕过保护；标注历史记录 | grep 无残留 |

### 16.2 测试结果（本地）
- 发布脚本假 gcloud 测试：203 项通过。
- 工作流步骤测试（真实执行工作流里的 shell）：19 项通过。
- 验收脚本测试：10 项通过。
- Web 代理/前端 Node 测试：36 项通过。
- .NET 服务测试：75 项通过；仓库卫生检查通过。
- CI 新增两步：`Deploy workflow tests`、`Staging acceptance suite tests`。

### 16.3 仍然**没有**被证明的（需要真实云端）
- 所有真实 Vertex / Cloud Run 行为：模型可用性、取消后上游是否停止、>60 秒流式经真实前端、候选标签 URL 的 IAM 与受众行为、`min/max-instances` 是否随候选生效。
- 验收脚本只在假服务器上测过，**没有对真实 staging 跑过**；真实运行中个别用例（搜索引用、思考档位取值）可能需要根据实际返回调整。
- 首次部署的新服务没有可回滚的旧修订版。
- 生产无法做"候选前端 + 候选后端"同时联调，只能在 staging 完成。

### 16.4 现在能否安全创建测试环境？
**代码与流水线：可以。** staging 的创建需要你先完成云端配置（指南步骤 1–8）；工作流的默认值与隔离检查保证第一次点击也只会对 staging 做 prepare。真实验收通过之前，不应对生产执行 promote。
