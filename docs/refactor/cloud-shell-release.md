# 发布手册：在 Cloud Shell 部署新版工作台

这份手册对应一个确定的提交。照着从上到下执行即可，不需要补代码、猜参数，也不需要重新输入已有的 Firebase 配置、模型密钥或 Secret 引用：脚本从线上的两个 Cloud Run 服务里读取现有配置，只新增必要的设置并替换镜像。

- **分支**：`claude-ai-chat-redesign-f8o3rg`（PR #2，草稿；**不要合并到 `main`**，`main` 的推送会自动触发部署流水线）。
- **提交**：以交付消息里给出的完整 SHA 为准（分支末端即该提交；下面的命令会核对 SHA，不一致就拒绝运行）。
- 所有命令都可以失败后重跑；`DRY_RUN=1` 会只打印将要执行的变更命令。

## 0. 先看清楚会发生什么

**API 不是无流量发布。** 只有 Web 是无流量候选修订；**API 新修订在 `deploy` 时立即承接生产流量**。从那一刻起，仍在使用旧界面的用户请求由新 API 代码处理（旧接口行为不变、v2 路由关闭，旧接口的回归由现有测试覆盖，但没有在真实环境验证过）。API 的回滚见 5.2：`rollback api`。

| 阶段 | 命令 | 改动 | 对现有用户的影响 |
|---|---|---|---|
| 计划 | `plan` | 无（只读） | 无 |
| 备份 | `backup` | 只向备份桶写入 | 无 |
| 索引 | `indexes` | 新增 Firestore 复合索引（已有的不动），逐项核验 READY | 无 |
| 构建 | `build` | 只向 Artifact Registry 推镜像 | 无 |
| 部署 | `deploy` | **API 新修订立即承接生产流量**（v2 路由仍关闭）；Web 新镜像作为**无流量**候选修订，标签 `next` | **有：API 新代码开始处理旧界面的请求**；Web 访客仍使用现有修订 |
| 授权域名 | `auth-domain add` | Firebase 授权域名增加测试地址的主机名 | 无 |
| 启用 v2 | `enable-v2` | API 环境变量 `Runs__Enabled=true`（新增路由，旧界面不使用）；产生又一个 API 修订 | 无 |
| 验证 | `verify` + 第 4 节清单 | 无 | 无 |
| 切流量 | `promote` | Web 全部流量到候选修订；`/` 仍然显示旧界面 | 新修订的 Web 超时为 300 秒 |
| 切界面 | `ui next` | `/` 改为显示新界面 | 用户看到新界面；旧界面仍在 `/legacy` |

### 0.1 每次发布一份独立的记录

脚本在 `~/vai-release/` 下为**每个“项目 + 区域 + API 服务 + Web 服务 + 提交”**建一份状态文件（`release-….env`），文件头记录这五项，每次读取都会核对。结果：

- 换项目、区域、服务名或提交，都读不到旧的备份和回滚点；`enable-v2` 会因“没有本次发布的备份”而拒绝。
- 备份超过 72 小时（`BACKUP_MAX_AGE_HOURS` 可调）、或备份的导出元数据已不在，`enable-v2` 拒绝。
- `verify`、`promote` 只认本次 `deploy` 记录的候选修订。
- 候选修订的镜像按**摘要**核对：Cloud Run 把修订的镜像记成 `<仓库>/<名称>@sha256:…`（不带标签），脚本取本次镜像标签在 Artifact Registry 里的摘要来比对，仓库路径或摘要不同都算不匹配。
- **继续同一次发布、但换了更新的检出时**（比如只修了脚本）：`export RELEASE_ID=<原发布的 12 位短 SHA>`。镜像标签和发布记录都按 `RELEASE_ID` 算，所以已构建的镜像、已有的备份和回滚点都沿用，不会重新构建，也不会把回滚点改成已经是新版的修订。

### 0.2 上线后只修前端：`WEB_ONLY=1`

线上已经是新版、只有网页前端需要小修（API 不变）时，用一个更新的检出和 `WEB_ONLY=1`，只构建、只更新 Web，API 不动：

