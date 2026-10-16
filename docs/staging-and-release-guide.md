# Staging 环境与正式发布指南（云端配置 + 操作顺序）

这份文档面向**你（项目所有者）**：按顺序做完 Google Cloud 和 GitHub 上的配置，就能得到一个与生产完全隔离的 staging 环境，并用「候选验收 → 完整业务验收 → 正式发布」三步安全上线。

约定：
- 文中所有 `<...>` 都是占位符，请换成你自己的值。**不要把密钥、密码、令牌写进仓库或 Issue/PR。**
- 本文命令仅供参考，使用前请对照 Google Cloud 当前控制台/官方文档核对（产品界面和参数会变）。标记 **【未核实】** 的地方是代码作者没能在真实云端验证的假设。
- 本仓库的自动化**不会**替你创建项目、开通计费、修改 IAM（`bootstrap_infrastructure` 只创建 Artifact Registry 仓库 / 附件桶 / 索引）。下面凡是"你来做"的步骤都需要你手工完成。

---

## 0. 先看结论：整体怎么走

```
(1) staging prepare ─▶ (2) staging promote ─▶ (3) staging acceptance ─▶ (4) production prepare ─▶ (5) production promote
 候选冒烟（不切流）     切 staging 流量         完整业务验收（真实模型）    生产候选冒烟（不切流）     口令+验收引用，切生产流量
```

- 每一步都是你在 GitHub → Actions 里**手动**点的，没有任何自动触发。
- `prepare` 永远不移动流量；`promote` 需要你亲手输入 `promote:<环境>:<提交SHA>`。
- staging 与生产使用**不同的 GCP 项目、Firebase/Firestore、附件桶、服务名、GitHub 变量**，工作流选择环境后只读对应的一套，缺值时**不会**回落到另一套。
- 生产 promote 额外要求 `acceptance_ref`（已通过的 `Staging Acceptance` 运行链接）。

## 1. 需要的云端资源清单

| 资源 | staging | 生产（已有） | 说明 |
|---|---|---|---|
| GCP 项目 | **新建**（例如 `<name>-staging`） | 现有 | 隔离计费、IAM、数据；不要复用生产项目 |
| Firebase 项目 | 同上（同一个 GCP 项目启用 Firebase） | 现有 | 独立用户库；staging 的 Firebase 与生产必须不同 |
| Firestore | staging 项目内一个 Native 模式数据库 | 现有 | 测试数据，随时可清空 |
| Cloud Storage 附件桶 | `<staging-project>-attachments-staging`（名称必须含 `staging`） | 现有 | 私有、统一桶级访问 |
| Artifact Registry | staging 项目内的 `vertex-ai` 仓库 | 现有 | |
| Cloud Run 服务 | `vertex-ai-api-staging`（私有）、`vertex-ai-web-staging`（公开入口） | 现有两项 | 名称必须以 `-staging` 结尾 |
| 服务账号 | 运行(API)、web、部署(CI)、探测(可选) | 现有 | 各自只在自己的项目里 |
| Workload Identity Federation | staging 项目内一套池/提供方 | 现有 | 让 GitHub Actions 无密钥登录 |
| Vertex AI | staging 项目启用，且模型可用 | 现有 | 验收会真实调用，产生少量费用 |
| 预算告警 | **必须**设置 | 现有 | 见第 8 节 |

## 2. 操作顺序（逐步）

### 步骤 1：创建 staging 项目并开通计费（你来做）
1. 在控制台创建新项目，记下 **项目 ID**（下文 `<STG_PROJECT>`）。
2. 为它关联计费账号。
3. **立即**创建预算告警（见第 8 节），再继续后面的步骤。

### 步骤 2：启用 API（在 staging 项目）
```bash
gcloud services enable run.googleapis.com cloudbuild.googleapis.com artifactregistry.googleapis.com \
  firestore.googleapis.com aiplatform.googleapis.com iam.googleapis.com iamcredentials.googleapis.com \
  sts.googleapis.com cloudresourcemanager.googleapis.com identitytoolkit.googleapis.com \
  --project=<STG_PROJECT>
```

