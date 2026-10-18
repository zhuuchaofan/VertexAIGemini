# 发布手册：在 Cloud Shell 部署新版工作台

这份手册对应一个确定的提交。照着从上到下执行即可，不需要补代码、猜参数，也不需要重新输入已有的 Firebase 配置、模型密钥或 Secret 引用：脚本从线上的两个 Cloud Run 服务里读取现有配置，只新增必要的设置并替换镜像。

- **分支**：`claude-ai-chat-redesign-f8o3rg`（PR #2，草稿；**不要合并到 `main`**，`main` 的推送会自动触发部署流水线）。
- **提交**：以交付消息里给出的完整 SHA 为准；同一个提交也打了标签 `release/workbench-v2`（见 1.1）。
- 所有命令都可以失败后重跑；`DRY_RUN=1` 会只打印将要执行的变更命令。

## 0. 先看清楚会发生什么

| 阶段 | 命令 | 改动 | 对现有用户的影响 |
|---|---|---|---|
| 计划 | `plan` | 无（只读） | 无 |
| 备份 | `backup` | 只向备份桶写入 | 无 |
| 索引 | `indexes` | 新增 Firestore 复合索引（已有的不动） | 无 |
| 构建 | `build` | 只向 Artifact Registry 推镜像 | 无 |
| 部署 | `deploy` | API 新镜像（v2 路由仍关闭，行为与旧版一致）；Web 新镜像作为**无流量**修订，标签 `next` | 无：访客仍使用现有 Web 修订 |
| 启用 v2 | `enable-v2` | API 环境变量 `Runs__Enabled=true`（新增路由，旧界面不使用） | 无 |
| 验证 | `verify` + 第 4 节清单 | 无 | 无 |
| 切流量 | `promote` | Web 全部流量到新修订；`/` 仍然显示旧界面 | 新修订的 Web 超时为 300 秒 |
| 切界面 | `ui next` | `/` 改为显示新界面 | 用户看到新界面；旧界面仍在 `/legacy` |

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
git checkout --detach release/workbench-v2 2>/dev/null || git checkout --detach origin/claude-ai-chat-redesign-f8o3rg
git rev-parse HEAD                            # 必须与交付消息里的 SHA 完全一致
export EXPECT_COMMIT=<交付消息里的完整 SHA>   # 之后每个命令都会据此拒绝错误的检出
```

如果名称不是 `asia-east1`，或者服务名不是 `vertex-ai-api` / `vertex-ai-web`，在这里 `export API_SERVICE=… WEB_SERVICE=…`。`plan` 找不到服务时会直接说明，不会创建新服务。

## 2. 按顺序执行

```bash
S="bash scripts/release-cloud-shell.sh"

$S plan        # 只读。核对：项目、区域、当前修订、将要变化的设置（Web 超时、Runs__MaxDurationSeconds）
```

`plan` 的输出必须与你的认知一致（项目、服务、附件桶名）。不一致就停下，不要继续。

```bash
$S backup      # Firestore 整库导出 + 附件桶复制到备份桶；末尾会打印并记录备份前缀
$S indexes     # 创建 runs 索引并等到 READY（最长约 20 分钟；超时可直接重跑）
$S build       # Cloud Build 构建两个镜像，标签是提交短 SHA；已存在则跳过
$S deploy      # API 新镜像（v2 仍关闭）+ Web 新镜像（无流量，标签 next）；末尾打印测试地址
$S enable-v2   # 需要已记录备份与 READY 索引，否则拒绝
$S verify      # 自动检查：私有边界、/health/ready、新旧界面、v2 路由
```

`verify` 全部通过后，用 `deploy` 末尾打印的测试地址（形如 `https://next---<web 服务>-….run.app/app/`）做第 4 节的**真实验证**。

```bash
$S promote     # 验证通过后：Web 全部流量切到新修订（"/" 仍是旧界面）
$S ui next     # 想让所有人默认进入新界面时再执行
$S status      # 随时查看：当前修订、v2 开关、默认界面、已记录的备份和回滚点
```

任意一步失败：读错误信息，修复原因（通常是权限或等待索引），**重跑同一个命令**。`deploy` 第一次运行时记录“发布前正在服务的修订”，重跑不会覆盖这条记录。

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

下面每一项都发生在真实的 Firebase、Vertex、GCS 上，我没有在沙箱里执行过。请在 `deploy` + `enable-v2` 之后、`promote` 之前，用无流量修订的测试地址完成。**除“自动检查”外，其余每项都会产生少量真实费用或写入真实数据，全部使用你自己的账号。**