```bash
export EXPECT_COMMIT=<新提交的完整 SHA>
git fetch origin claude-ai-chat-redesign-f8o3rg && git checkout --detach "$EXPECT_COMMIT"
unset RELEASE_ID                      # 用新提交作为这次的发布编号
export WEB_ONLY=1
S="bash scripts/release-cloud-shell.sh"
$S build && $S deploy && $S verify    # Web 先作为无流量候选，verify 只校验 Web 候选
# 在测试地址上看过之后：
$S promote
```

这次发布的回滚点就是“修复之前正在服务的修订”（`rollback web` 回到那里）。

## 1. 准备（每次新开 Cloud Shell 都做一遍）

```bash
# 项目与区域：沿用你当前 gcloud 使用的项目；区域默认 asia-east1，不同则先 export REGION=<区域>
gcloud config get-value project
export REGION=asia-east1                      # 现有服务所在区域

git clone https://github.com/zhuuchaofan/VertexAIGemini.git 2>/dev/null || true
cd VertexAIGemini
git fetch origin claude-ai-chat-redesign-f8o3rg
```

### 1.1 取到确定的提交

```bash
export EXPECT_COMMIT=<交付消息里的完整 SHA>   # 之后每个命令都会据此拒绝错误的检出
git checkout --detach "$EXPECT_COMMIT"
git rev-parse HEAD                            # 必须与交付消息里的 SHA 完全一致
```

如果名称不是 `asia-east1`，或者服务名不是 `vertex-ai-api` / `vertex-ai-web`，在这里 `export API_SERVICE=… WEB_SERVICE=…`。`plan` 找不到服务时会直接说明，不会创建新服务。

## 2. 按顺序执行

```bash
S="bash scripts/release-cloud-shell.sh"

$S plan        # 只读。核对：项目、区域、当前修订、将要变化的设置（Web 超时、Runs__MaxDurationSeconds）
```

`plan` 的输出必须与你的认知一致（项目、服务、附件桶名）。不一致就停下，不要继续。

```bash
$S backup      # Firestore 整库导出到 <根>/firestore，附件复制到 <根>/attachments；记录本次发布的备份根
$S indexes     # 创建缺的索引，并按完整定义逐项核验 READY（最长约 20 分钟；超时可直接重跑）
$S build       # Cloud Build 构建两个镜像，标签是提交短 SHA；已存在则跳过
$S deploy      # 会立即让 API 新修订承接生产流量；Web 候选修订无流量，标签 next；记录候选修订；末尾打印测试地址
$S auth-domain add   # 把测试地址的主机名加入 Firebase 授权域名（见 4.0）
$S enable-v2   # 需要本次发布 72 小时内的备份，并且此刻重新核验全部索引 READY，否则拒绝
$S verify      # 校验本次记录的候选修订（镜像、标签、服务流量）并做自动检查；没有候选或版本不符就失败
```

`verify` 全部通过后，用 `deploy` 末尾打印的测试地址（形如 `https://next---<web 服务>-….run.app/app/`）做第 4 节的**真实验证**。

```bash
$S promote     # 验证通过后：Web 全部流量切到候选修订（"/" 仍是旧界面）
$S ui next     # 想让所有人默认进入新界面时再执行
$S status      # 随时查看：当前修订、v2 开关、默认界面、本机的发布记录文件
```

任意一步失败：读错误信息，修复原因（通常是权限或等待索引），**重跑同一个命令**。`deploy` 第一次运行时记录“发布前正在服务的 API 与 Web 修订”，重跑不会覆盖这条记录。

## 3. 新增或变化的配置

只有这些会被改动，其余（服务账号、Firebase 配置、模型密钥、Secret 引用、扩缩容、IAM）保持原样。