### 步骤 3：Firebase 与 Firestore（staging 项目）
1. 在 Firebase 控制台把 `<STG_PROJECT>` **添加为 Firebase 项目**（不要选生产项目）。
2. Authentication → 登录方式：启用 **电子邮件/密码**（验收脚本用它登录）；如需也可启用 Google。
3. Firestore Database → 创建 **Native 模式** 数据库，位置与 Cloud Run 区域相近（例如 `asia-east1`）。
4. 项目设置 → 添加 **Web 应用**，记下 `apiKey`、`appId`、`authDomain`（`<STG_PROJECT>.firebaseapp.com`）。
   - `apiKey`/`appId` 会下发给浏览器，不是机密，但本仓库仍按 Secret 存放。
   - ⚠ **不要**给这把 Web API Key 加"HTTP 引荐来源网址"限制：验收脚本在服务器端调用 Firebase 登录接口，没有引荐来源，会被拒。可以只限制可调用的 API（Identity Toolkit）。
5. 创建**验收测试账号**：Authentication → 用户 → 添加用户（邮箱 + 强密码）。记下该用户的 **用户 UID**（后面放进白名单）。
   - 控制台创建的用户邮箱默认**未验证**，而邮箱白名单只信任已验证邮箱，所以 staging 用 **UID 白名单**最省事。
6. 可选：再建一个**不在白名单**的账号，供验收的"越权被拒（403）"用例使用。
7. Firestore 索引：由 `bootstrap_infrastructure`（步骤 9）创建，无需手工。

### 步骤 4：服务账号（staging 项目，你来做）
建议 4 个，名称仅为示例：
```bash
P=<STG_PROJECT>
gcloud iam service-accounts create run-api-stg  --project=$P --display-name="Cloud Run API runtime (staging)"
gcloud iam service-accounts create run-web-stg  --project=$P --display-name="Cloud Run web (staging)"
gcloud iam service-accounts create ci-deploy-stg --project=$P --display-name="GitHub deploy (staging)"
gcloud iam service-accounts create ci-probe-stg  --project=$P --display-name="Candidate probe (staging)"   # 可选，见步骤 6
```

### 步骤 5：IAM 授权（staging 项目，你来做）
**API 运行账号 `run-api-stg`**（只在 staging 项目）：
```bash
for role in roles/datastore.user roles/aiplatform.user; do
  gcloud projects add-iam-policy-binding $P --member="serviceAccount:run-api-stg@$P.iam.gserviceaccount.com" --role=$role
done
```
- 附件桶权限由 bootstrap 授予（`roles/storage.objectUser` 或自建的 `vertexAiAttachmentObjectUser` 自定义角色）。staging 建议直接用预定义角色，并设置变量 `STAGING_ATTACHMENT_STORAGE_ROLE=roles/storage.objectUser`。

**web 账号 `run-web-stg`**：无需项目级角色。它在 API 服务上的 `roles/run.invoker` 由发布流程在 staging prepare 时自动**新增**。

**部署账号 `ci-deploy-stg`**（GitHub Actions 使用，权限只限 staging 项目）。能完成发布流程的一组角色（**【未核实】**，是依据命令推导的，首次演练时若提示缺权限，按报错补充，不要直接给 Owner）：
```bash
for role in roles/run.admin roles/cloudbuild.builds.editor roles/artifactregistry.admin \
            roles/storage.admin roles/serviceusage.serviceUsageConsumer roles/datastore.indexAdmin; do
  gcloud projects add-iam-policy-binding $P --member="serviceAccount:ci-deploy-stg@$P.iam.gserviceaccount.com" --role=$role
done
# 允许它把服务"以"运行账号/web账号身份部署：
for sa in run-api-stg run-web-stg; do
  gcloud iam service-accounts add-iam-policy-binding $sa@$P.iam.gserviceaccount.com \
    --member="serviceAccount:ci-deploy-stg@$P.iam.gserviceaccount.com" --role=roles/iam.serviceAccountUser
done
```
`roles/run.admin`、`roles/storage.admin`、`roles/artifactregistry.admin` 偏宽，是为了让首次 bootstrap 能创建资源；稳定后可收窄（Phase 1 事项）。
另外 Cloud Build 默认构建账号需要能把镜像推到 Artifact Registry、读取源码桶——新项目通常已具备，**【未核实】**，失败时看构建日志。