| # | 项目 | 操作 | 预期结果 | 验证状态 |
|---|---|---|---|---|
| 1 | 自动检查 | `bash scripts/release-cloud-shell.sh verify` | 全部 `ok`：API 直连 403、`/health/ready` 200（真实 Firestore）、`/app/` 200、`/legacy` 200、v2 路由未登录 401、Web 超时 300 | **未在真实环境验证**（脚本逻辑已用假 gcloud 测过） |
| 2 | Firebase 登录 | 打开测试地址 `/app/`，用已有账号登录；退出再登录；再试一次“忘记密码”或 Google 登录（任选你日常使用的） | 登录后进入会话列表，旧会话都在；登出回到登录页；刷新后仍保持登录 | **未真实验证**（沙箱只用开发用的伪用户） |
| 3 | 索引 | 登录后打开任意旧会话 | 消息正常显示；`gcloud logging read 'severity>=ERROR AND textPayload:"FAILED_PRECONDITION"' --limit=5` 没有新记录 | **未真实验证** |
| 4 | Vertex | 选 Gemini 模型，发送“用一句话介绍你自己” | 逐字流出回复；回复下方点“运行详情”：状态已完成、模型版本有值、用量来源为“服务商报告”、请求与实际两列都有值 | **未真实验证**（沙箱只用 Mock 模型；用量与结束原因的真实形状见 `real-call-verification-plan.md`） |
| 5 | GCS 附件 | 发送一条带小图片（< 1 MB）的消息 | 缩略图显示，点击能下载并与原文件一致；`gcloud storage ls "gs://<附件桶>/chat-attachments/<会话ID>/"` 出现 `<消息ID>/01-<16位哈希>.png`（`plan` 输出里有桶名） | **未真实验证**（“不存在才创建”的真实 GCS 行为、权限均未验证） |
| 6 | 附件保护 | 同一条消息的重试不会产生新对象 | 重试后同一路径下对象数不变 | **未真实验证** |
| 7 | 停止 | 发送“写一篇 2000 字的文章”，生成中点停止 | 立刻显示“已停止”；已生成内容保留；运行详情里状态为已停止、原因“你停止了生成” | **未真实验证**（Mock 下已测；真实 Vertex 的取消行为见验证计划实验 A） |
| 8 | 刷新恢复 | 同样的长回复，生成中刷新页面 | 刷新后继续显示该会话；回复自动接上并最终完成（或标记为已中断且保留已生成部分），不会出现重复的问题 | **未真实验证**（Mock 与模拟器下已测） |
| 9 | 双窗口 | 在窗口 A 生成时，窗口 B 打开同一会话 | B 显示“正在另一个窗口生成回复”，结束后自动出现回复 | **未真实验证** |
| 10 | 时限 | 发送一个会超过 5 分钟的任务（可选，费用最高，可跳过） | 约 270 秒时以“超时”结束并保留已生成部分，而不是连接断开 | **未真实验证** |
| 11 | 其他服务商 | 若已配置 OpenAI 兼容服务商：选它发一条 | 正常回复；运行详情中用量要么是服务商报告要么显示“未知”，不会编造 | **未真实验证** |

任何一项不符合预期：先不要 `promote`；运行 `disable-v2` 即可让新界面不可用而旧界面不受影响，把现象告诉我。

## 5. 备份、回滚、恢复

### 5.1 备份

`backup` 做了两件事，命令等价于：

```bash
gcloud firestore export gs://<项目>-firestore-backups/pre-v2-<时间戳> --project=<Firestore 项目> --database='(default)'
gcloud storage rsync --recursive gs://<附件桶> gs://<项目>-firestore-backups/pre-v2-<时间戳>-attachments   # 只复制，不删除
```

备份前缀记录在 `~/vai-release/state.env`（`BACKUP_PREFIX=`）。完整的位置、权限、恢复演练与按场景恢复，见 [`backup-and-restore.md`](backup-and-restore.md)。**恢复演练（该文第 5 节）没有被执行过**，建议在 `promote` 之前抽一次空闲时间做；演练恢复到临时数据库，不影响生产。

### 5.2 回滚（按影响由小到大）

v2 对数据是叠加式的：只新增可选字段和 `runs`、`userSettings` 集合，不改写、不删除旧数据。所以正常回滚**不需要恢复数据**。

```bash
S="bash scripts/release-cloud-shell.sh"

$S ui legacy        # 只是想把 "/" 换回旧界面（新界面仍可从 /app/ 访问）
$S disable-v2       # 新界面不可用（/api/v2/* 返回 404），旧界面完全不受影响；已存的 v2 数据保留
$S rollback         # Web 与 API 的流量回到发布前记录的修订（需要 deploy 当时记录的 state.env）
```

没有 `state.env` 时手动回滚：

```bash
gcloud run revisions list --service=vertex-ai-web --region="$REGION" --limit=5
gcloud run revisions list --service=vertex-ai-api --region="$REGION" --limit=5
PREV_WEB_REVISION=<旧 Web 修订> PREV_API_REVISION=<旧 API 修订> bash scripts/release-cloud-shell.sh rollback
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
7. 真实环境下 Cloud Run 对标签修订地址的访问（`https://next---…`）：如果该地址在你的组织策略下不可达，改用 `promote` 之前的另一种方式：先 `promote`，再用 `ui legacy` 保持默认界面不变，通过 `/app/` 验证，有问题用 `rollback`。
8. 容器冒烟没有覆盖“已登录请求”（生产镜像按设计拒绝开发用的伪用户）；已登录路径由 e2e 在同一份代码上覆盖。