| 位置 | 名称 | 值 | 谁设置 | 说明 |
|---|---|---|---|---|
| API | `Runs__MaxDurationSeconds` | `min(Web, API 超时) − 30`，默认 270 | `deploy` | 生成时限；到时间会如实记录为超时并保留已生成内容 |
| API | `Runs__Enabled` | 先 `false`，`enable-v2` 后 `true` | `deploy` / `enable-v2` / `disable-v2` | 关闭时所有 `/api/v2/*` 返回 404，旧界面不受影响 |
| Web | 请求超时 | 300 秒（原来线上是 60 秒） | `deploy` | 生成会话经 Web 代理流式传输，**较小的超时才是真正的上限** |
| Web | `WEB_DEFAULT_UI` | 先 `legacy`，`ui next` 后 `next` | `deploy` / `ui` | `/` 显示哪个界面；`/legacy` 永远是旧界面，`/app/` 永远是新界面 |
| 附件桶 | 无变化 | | | 沿用现有桶和自定义角色（`create/get/delete`）；新版只用“不存在才创建”写入与按名读取、删除 |

### 3.1 如果以后还会用 GitHub Actions 部署（合并到 `main` 之后）

`deploy-cloud-run.yml` 使用 `--set-env-vars`，会按流水线里的值覆盖这两项。合并之前在仓库 Variables 里设置：

- `RUNS_ENABLED` = `true`（与当前线上一致）
- `WEB_DEFAULT_UI` = `legacy` 或 `next`（与当前线上一致）
- `WEB_TIMEOUT` 保持 `300s`

否则第一次自动部署会把这两个开关重置为默认值。**这就是现在不合并 `main` 的原因之一。**

## 4. 部署后的真实验证清单

### 4.0 先授权测试域名（Firebase 登录需要）

候选修订的测试地址有自己的主机名（`next---<web 服务>-….run.app`）。它不在 Firebase 的“授权域名”里时，Google 登录弹窗/重定向和密码重置链接会失败（邮箱密码登录本身不一定受影响，但不要依赖这一点）。

```bash
bash scripts/release-cloud-shell.sh auth-domain         # 只检查：未授权会打印主机名并返回非 0
bash scripts/release-cloud-shell.sh auth-domain add     # 保留现有域名，追加这个主机名（幂等）
```

等价的手动步骤：Firebase 控制台 → Authentication → Settings → Authorized domains → Add domain，填入测试地址的主机名（不带 `https://` 和路径）。需要的权限：Firebase Authentication 管理员（`add` 用你当前的 gcloud 身份调用 Identity Toolkit Admin API）。测试结束后可在同一页面删除该域名；留着也无害。**这是对生产 Firebase 项目的一处配置修改，只由你执行。**

### 4.1 清单

下面每一项都发生在真实的 Firebase、Vertex、GCS 上，我没有在沙箱里执行过。请在 `deploy` + `auth-domain add` + `enable-v2` 之后、`promote` 之前，用候选修订的测试地址完成。**除“自动检查”外，其余每项都会产生少量真实费用或写入真实数据，全部使用你自己的账号。**