### 步骤 6：（强烈建议）候选 API 探测账号
私有 API 的候选必须带 Google ID 令牌才能被检查。联合身份凭据不能直接签发 ID 令牌，需要一个被模拟的服务账号：
```bash
# 探测账号可以调用 staging 的 API 服务（服务创建后再执行；首次部署前可先跳过，用下面的放行开关）
gcloud run services add-iam-policy-binding vertex-ai-api-staging --project=$P --region=<REGION> \
  --member="serviceAccount:ci-probe-stg@$P.iam.gserviceaccount.com" --role=roles/run.invoker
# 部署账号可以模拟它：
gcloud iam service-accounts add-iam-policy-binding ci-probe-stg@$P.iam.gserviceaccount.com \
  --member="serviceAccount:ci-deploy-stg@$P.iam.gserviceaccount.com" --role=roles/iam.serviceAccountTokenCreator
```
然后设置变量 `STAGING_CRR_IDENTITY_TOKEN_SA=ci-probe-stg@<STG_PROJECT>.iam.gserviceaccount.com`。
ID 令牌的受众（audience）必须是**被调用服务的 URL**（即使访问的是带标签的候选 URL）。这是 Cloud Run 官方规则，发布脚本按此实现。
> 首次创建 staging 服务时没有"候选"可检查（新服务直接承载流量），不需要令牌；**从第二次发版起**，API 候选必须能被探测，否则 prepare 会停止。若暂时配不好，可临时设 `STAGING_CRR_ALLOW_UNVERIFIED_API_CANDIDATE=true`（**仅 staging**，之后删除）。生产不要开启。

### 步骤 7：Workload Identity Federation（staging 项目，你来做）
让 GitHub Actions 无需服务账号密钥登录（`<OWNER>/<REPO>` 换成你的仓库）：
```bash
gcloud iam workload-identity-pools create github-stg --project=$P --location=global --display-name="GitHub (staging)"
gcloud iam workload-identity-pools providers create-oidc github --project=$P --location=global \
  --workload-identity-pool=github-stg --issuer-uri="https://token.actions.githubusercontent.com" \
  --attribute-mapping="google.subject=assertion.sub,attribute.repository=assertion.repository" \
  --attribute-condition="assertion.repository=='<OWNER>/<REPO>'"
PROJECT_NUMBER=$(gcloud projects describe $P --format='value(projectNumber)')
gcloud iam service-accounts add-iam-policy-binding ci-deploy-stg@$P.iam.gserviceaccount.com --project=$P \
  --role=roles/iam.workloadIdentityUser \
  --member="principalSet://iam.googleapis.com/projects/$PROJECT_NUMBER/locations/global/workloadIdentityPools/github-stg/attribute.repository/<OWNER>/<REPO>"
```
`WIF_PROVIDER` 的值形如 `projects/<项目编号>/locations/global/workloadIdentityPools/github-stg/providers/github`。

### 步骤 8：GitHub 仓库变量与密钥（Settings → Secrets and variables → Actions，你来做）
**生产的变量保持原样（不带前缀）。** staging 新增下面这些（`STAGING_` 前缀）。

Variables：