| # | 项目 | 操作 | 预期结果 | 验证状态 |
|---|---|---|---|---|
| 1 | 自动检查 | `bash scripts/release-cloud-shell.sh verify` | 全部 `ok`：候选修订的镜像是本次提交、标签 `next` 指向候选、API 候选正在服务、API 直连 403、`/health/ready` 200（真实 Firestore）、`/app/` 200、`/legacy` 200、v2 路由未登录 401、候选的 Web 超时 300 | **未在真实环境验证**（脚本逻辑已用假 gcloud 测过） |
| 2 | Firebase 登录 | （先完成 4.0）打开测试地址 `/app/`，用已有账号登录；退出再登录；再试一次“忘记密码”或 Google 登录（任选你日常使用的） | 登录后进入会话列表，旧会话都在；登出回到登录页；刷新后仍保持登录 | **未真实验证**（沙箱只用开发用的伪用户） |
| 3 | 索引 | 登录后打开任意旧会话 | 消息正常显示；`gcloud logging read 'severity>=ERROR AND textPayload:"FAILED_PRECONDITION"' --limit=5` 没有新记录 | **未真实验证** |
| 4 | Vertex | 选 Gemini 模型，发送“用一句话介绍你自己” | 逐字流出回复；回复下方点“运行详情”：状态已完成、模型版本有值、用量来源为“服务商报告”、请求与实际两列都有值 | **未真实验证**（沙箱只用 Mock 模型；用量与结束原因的真实形状见 `real-call-verification-plan.md`） |
| 5 | GCS 附件 | 发送一条带小图片（< 1 MB）的消息 | 缩略图显示，点击能下载并与原文件一致；`gcloud storage ls "gs://<附件桶>/chat-attachments/<会话ID>/"` 出现 `<消息ID>/00-<16位哈希>.<扩展名>`（序号从 00 开始，已在真实环境核对）（`plan` 输出里有桶名） | **未真实验证**（“不存在才创建”的真实 GCS 行为、权限均未验证） |
| 6 | 附件保护 | 同一条消息的重试不会产生新对象 | 重试后同一路径下对象数不变 | **未真实验证** |
| 7 | 停止 | 发送“写一篇 2000 字的文章”，生成中点停止 | 立刻显示“已停止”；已生成内容保留；运行详情里状态为已停止、原因“你停止了生成” | **未真实验证**（Mock 下已测；真实 Vertex 的取消行为见验证计划实验 A） |
| 8 | 刷新恢复 | 同样的长回复，生成中刷新页面 | 刷新后继续显示该会话；回复自动接上并最终完成（或标记为已中断且保留已生成部分），不会出现重复的问题 | **未真实验证**（Mock 与模拟器下已测） |
| 9 | 双窗口 | 在窗口 A 生成时，窗口 B 打开同一会话 | B 显示“正在另一个窗口生成回复”，结束后自动出现回复 | **未真实验证** |
| 10 | 时限 | 发送一个会超过 5 分钟的任务（可选，费用最高，可跳过） | 约 270 秒时以“超时”结束并保留已生成部分，而不是连接断开 | **未真实验证** |
| 11 | 其他服务商 | 若已配置 OpenAI 兼容服务商：选它发一条 | 正常回复；运行详情中用量要么是服务商报告要么显示“未知”，不会编造 | **未真实验证** |

任何一项不符合预期：先不要 `promote`；运行 `disable-v2` 即可让新界面不可用而旧界面不受影响，把现象告诉我。

## 5. 备份、回滚、恢复

### 5.1 备份

`backup` 在一个根下放两样东西，`<根>` 形如 `gs://<项目>-firestore-backups/pre-v2-<时间戳>`，记录在本次发布的状态文件（`BACKUP_ROOT=`）里：

```bash
gcloud firestore export "<根>/firestore" --project=<Firestore 项目> --database='(default)'
gcloud storage rsync --recursive gs://<附件桶> "<根>/attachments"      # 只复制，不删除
```

**恢复路径与之一一对应**：Firestore 从 `<根>/firestore` 导入，附件从 `<根>/attachments` 复制回附件桶（`backup-and-restore.md` 第 5、6 节用同一个 `$ROOT`）。完整的位置、权限、恢复演练与按场景恢复见 [`backup-and-restore.md`](backup-and-restore.md)。**恢复演练（该文第 5 节）没有被执行过**，建议在 `promote` 之前抽一次空闲时间做；演练恢复到临时数据库，不影响生产。

### 5.2 回滚（按影响由小到大）

v2 对数据是叠加式的：只新增可选字段和 `runs`、`userSettings` 集合，不改写、不删除旧数据。所以正常回滚**不需要恢复数据**。

```bash
S="bash scripts/release-cloud-shell.sh"

$S ui legacy        # 只是想把 "/" 换回旧界面（新界面仍可从 /app/ 访问）
$S disable-v2       # 新界面不可用（/api/v2/* 返回 404），旧界面完全不受影响；已存的 v2 数据保留
$S rollback web     # 只把 Web 流量回到发布前记录的修订
$S rollback api     # 只把 API 流量回到发布前记录的修订（见下）
$S rollback         # 等价于 rollback all：Web 与 API 都回去
```

**API 回滚要点（因为 API 新修订在 `deploy` 时就承接了生产流量）：**

1. 如果已经 `enable-v2`：先 `disable-v2`，等约一分钟让进行中的回复结束，再 `rollback api`。旧 API 不认识 v2 运行，仍在 `running` 的运行记录会一直停在 `running`（对旧界面无害，之后再次上线新版时由对账器标记为已中断）。脚本在 v2 开着时拒绝回滚 API，除非你设置 `FORCE_ROLLBACK=1`。
2. `rollback api` 只切流量，不动数据，也不改环境变量；旧 API 修订原样回到服务状态（它的 `Runs__Enabled` 是旧值）。
3. 旧界面的用户在 `deploy` 到 `rollback api` 之间由新 API 处理请求；这段时间写入的数据只含新增的可选字段，旧代码会忽略。
4. 回滚点只来自**本次发布**的状态文件，来自别的发布的记录不会被用到。没有记录时手动指定：

```bash
gcloud run revisions list --service=vertex-ai-api --region="$REGION" --limit=5
gcloud run revisions list --service=vertex-ai-web --region="$REGION" --limit=5
PREV_API_REVISION=<旧 API 修订> bash scripts/release-cloud-shell.sh rollback api
PREV_WEB_REVISION=<旧 Web 修订> bash scripts/release-cloud-shell.sh rollback web
```

回到旧版后，新版里产生的会话仍在 Firestore，旧界面能正常读取（新增字段对旧代码无害）。

### 5.3 需要恢复数据时

按 `backup-and-restore.md` 第 6 节：优先“恢复到临时数据库后只复制受影响的文档”；对生产库直接导入是最后手段（不会删除备份之后新建的文档，会让所属会话的 `updatedAt`、`tokenCount` 回退）。

## 6. 新旧界面切换

| 地址 | 内容 |
|---|---|
| `/app/` | 新界面（永远可用） |
| `/legacy` | 旧界面（永远可用） |
| `/` | 由 `WEB_DEFAULT_UI` 决定：`legacy`（默认）显示旧界面；`next` 重定向到 `/app/` |

```bash
bash scripts/release-cloud-shell.sh ui next      # 默认进入新界面
bash scripts/release-cloud-shell.sh ui legacy    # 默认回到旧界面
```

切换会创建一个新的 Web 修订并立即承接流量，不需要重新构建。旧界面使用的接口没有改变。

## 7. 没有真实验证的事项（汇总）

这些在沙箱里用模拟器、Mock 模型、假 OpenAI 上游和真实容器构建验证过，**但没有在真实 Google 环境里验证**：

1. Firebase 真实登录、注册、Google 登录、密码重置。
2. Vertex 真实流式输出、取消、用量与结束原因的真实形状。
3. GCS 真实的“不存在才创建”（`ifGenerationMatch=0`）与自定义角色权限（`create/get/delete`）。
4. 真实 Firestore 上事务与租约的行为（模拟器与生产可能有差异；已有的跨实例测试在模拟器上通过）。
5. 在线修改 Web 超时（60 → 300 秒）对现有用户的影响。
6. 备份与恢复演练。
7. 真实环境下 Cloud Run 对标签修订地址的访问（`https://next---…`）以及它与 Firebase 授权域名的配合：如果该地址在你的组织策略下不可达，改用另一种方式：先 `promote`（`/` 仍是旧界面），通过主域名的 `/app/` 验证，有问题用 `rollback web`。
8. API 新修订在 `deploy` 时承接生产流量后，旧界面请求由新 API 代码处理：旧接口的回归只由现有自动测试覆盖，没有在真实环境验证。
9. 容器冒烟没有覆盖“已登录请求”（生产镜像按设计拒绝开发用的伪用户）；已登录路径由 e2e 在同一份代码上覆盖。

## 8. 可选：通过 Vertex AI 使用 Grok（`xai/grok-4.6`）

内置了一个默认关闭的服务商条目 `grok-vertex`，走 Vertex AI 的 OpenAI 兼容接口，**不需要任何 API 密钥**：认证用 Cloud Run 运行账号换出的短期 Google 令牌（`google-adc`）。

前提（都在 Google Cloud 里，我没有验证过）：

1. 在 Vertex AI Model Garden 里该模型已对你的项目启用（有些合作伙伴模型要先点“启用”并同意条款）。
2. API 服务的运行账号有 `roles/aiplatform.user`（跑 Gemini 已经需要它）。
3. 你知道模型可用的区域：默认 `global`；不是的话设 `GROK_VERTEX_LOCATION`（例如 `us-central1`）。