| 变量 | 必填 | 示例/说明 |
|---|---|---|
| `STAGING_GCP_PROJECT_ID` | 是 | `<STG_PROJECT>` |
| `STAGING_GCP_REGION` | 否 | 默认 `asia-east1` |
| `STAGING_ARTIFACT_REPOSITORY` | 否 | 默认 `vertex-ai` |
| `STAGING_API_SERVICE` / `STAGING_WEB_SERVICE` | 否 | 默认 `vertex-ai-api-staging` / `vertex-ai-web-staging`（必须以 `-staging` 结尾） |
| `STAGING_WIF_PROVIDER` | 是 | 步骤 7 的完整资源名 |
| `STAGING_GCP_DEPLOY_SERVICE_ACCOUNT` | 是 | `ci-deploy-stg@<STG_PROJECT>.iam.gserviceaccount.com` |
| `STAGING_CLOUD_RUN_SERVICE_ACCOUNT` | 是 | `run-api-stg@...`（API 运行账号） |
| `STAGING_WEB_SERVICE_ACCOUNT` | 否 | 默认回落到 `STAGING_WEB_INVOKER_SERVICE_ACCOUNT` 再到运行账号；建议填 `run-web-stg@...` |
| `STAGING_WEB_INVOKER_SERVICE_ACCOUNT` | 否 | 同上 |
| `STAGING_FIREBASE_PROJECT_ID` | 是 | `<STG_PROJECT>`（**不能**等于生产的 Firebase 项目） |
| `STAGING_FIRESTORE_PROJECT_ID` | 是 | `<STG_PROJECT>`（**不能**等于生产的 Firestore 项目） |
| `STAGING_FIREBASE_AUTH_DOMAIN` | 是 | `<STG_PROJECT>.firebaseapp.com` |
| `STAGING_ATTACHMENT_STORAGE_BUCKET` | 是 | `<STG_PROJECT>-attachments-staging`（名称必须含 `staging`，且不能等于生产桶） |
| `STAGING_ATTACHMENT_STORAGE_ROLE` | 建议 | `roles/storage.objectUser` |
| `STAGING_ACCESS_ALLOWED_UIDS` | **是**（三选一至少一个） | 验收测试账号的 UID（可逗号分隔多个）。工作流**强制** staging 配置白名单 |
| `STAGING_ACCESS_ALLOWED_EMAILS` / `_EMAIL_DOMAINS` | 可选 | 仅对已验证邮箱生效 |
| `STAGING_QUOTA_GLOBAL_DAILY_REQUEST_LIMIT` 等 | 建议 | 给 staging 的全站日上限设小一点，见 `docs/cost-controls.md` |
| `STAGING_API_SECRETS` | 否 | 第三方模型供应商密钥引用（staging 项目自己的 Secret Manager，见 `docs/secrets.md`） |
| `STAGING_CRR_IDENTITY_TOKEN_SA` | 建议 | 步骤 6 |
| `STAGING_CRR_ALLOW_UNVERIFIED_API_CANDIDATE` | 否 | 仅首次演练用，见步骤 6 |

Secrets：

| 密钥 | 说明 |
|---|---|
| `STAGING_FIREBASE_API_KEY`、`STAGING_FIREBASE_APP_ID` | 步骤 3 的 Web 应用配置（staging 项目的，**不是**生产的） |
| `STAGING_ACCEPT_EMAIL`、`STAGING_ACCEPT_PASSWORD` | 验收测试账号 |
| `STAGING_ACCEPT_OUTSIDER_EMAIL`、`STAGING_ACCEPT_OUTSIDER_PASSWORD` | 可选：不在白名单的账号 |

> 生产的 `GCP_PROJECT_ID`、`FIREBASE_*`、`ATTACHMENT_STORAGE_BUCKET` 等**必须保持设置**：工作流据此证明 staging 没有指向生产数据（缺少这些值时 staging 发布会拒绝运行）。
> 建议（可选，需要你自己在 GitHub 设置）：给 `production` 创建 GitHub Environment 并设置必需审阅人。当前工作流没有绑定 Environment，所以这不是必需步骤。

### 步骤 9：首次 staging 部署（GitHub Actions → Deploy Cloud Run → Run workflow）
参数：`target_env=staging`、`mode=prepare`、`deploy_target=all`、`bootstrap_infrastructure=true`。
- 工作流先在**不访问云端**的情况下校验隔离：staging 服务名后缀、桶名、与生产的 Firestore/Firebase/桶不同、已配置白名单。任一不满足立即失败。
- bootstrap 会创建 Artifact Registry 仓库、附件桶、给运行账号授桶权限、部署 Firestore 索引。
- 然后构建并部署候选 + 冒烟检查，日志末尾会打印 `PREPARED (staging). Smoke checks only - this is NOT business acceptance.` 和下一步的确认口令。
- ⚠ **首次创建的新服务没有"上一个修订版"可回滚**：它一创建就在承载流量。这是 staging，可接受；失败时修复后重新发布，或删除服务重来。

### 步骤 10：把 staging 的 web 域名加入 Firebase 授权域名
Firebase → Authentication → 设置 → 授权域名：添加 `vertex-ai-web-staging-....run.app` 的主机名（服务 URL 在步骤 9 日志里）。否则浏览器登录会失败（验收脚本直接调用接口，不受影响，但你手工点页面会）。