先用你自己的账号确认接口真的通（会产生极小的费用）：

```bash
LOCATION=global   # 按控制台示例里的区域
HOST=$([ "$LOCATION" = global ] && echo aiplatform.googleapis.com || echo "$LOCATION-aiplatform.googleapis.com")
curl -sS -X POST -H "Authorization: Bearer $(gcloud auth print-access-token)" -H "Content-Type: application/json" \
  "https://$HOST/v1/projects/zhumyproject/locations/$LOCATION/endpoints/openapi/chat/completions" \
  -d '{"model":"xai/grok-4.6","stream":false,"max_tokens":20,"messages":[{"role":"user","content":"say hi"}]}'
```

返回 JSON 里有回复内容再继续。然后打开开关（这是一次对生产 API 的配置修改，会生成新的 API 修订并立即承接流量）：

```bash
gcloud run services update vertex-ai-api --region asia-east1 --update-env-vars=GROK_VERTEX_ENABLED=true
# 区域不是 global 时再加：--update-env-vars=GROK_VERTEX_LOCATION=us-central1
```

刷新页面，模型选择里出现 “Grok (Vertex AI)”。关掉：`--update-env-vars=GROK_VERTEX_ENABLED=false`。

没有验证的部分：真实 Vertex 对这个模型的行为。用量字段、结束原因和流式格式要等你真实调用一次才知道；运行详情里拿不到的值会显示“未知”。这个模型没有配置“思考”选项，设置里不会出现思考档位。

## 9. 其他服务商的联网搜索（先搜后答）

只有 Gemini 自带联网。其他服务商（Grok、DeepSeek、Kimi 等）没有搜索能力，所以**后端先替它们搜**，再把结果交给它们回答：

1. **什么时候搜**：设置里“联网”选“关闭”就不搜；选“自动”时，先用一次很小的 Gemini 调用判断这个问题是否需要联网（新闻、天气、价格、近期事件、用户明确要求查证等才需要；写作、翻译、编程、闲聊不需要），同时把问题改写成适合搜索的关键词（会结合上一轮补全“那明天呢”这类指代）；选“强制查证”则每次都搜。这次判断失败时退回关键词规则（今天、最新、天气、价格、新闻等）。
2. **怎么搜**：用 Gemini 的 Google 搜索得到带来源的摘要，连同编号的来源一起放进给模型的问题里；回复下方照常显示来源链接。
3. **日期**：每个问题都会带上当前日期（北京时间），模型不会再编日期。
4. **失败不影响回答**：搜索超时（25 秒）或出错，回答照常进行，但会明确告诉模型“没有联网，不要编造时效性内容”；回复上显示“联网失败”标记。
5. **记录**：“运行详情”里有“联网检索”一栏：状态（已联网检索 / 没有联网 / 检索失败 / 未启用）、搜索词、来源数、说明，“模型调用”里多一项“检索”次数。

每次搜索会多两次 Gemini 调用（判断一次，搜索一次），费用在 Vertex 账单里。

可调的环境变量（API 服务上，都有默认值，不设也行）：

| 变量 | 默认 | 作用 |
|---|---|---|
| `WebSearch__Enabled` | `true` | 设为 `false` 整体关闭；之后选了联网的问题会提示“没有启用联网检索” |
| `WebSearch__ModelName` | 默认的 Gemini 模型 | 判断和搜索用哪个 Gemini 模型 |
| `WebSearch__TimeoutSeconds` | `25` | 单次判断或搜索的时限 |
| `WebSearch__UtcOffsetHours` | `8` | 告诉模型的日期所用的时区 |

没有真实验证的部分：对着真实 Vertex 的“判断 + 带搜索的 Gemini 调用”（含思考档位、返回格式、来源链接是 Google 的跳转链接）。上线后用手机选 Grok，问“今天几号，西安什么天气”，应看到：回复带“已联网”标记和来源，日期正确；点“运行详情”能看到“联网检索”一栏。