### 步骤 11：之后每次发版的 staging 流程
1. `target_env=staging, mode=prepare`（不勾 bootstrap）：构建候选，冒烟检查。**此时流量没动。**
2. `target_env=staging, mode=promote`，`confirm_promote=promote:staging:<完整提交SHA>`（prepare 的日志里有这行原文）。
   - 脚本只会提升"确实是这个提交"的候选；候选不存在或属于别的提交会拒绝。切流后做校验，失败自动回滚到原修订版。
   - 首次部署后服务已在承载新代码，promote 会提示 `Nothing to promote`，属正常。
3. 运行 **Staging Acceptance** 工作流（见下一节）。

### 步骤 12：完整业务验收（Actions → Staging Acceptance → Run workflow）
该工作流只能访问 staging；它先确认 staging 正在承载**当前提交**，再用测试账号登录，真实地：
| 编号 | 验证 | 说明 |
|---|---|---|
| A1–A3 | 配置接口、登录（Firebase 邮箱密码）、匿名/畸形令牌被拒（401，不是 5xx） | 前后端联调：浏览器等价的完整链路 |
| B1/B2/B4 | 流式对话（真实 Vertex）、消息确实保存、同会话追问保持上下文 | 流式 + 数据保存 |
| C1/C2 | 图片附件、PDF 附件：被接受、被回答、附件随消息保存 | 附件走 staging 附件桶 |
| D1 | 默认模型的每个思考档位 | 思考模式 |
| D2/D3 | 联网搜索（强制）能完成；关闭搜索时不返回引用 | 联网搜索；"强制搜索"是提示词指令，**不保证**一定有引用（无引用会报 WARN） |
| E1 | 用户取消：保留已生成的部分内容，保存状态为 `cancelled` | |
| E2 | 未知模型被明确拒绝（不静默切换模型） | |
| F1/F2 | 非白名单账号得到 403；旧版本写入的会话仍可读取/导出 | 需要提供对应账号/会话 ID，否则 SKIP |
| G1 | （勾选 `long_generation`）超过 60 秒的生成不被截断 | 费用较高 |
| H1 | 测试会话可删除且确实消失 | 只删除本次创建的会话 |

结果判定：`PASSED` 才算通过；有 `FAIL`、有必需项被跳过、或只跑了子集都**不算**通过。**这个工作流证明不了的**（必须人工）：取消后 Vertex 是否真的停止计费、真实前端上 >60 秒流式、全站额度上限的实际触发、成本控制台数字、IAM 是否被改动。清单见 `docs/audit/verification-plan.md`。

> 想本地跑：`ACCEPT_BASE_URL=<staging web URL> ACCEPT_EMAIL=... ACCEPT_PASSWORD=... node scripts/acceptance/staging-acceptance.mjs`（脚本拒绝非 `staging` 主机名）。

### 步骤 13：正式发布到生产
前提：步骤 12 在**同一个提交**上 `PASSED`，并且你手工核对过第 7 节"人工验收项"。
1. Deploy Cloud Run：`target_env=production, mode=prepare`。生产服务必须已存在（脚本**拒绝**替你创建生产服务）；只创建 0% 流量的候选并做冒烟检查，不碰 IAM。
2. 再次运行：`target_env=production, mode=promote`，`confirm_promote=promote:production:<完整提交SHA>`，`acceptance_ref=<步骤 12 运行页面的链接>`。
   - 先切 API，再检查"候选 web + 新 API"能否一起工作，通过后才切 web；两个服务都被校验确实承载新修订版后做冒烟；任何一步失败都按相反顺序自动回滚并校验。
3. 之后人工抽查：登录、一次对话、查看 Cloud Run 修订版与日志。

### 回滚
- 自动：promote 过程中失败会自动回滚；日志会写明是否回滚成功。
- 手工：`gcloud run services update-traffic <服务> --project=<项目> --region=<区域> --to-revisions=<上一修订版>=100`（修订版用 `gcloud run revisions list` 查）。
- 新建服务的第一个修订版无法这样回滚（没有旧版本）。

## 3. 人工验收项（脚本证明不了）
1. 取消后 Vertex 侧是否停止：发起长生成并取消，在 Vertex AI / 计费用量里确认不再增长（Google.GenAI 0.14.0 不响应取消，已知限制，见 `docs/audit/phase0-report.md`）。
2. 在真实前端页面上跑一个超过 60 秒的生成，确认没被中途切断（需要 web 330s/API 300s/proxy 315s 的分层超时已部署）。
3. 全站日额度达到上限时返回的错误提示是否合理（把 staging 的 `STAGING_QUOTA_GLOBAL_DAILY_REQUEST_LIMIT` 临时设成很小的值后测试）。
4. 旧会话兼容：先用旧版本在 staging 写入一个会话，再升级，把会话 ID 填进 `legacy_conversation_id`。
5. 生产 `/health/ready` 之外，生产 web 页面手工走一遍核心路径。

## 4. 如何确保不会误操作生产
| 风险 | 防护 |
|---|---|
| 点错环境 | `target_env` 默认 `staging`；`mode` 默认 `prepare`；`bootstrap` 默认关闭 |
| 验收时不小心切流 | `prepare` 的代码路径不包含任何 `update-traffic`；测试逐条证明 |
| 用 staging 的口令推生产 | 口令格式 `promote:<env>:<SHA>`，环境与提交都被绑定；缺失/错误在任何 gcloud 调用前失败 |
| 没验收就上生产 | 生产 promote 必须带 `acceptance_ref` |
| staging 指到生产数据 | 校验 staging 的 Firestore/Firebase/桶 ≠ 生产值、名称带 `staging`，且 staging 在**独立 GCP 项目**中用独立的部署账号与 WIF（它的凭据本来就碰不到生产） |
| 生产被指到 staging 资源 | 生产拒绝任何 `-staging`/`staging` 命名的资源 |
| 生产服务被脚本意外创建 | 生产 prepare/promote 都拒绝"服务不存在" |
| 变量缺失时悄悄用了另一套 | 环境选择步骤逐项读取所选环境的值，没有跨环境回落（有测试） |
| 自动触发 | 两个工作流都没有 push/pull_request/schedule 触发（有仓库卫生检查） |

## 5. 已知限制与未核实项
- 首次创建的新服务没有可回滚的旧修订版（见上）。
- 候选标签 URL 是否接受以"服务 URL"为受众的 ID 令牌：官方文档摘录表明受众应为服务 URL，但标签 URL 上的 IAM 行为**【未核实】**，请在 staging 演练时看 prepare 的日志。
- `--min/max-instances` 等服务级参数是否随 0% 流量的候选立即生效**【未核实】**。
- 生产上"候选前端 + 候选后端"无法同时联调（候选 web 只能面对当前生产 API）；完整的前后端联调只在 staging 做，生产靠"切 API 后再检查候选 web"与回滚兜底。
- 验收脚本使用真实模型，输出内容不固定；个别用例（如搜索引用数）可能偶发 WARN，需人工判断。
- 真实 Vertex、真实 Cloud Run 的行为（计费、取消、超时）本仓库的 CI **无法**验证，只能靠本指南的人工验收。

## 6. 费用（Billing）
1. 在 staging 项目的 Billing → 预算与提醒中创建预算（例如每月 10–20 美元），阈值 50%/90%/100% 发邮件；预算告警**不会**自动停止服务。
2. 控制费用的真正手段是应用内的日额度（`STAGING_QUOTA_GLOBAL_DAILY_*`）与 `Spend cap`（可用性与细节见 `docs/cost-controls.md`，请以 Google Cloud 当前文档为准）。
3. 不用时：把 staging 的 `min-instances` 保持为 0（默认），或直接删除 staging 的两个 Cloud Run 服务；Firestore/桶中的测试数据可随时清空。

## 7. 清单（打勾用）
- [ ] 新建 staging 项目、开通计费、设置预算
- [ ] 启用 API
- [ ] Firebase（邮箱/密码登录）、Firestore、Web 应用、测试账号（记录 UID）
- [ ] 4 个服务账号 + IAM（含 WIF）
- [ ] GitHub `STAGING_*` 变量与密钥
- [ ] Deploy Cloud Run：staging / prepare / bootstrap
- [ ] Firebase 授权域名加入 staging web 域名
- [ ] （之后每次）staging prepare → promote → Staging Acceptance = PASSED
- [ ] 人工验收项
- [ ] 生产 prepare → 生产 promote（口令 + 验收引用）
