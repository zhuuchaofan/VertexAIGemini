# Cloud Run Deployment

This project runs as two containers:

- API: ASP.NET Core service in `VertexAI/`.
- Web: standalone Node static/proxy service in `apps/web/`.

The API is Firebase Auth + Firestore only. It does not need PostgreSQL, cookies, SMTP, or server-side session storage.

## Prerequisites

- Firebase Authentication enabled.
- Cloud Firestore enabled.
- Vertex AI enabled.
- Firestore composite indexes deployed from `firestore.indexes.json`.
- A runtime service account with these roles on the relevant projects:
  - Firebase Auth token verification through Firebase Admin credentials.
  - Firestore read/write access, for example `roles/datastore.user`.
  - Vertex AI access, for example `roles/aiplatform.user`.
  - Cloud Storage object access on the attachment bucket, for example
    `roles/storage.objectUser` on the bucket.

If Firebase/Firestore and Vertex AI live in different Google Cloud projects, grant the Cloud Run runtime service account access to both projects.

## Scripted Deployment

`scripts/deploy-cloud-run.sh` builds both images with Cloud Build and runs the release procedure in
`scripts/lib/cloud-run-release.sh`. **By default it only prepares a no-traffic candidate in the STAGING
environment.** It never moves traffic unless `CRR_MODE=promote` and the confirmation phrase are both supplied
(see "发布流程" below). Infrastructure creation (Artifact Registry repository, attachment bucket, bucket IAM,
Firestore indexes) only happens when `BOOTSTRAP_INFRASTRUCTURE=true` is set together with `CRR_MODE=prepare`.

All deployment values are provided through shell environment variables or CI variables/secrets. The script does
not read env files.

```bash
# Staging candidate (the default environment and mode). Staging service names must end with "-staging", the
# bucket name must contain "staging", and staging must not share Firestore/Firebase/bucket with production.
CRR_ENV=staging CRR_MODE=prepare \
SERVICE_ACCOUNT=cloud-run-runtime@your-staging-project.iam.gserviceaccount.com \
WEB_SERVICE_ACCOUNT=cloud-run-web@your-staging-project.iam.gserviceaccount.com \
PROJECT_ID=your-staging-project \
API_SERVICE=vertex-ai-api-staging WEB_SERVICE=vertex-ai-web-staging \
ATTACHMENT_STORAGE_BUCKET=your-staging-project-attachments-staging \
ACCESS_ALLOWED_EMAILS=tester@example.com \
PRODUCTION_FIRESTORE_PROJECT_ID=your-production-project \
PRODUCTION_FIREBASE_PROJECT_ID=your-production-project \
PRODUCTION_ATTACHMENT_STORAGE_BUCKET=your-production-bucket \
FIREBASE_API_KEY=staging-firebase-web-api-key \
FIREBASE_APP_ID=staging-firebase-web-app-id \
./scripts/deploy-cloud-run.sh
```

Optional overrides (all per environment):

```bash
REGION=asia-east1
REPOSITORY=vertex-ai
IMAGE_TAG=git-sha-or-release-id      # promote must use the SAME tag that prepare deployed
DEPLOY_TARGET=all                    # all | api | web
WEB_INVOKER_SERVICE_ACCOUNT=cloud-run-web@your-gcp-project-id.iam.gserviceaccount.com
FIREBASE_PROJECT_ID=your-firebase-project-id
FIRESTORE_PROJECT_ID=your-firebase-project-id
FIREBASE_AUTH_DOMAIN=your-firebase-project-id.firebaseapp.com
DEFAULT_PROVIDER_ID=gemini
```

## Automated Deployment (GitHub Actions, manual dispatch only)

`.github/workflows/deploy-cloud-run.yml` runs the same procedure from GitHub Actions. **It has no `push` or
`pull_request` trigger: merging to `main` or opening a PR never deploys.** It is started by hand from the Actions
tab with these inputs (the defaults are the safe ones):

| Input | Default | Meaning |
|---|---|---|
| `target_env` | `staging` | `staging` or `production`. Selects a completely separate set of variables (`STAGING_*` vs the unprefixed production ones). A missing value is never taken from the other environment. |
| `mode` | `prepare` | `prepare` builds and deploys a no-traffic candidate. `promote` switches traffic to an already prepared candidate. |
| `deploy_target` | `all` | `all`, `api` or `web`. |
| `bootstrap_infrastructure` | `false` | First-time setup only (prepare mode): Artifact Registry repo, bucket, bucket IAM, Firestore indexes. |
| `confirm_promote` | empty | Promote only. Must equal `promote:<env>:<full commit SHA>`; the prepare run prints the exact text. |
| `acceptance_ref` | empty | Production promote only. Link/ID of the passed `Staging Acceptance` run. |

`.github/workflows/staging-acceptance.yml` is a second, staging-only manual workflow that runs the business
acceptance suite (real model, attachments, thinking, search, cancel, data saved) against the live staging web.

The complete, ordered list of GitHub variables/secrets, GCP IAM, Firebase, Firestore, Cloud Storage and billing
steps is in [`docs/staging-and-release-guide.md`](staging-and-release-guide.md). Do not duplicate it here.

Immutable image tags (the Git SHA) are always used; `latest` is never deployed.

After the web service URL is created, add its host to Firebase Authentication authorized domains (staging and
production each in their own Firebase project). Without that, browser login can fail even when the Cloud Run
services are healthy.

## Private API Boundary

The production boundary is:

- `vertex-ai-web` is public.
- `vertex-ai-api` is private (created with `--no-allow-unauthenticated`; afterwards its IAM is only ever
  *added to* by the release, never rewritten).
- The web service runs as `WEB_SERVICE_ACCOUNT`.
- The API grants `roles/run.invoker` only to the web service account.
- The web proxy sends Cloud Run's identity token in
  `X-Serverless-Authorization`.
- Browser Firebase ID tokens stay in `Authorization` and are verified by the
  API application.

The release procedure does **not** remove or rewrite IAM on an existing API service. Before building anything
it reads the API's IAM policy and **stops** if `allUsers` or `allAuthenticatedUsers` holds `roles/run.invoker`,
printing the manual command to remove it. The only IAM change it makes is to *add* `roles/run.invoker` for the web
service account when that binding is missing (see the release section); it never removes a binding. `--allow-unauthenticated`
and `--no-allow-unauthenticated` are only passed when a service is created for the first time, because those flags
rewrite service-level IAM immediately and are not undone by a traffic rollback.

## Firestore Indexes

Deploy indexes before routing traffic. The deploy script and CI run this
automatically from `firestore.indexes.json`:

```bash
FIRESTORE_PROJECT_ID=your-firebase-project-id node scripts/deploy-firestore-indexes.mjs
```

Check readiness:

```bash
gcloud firestore indexes composite list \
  --project=your-firebase-project-id \
  --format='table(name.segment(-1),queryScope,state,fields[].fieldPath,fields[].order)'
```

## 发布流程：候选验收（prepare）与正式发布（promote）完全分离

`scripts/deploy-cloud-run.sh` 与 GitHub 工作流共用 `scripts/lib/cloud-run-release.sh`。**部署工作流只支持手动触发（`workflow_dispatch`）**：合并到 `main`、创建 PR、普通推送都不会部署。完整的云端配置顺序见 [`staging-and-release-guide.md`](staging-and-release-guide.md)。

### 两个环境、两种模式
| | `CRR_MODE=prepare`（**默认**） | `CRR_MODE=promote` |
|---|---|---|
| 做什么 | 构建镜像，用 `--no-traffic --tag=candidate` 部署候选修订版，做**候选冒烟检查** | 把流量切到**已经 prepare 过的同一提交**的候选，切后校验，失败自动回滚 |
| 会不会移动流量 | **绝不**（代码里 `crr_run_prepare` 不会调用任何 `update-traffic`；测试用例逐条证明） | 会 |
| 需要什么 | 无 | `CRR_CONFIRM_PROMOTE=promote:<env>:<IMAGE_TAG>`；生产另需 `CRR_ACCEPTANCE_REF`（已通过的 staging 验收的链接/ID） |
| 重复执行 | 安全 | 幂等：已经在承载该提交则提示"Nothing to promote" |

`CRR_ENV` 默认 `staging`，所以什么都不设置时只会对 staging 做 prepare。确认口令同时绑定**环境和提交**：给 staging 的口令不能用于 production，给提交 A 的口令不能用于提交 B。口令缺失或不匹配时，**在任何 gcloud 调用之前**就失败。

promote 只会提升"最新就绪修订版的镜像标签等于本次 `IMAGE_TAG`"的候选：没有候选（`absent`）、候选属于别的提交（`mismatch`）都直接拒绝，不会"顺手"构建或部署新代码。

### 候选冒烟检查 ≠ 完整业务验收
| | 候选冒烟检查（prepare 自动执行） | 完整业务验收（`staging-acceptance`，另行触发） |
|---|---|---|
| 证明什么 | 新修订版能启动、依赖可达、web 能转发到 API、私有 API 没有对外开放 | 登录、真实 Vertex 对话与流式、图片/PDF 附件、思考档位、联网搜索、用户取消、数据确实保存、未知模型被明确拒绝、白名单、旧会话兼容 |
| 数据 | 只读健康检查 | 在 **staging 的 Firestore/附件桶**里创建并删除测试会话，调用真实模型（有少量费用） |
| 通过含义 | **仅表示"可以进入验收"**，日志会明确打印 `NOT business acceptance` | 才是"可以申请正式发布"的依据 |
| 能否单独决定上线 | 不能。`/health/ready` 返回 200 不构成通过 | 通过后，其运行链接作为生产 promote 的 `acceptance_ref` |

### Staging 与生产的隔离
staging 必须是**独立的 GCP 项目 + 独立的 Firebase/Firestore + 独立的附件桶 + 独立的服务名**。脚本强制检查：staging 服务名以 `-staging` 结尾、桶名包含 `staging`、且 Firestore 项目 / Firebase 项目 / 附件桶都不能等于 `PRODUCTION_*` 对应值；生产则拒绝任何看起来像 staging 的资源名；生产**永远不会创建缺失的服务**（只部署到已存在的服务）。工作流还要求 staging 必须配置访问白名单（staging 的 web 在公网可达且调用真实模型）。

**首次部署一个全新服务（例如第一次创建 staging 服务）没有可以回滚的旧修订版**：新服务的第一个修订版直接承载流量，失败时只能删除服务或修复后重新发布。因此 staging 的首次创建是"可丢弃的"，生产首次上线则必须先手工创建好服务（脚本会拒绝替你创建）。

### 步骤与保证（prepare）
| 阶段 | 做什么 | 对线上的影响 |
|---|---|---|
| 0 校验（无云访问） | 模式/环境取值、环境隔离、超时分层、`API_SECRETS` 格式、认证绕过开关 | 无 |
| 预检（只读） | 读取两个服务当前承载流量的修订版；读取 API 的 IAM。API 若对 `allUsers`/`allAuthenticatedUsers` 开放则**直接停止**并给出手工命令 | 无 |
| 构建并部署候选 | 现有服务用 `--no-traffic --tag=candidate`；**不带** `--allow/--no-allow-unauthenticated`；仅新建服务时才带 | 只创建 0% 流量的新修订版 |
| 候选冒烟检查 | 经 `https://candidate---<服务主机>` 检查 web `/`、`/api/workspace/config`；用 ID 令牌检查私有 API 候选 `/health/ready`（受众 = API 服务 URL）；取不到令牌则停止 | 无流量、无权限变化 |

### 步骤与保证（promote）
| 阶段 | 做什么 | 对线上的影响 |
|---|---|---|
| 授权校验 | 口令（及生产的验收引用）在任何云调用前校验 | 无 |
| 候选状态 | 对每个服务判定 `absent/mismatch/live/prepared`；有缺失或不匹配则拒绝 | 无 |
| 新鲜的候选检查 | 只对需要提升的服务再检查一遍候选（prepare 与 promote 之间可能已变化） | 无 |
| 授权（唯一的非流量变更） | 仅当 web 服务账号缺少 API 的 `roles/run.invoker` 时才**新增**（只增不删），紧挨着切流前；本次新增的授权在回滚时撤销，原有授权不动 | 可回滚的最小变更 |
| 切流 | 先 API 后 web；**每一步都校验服务确实在承载新修订版**（退出码 0 不算数）；API 切换后会**再次检查 web 候选能否与"新 API"配合**，再切 web | 切流 |
| 冒烟 | 直连 API 必须 403、web `/` 与 config 为 200（带重试），只在两个服务都确认切换后才执行 | — |
| 失败回滚 | 凡是**尝试过**切流的服务（含失败或结果不明的）都按相反顺序切回原修订版并**校验**；回滚失败会明确提示"可能仍在新修订版"并打印手工命令 | 恢复 |

### 候选 web 与候选 API 的联调链路
候选 web 的 `BACKEND_URL` 指向**API 服务的正式 URL**（不是 API 的标签 URL）：Cloud Run 要求 ID 令牌的 `aud` 是被调用服务的 URL，即使调用的是带标签的 URL 也一样；`apps/web` 在 `BACKEND_URL` 含 `---`（标签 URL）而没有显式 `BACKEND_AUDIENCE` 时会**拒绝启动**，避免因受众错误而在运行时才出现 401/403。

因此：
- **staging**：先 `prepare`（候选冒烟）→ `promote` 到 staging 的两个服务 → 对 staging 的 web 运行完整业务验收。此时 staging 的 web 与 API 是一对真正的前后端，验收覆盖"登录 → 对话 → 附件 → 流式响应 → 数据保存"全链路。
- **生产**：候选 web 只能"候选 web → 当前生产 API"。因此 promote 时先切 API，再检查候选 web 能否与新 API 一起工作，然后才切 web；这一步以及切流后的冒烟、回滚就是生产上的联调保证。真正的"候选前端 + 候选后端"完整联调只有在 staging 做过，**这是 staging 必须存在的原因**。

候选标签 URL 是否接受以"服务 URL"为受众的令牌，官方文档的片段表明应以服务 URL 作为受众，但标签 URL 本身的 IAM 行为**未实测**，请在 staging 演练时确认；脚本没有假设它，取不到令牌时会停止（或按 `CRR_ALLOW_UNVERIFIED_API_CANDIDATE=true` 明示放行）。

### 切流前会改变云端资源/权限的操作（完整清单）
这些在流量回滚时**不会**自动恢复，因此都被限制为"只增、幂等、仅在缺失时"：
| 操作 | 位置 | 处理 |
|---|---|---|
| 推送镜像到 Artifact Registry | 构建 | 无害（不被引用） |
| 创建新修订版（0% 流量） | 部署 | 无害 |
| 部署参数 `--allow/--no-allow-unauthenticated`（服务级 IAM） | 部署 | 仅新建服务时使用；生产不新建服务 |
| `add-iam-policy-binding`（API 调用权限） | promote，切流前一刻 | 缺失时才新增；回滚时撤销本次新增；**不会**移除任何绑定（发现 `allUsers` 即停止） |
| 给 web 服务账号新增 API 的 `roles/run.invoker`（只增不删） | **仅 staging 的 prepare**（staging 没有需要保护的线上流量，候选与验收要能调用 API） | 仅 staging；生产的 prepare 不碰 IAM，生产的授权发生在 promote 切流前一刻 |
| 附件桶的 IAM 绑定、创建 Artifact Registry 仓库 / 附件桶 / Firestore 复合索引 | **仅 `bootstrap_infrastructure=true`（prepare 模式）** | 不存在时才创建，幂等，不删除任何东西 |
| 服务级伸缩参数（`--min/max-instances`） | 部署 | **未核实**是否随 0% 流量的候选一起立即生效；请在 staging 演练时确认 |

### 私有 API 候选需要身份令牌
候选 API 是私有的，要验证它必须能调用它。联合身份（WIF）凭据**不能**直接签发 ID 令牌，所以 CI 需要：
1. 一个专用服务账号（例如 `ci-api-probe@<project>.iam.gserviceaccount.com`），在 **API 服务**上拥有 `roles/run.invoker`；
2. 部署身份对该服务账号拥有 `roles/iam.serviceAccountTokenCreator`；
3. 仓库变量 `CRR_IDENTITY_TOKEN_SA`（staging 为 `STAGING_CRR_IDENTITY_TOKEN_SA`）= 该服务账号邮箱。
**取不到令牌时发布会直接停止**（不会跳过检查）。如果你明确接受"API 候选未经切流前验证、只靠切流后的冒烟与回滚兜底"，可设置 `CRR_ALLOW_UNVERIFIED_API_CANDIDATE=true`，日志里会有醒目的警告。生产不建议开启。

配置一律用 `--update-env-vars` / `--update-secrets` 合并，不会覆盖手工配置（见 `docs/secrets.md`）。下面的手工命令同理。

## 手工命令（仅供参考 / 应急）

> 以下手工命令**绕过**上面的所有保护（模式、环境隔离、确认口令、候选校验、自动回滚）。日常发布请使用脚本或工作流。
> 手工命令里的 `--allow-unauthenticated` / `--no-allow-unauthenticated` 会立即改写服务级 IAM，不随流量回滚恢复，对已有生产服务不要使用。

## API Service

Build and push the API image:

```bash
gcloud builds submit VertexAI \
  --project=your-gcp-project-id \
  --tag=REGION-docker.pkg.dev/your-gcp-project-id/REPOSITORY/vertex-ai-api:IMAGE_TAG
```

Deploy:

```bash
gcloud run deploy vertex-ai-api \
  --project=your-gcp-project-id \
  --region=REGION \
  --image=REGION-docker.pkg.dev/your-gcp-project-id/REPOSITORY/vertex-ai-api:IMAGE_TAG \
  --service-account=SERVICE_ACCOUNT_EMAIL \
  --no-allow-unauthenticated \
  --port=8080 \
  --cpu=1 \
  --memory=1Gi \
  --concurrency=20 \
  --timeout=300s \
  --min-instances=0 \
  --max-instances=10 \
  --update-env-vars=PROJECT_ID=your-gcp-project-id,VertexAI__ProjectId=your-gcp-project-id,FIREBASE_PROJECT_ID=your-firebase-project-id,FIRESTORE_PROJECT_ID=your-firebase-project-id,Firebase__ProjectId=your-firebase-project-id,Firebase__ApiKey=FIREBASE_WEB_API_KEY,Firebase__AuthDomain=your-firebase-project-id.firebaseapp.com,Firebase__AppId=FIREBASE_WEB_APP_ID,Workspace__DefaultProviderId=gemini,ATTACHMENT_STORAGE_BUCKET=your-gcp-project-id-vertex-ai-attachments,Persistence__AttachmentStorageBucket=your-gcp-project-id-vertex-ai-attachments
```

Cloud Run provides `PORT`; the API image respects it automatically.
The image defaults to port `8080`.

## Attachment Storage

Images up to 12MB are compressed in the browser to WebP before upload, targeting
an upload payload under roughly 700KB. The API uploads attachments to Cloud
Storage and stores only attachment metadata plus the object name in Firestore.
Conversation reads and model-history loading hydrate those objects back into
base64 only when needed. This avoids Firestore's per-field size limit for image
payloads.

The scripted deployment defaults to this bucket name:

```text
ATTACHMENT_STORAGE_BUCKET=${PROJECT_ID}-vertex-ai-attachments
```

The script creates the bucket if it is missing and grants the Cloud Run runtime
service account `roles/storage.objectUser` on that bucket. If you create the
bucket manually, keep uniform bucket-level access enabled and do not make the
bucket public; the API reads and deletes private object data for authenticated
users.

## Web Service

Build and push the web image:

```bash
gcloud builds submit apps/web \
  --project=your-gcp-project-id \
  --tag=REGION-docker.pkg.dev/your-gcp-project-id/REPOSITORY/vertex-ai-web:IMAGE_TAG
```

Deploy the web service after the API service URL is known:

```bash
gcloud run deploy vertex-ai-web \
  --project=your-gcp-project-id \
  --region=REGION \
  --image=REGION-docker.pkg.dev/your-gcp-project-id/REPOSITORY/vertex-ai-web:IMAGE_TAG \
  --service-account=WEB_SERVICE_ACCOUNT_EMAIL \
  --allow-unauthenticated \
  --port=8080 \
  --cpu=1 \
  --memory=512Mi \
  --concurrency=80 \
  --timeout=330s \
  --min-instances=0 \
  --max-instances=5 \
  --update-env-vars=BACKEND_URL=https://vertex-ai-api-xxxxx-REGION.a.run.app
```

Cloud Run serves the web container on port `8080`.

## Smoke Checks

```bash
curl -i https://API_URL/health/live
# Expected: 403, because the API service is private.

curl -i https://WEB_URL/
# Expected: 200.

curl -i https://WEB_URL/api/workspace/config
# Expected: 200 through the web proxy.
```

Then sign in through the web service and send a short chat message. A successful request should create:

- `users/{uid}`
- `conversations/{conversationId}`
- `messages/{messageId}` for the user message
- `messages/{messageId}` for the assistant message

## Deployment Record

> 下面的记录是历史，描述的是**当时**的手工/旧流程（例如 push 触发的自动部署、部署后移除 `allUsers`）。这些做法已被上面的流程取代，不要照做。

### 2026-06-19 Manual Cloud Run Deployment

Pre-deployment checks:

```bash
npm run check
dotnet restore VertexAI/VertexAI.csproj
dotnet restore VertexAI.Tests/VertexAI.Tests.csproj
dotnet build VertexAI/VertexAI.csproj --no-restore --disable-build-servers -c Release
dotnet build VertexAI.Tests/VertexAI.Tests.csproj --no-restore --disable-build-servers -c Release
dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj --no-build
```

Result: web syntax checks passed, Release build passed, and 22 tests passed.

Deployment used the checked-in script with a runtime service account exported
at invocation time:

```bash
SERVICE_ACCOUNT=vertex-express@copper-affinity-467409-k7.iam.gserviceaccount.com \
./scripts/deploy-cloud-run.sh
```

Effective deployment settings:

```text
PROJECT_ID=copper-affinity-467409-k7
REGION=asia-east1
REPOSITORY=vertex-ai
API_SERVICE=vertex-ai-api
WEB_SERVICE=vertex-ai-web
FIREBASE_PROJECT_ID=my-agent-app-a5e42
FIRESTORE_PROJECT_ID=my-agent-app-a5e42
DEFAULT_PROVIDER_ID=gemini
```

Cloud Build results:

```text
API build: 6aeb8edb-8d42-41fd-8e3f-1c223a45d3f7
API image: asia-east1-docker.pkg.dev/copper-affinity-467409-k7/vertex-ai/vertex-ai-api:latest
API digest: sha256:f100c65980edb914fbb3424753eb5da370b828dda34856da403075e369afff72

Web build: ffb88f02-324e-44ae-bf5f-4a0c758cfea5
Web image: asia-east1-docker.pkg.dev/copper-affinity-467409-k7/vertex-ai/vertex-ai-web:latest
Web digest: sha256:029b370c80545973c77979e682e449143d6b7d444bc49598f5661220425b4d73
```

Cloud Run results:

```text
API revision: vertex-ai-api-00004-pdw
API URL: https://vertex-ai-api-hyo2yvwwia-de.a.run.app

Web revision: vertex-ai-web-00005-n8p
Web URL: https://vertex-ai-web-hyo2yvwwia-de.a.run.app
```

Initial smoke check results:

```text
GET API /health/live: 200 Healthy
GET Web /: 200 OK
GET API /health/ready: 503 Unhealthy
```

The readiness failure was caused by Firestore permissions for the Cloud Run
runtime service account. The API logs showed:

```text
Grpc.Core.RpcException: Status(StatusCode="PermissionDenied", Detail="Missing or insufficient permissions.")
VertexAI.Services.Health.FirestoreHealthCheck.CheckHealthAsync
```

Before the next production rollout, grant the runtime service account Firestore
access on the Firebase/Firestore project, then rerun `/health/ready`:

```bash
gcloud projects add-iam-policy-binding my-agent-app-a5e42 \
  --member=serviceAccount:vertex-express@copper-affinity-467409-k7.iam.gserviceaccount.com \
  --role=roles/datastore.user

curl -i https://vertex-ai-api-hyo2yvwwia-de.a.run.app/health/ready
```

This IAM binding was applied after the deployment. After a short IAM propagation
delay, readiness recovered:

```text
GET API /health/ready: 200 Healthy
```

### 2026-06-20 Attachment Storage Deployment

This rollout deployed the attachment-storage change for image uploads:

- Browser image uploads are compressed to WebP before sending.
- API uploads attachments to Cloud Storage and stores only metadata plus object
  names in Firestore.
- The deployment script created and authorized the private attachment bucket.

Pre-deployment checks:

```bash
npm run check
dotnet build VertexAI/VertexAI.csproj --no-restore --disable-build-servers
dotnet build VertexAI.Tests/VertexAI.Tests.csproj --no-restore --disable-build-servers
dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj --no-build
```

Result: web syntax checks passed and 22 tests passed.

Deployment command:

```bash
SERVICE_ACCOUNT=vertex-express@copper-affinity-467409-k7.iam.gserviceaccount.com \
./scripts/deploy-cloud-run.sh
```

Attachment storage:

```text
Bucket: gs://copper-affinity-467409-k7-vertex-ai-attachments
Runtime service account: vertex-express@copper-affinity-467409-k7.iam.gserviceaccount.com
Bucket role: roles/storage.objectUser
```

Cloud Build results:

```text
API build: 44ce6105-f534-4527-8f51-3c289e7d80d9
API image: asia-east1-docker.pkg.dev/copper-affinity-467409-k7/vertex-ai/vertex-ai-api:latest
API digest: sha256:3fefb40df68b90156598a1d3616bcc534435286018e1fc455cd14606fe9a710b

Web build: 81a1b3ce-f7eb-4c23-844e-8dda00d74b5d
Web image: asia-east1-docker.pkg.dev/copper-affinity-467409-k7/vertex-ai/vertex-ai-web:latest
Web digest: sha256:1e1b55436afe2271b9926491bd311d4afb076209b86054c57710a33c4ada3051
```

Cloud Run results:

```text
API revision: vertex-ai-api-00005-hkm
API URL: https://vertex-ai-api-hyo2yvwwia-de.a.run.app

Web revision: vertex-ai-web-00006-w5g
Web URL: https://vertex-ai-web-hyo2yvwwia-de.a.run.app
```

Smoke check results:

```text
GET API /health/live: 200 Healthy
GET API /health/ready: 200 Healthy
GET Web /: 200 OK
```

### 2026-06-20 Cloud-First Cleanup Deployment

This rollout deployed the cloud-first cleanup:

- Removed local env loading from API startup.
- Removed local Docker Compose artifacts from the project.
- Required `BACKEND_URL` for the web service.
- Kept attachment storage behind the `Services/Attachments` boundary.

Deployment command:

```bash
SERVICE_ACCOUNT=vertex-express@copper-affinity-467409-k7.iam.gserviceaccount.com \
./scripts/deploy-cloud-run.sh
```

Cloud Build results:

```text
API build: 1d127202-b6fe-4cb8-9986-430e355a0f43
API image: asia-east1-docker.pkg.dev/copper-affinity-467409-k7/vertex-ai/vertex-ai-api:latest
API digest: sha256:92cf7de3edb63d6a45dca02ab8413346802fbbdf82d5c9491ab2183249361ad4

Web build: d9b9dcfe-bcbc-46c4-9111-c8813267cd36
Web image: asia-east1-docker.pkg.dev/copper-affinity-467409-k7/vertex-ai/vertex-ai-web:latest
Web digest: sha256:2f8a7f15014db0733f866166994bd24483c223c492643ab919b0156620224817
```

Cloud Run results:

```text
API revision: vertex-ai-api-00006-44t
API URL: https://vertex-ai-api-hyo2yvwwia-de.a.run.app

Web revision: vertex-ai-web-00007-p6k
Web URL: https://vertex-ai-web-hyo2yvwwia-de.a.run.app
```

Smoke check results:

```text
GET API /health/live: 200 Healthy
GET API /health/ready: 200 Healthy
GET Web /: 200 OK
```

### 2026-06-21 P0 Hardening Deployment

This rollout deployed the P0 hardening changes:

- Gemini safety policy split for normal users and admins.
- Admin quota usage endpoint.
- Endpoint-level export ownership and delete dispatch regression coverage.
- Private API boundary deployment automation.
- Firestore index deployment automation.

Pre-deployment checks:

```bash
npm run check --prefix apps/web
dotnet build VertexAI/VertexAI.csproj --no-restore /p:NuGetAudit=false
dotnet build VertexAI.Tests/VertexAI.Tests.csproj --no-restore /p:NuGetAudit=false
dotnet run --project VertexAI.Tests/VertexAI.Tests.csproj --no-restore
git diff --check
```

Cloud Build results:

```text
API build: ca53f995-2325-4d39-a4f9-f358f38f4863 SUCCESS
API image: asia-east1-docker.pkg.dev/copper-affinity-467409-k7/vertex-ai/vertex-ai-api:p0-20260621-hardening
API digest: sha256:e86add409da12acb67f26c369d3b035d97ee1aca37f5179399c1587032713807

Web build: 6b26bcf5-23e5-42e5-88f9-e92f5391be38 SUCCESS
Web image: asia-east1-docker.pkg.dev/copper-affinity-467409-k7/vertex-ai/vertex-ai-web:p0-20260621-hardening
Web digest: sha256:ae67dfa2c36eb43a9326698392da36a6816431b26ac9c7f3a7715c15a0a3af0f
```

Cloud Run results:

```text
API revision: vertex-ai-api-00012-mlc
API URL: https://vertex-ai-api-151587524132.asia-east1.run.app

Web revision: vertex-ai-web-00012-8m4
Web URL: https://vertex-ai-web-151587524132.asia-east1.run.app
```

Smoke check results:

```text
GET direct API /health/live: 403 Forbidden
GET Web /: 200 OK
GET Web /api/workspace/config: 200 OK
Provider catalog: defaultProviderId=gemini, providerCount=1, modelCount=4
Authenticated chat through web proxy: 200 OK, finalSucceeded=true
Firestore indexes in my-agent-app-a5e42: 3 READY
```

## 流式生成：超时、取消与中断（Phase 0）

### 超时契约（分层，每层都给内层留清理余量）
```text
生成上限（应用）       Chat__MaxGenerationSeconds   240s   到点以 failed 结束并保留已生成内容
  + 30s 清理余量（保存回复、结算配额、发送 final 事件）
API Cloud Run 超时     --timeout                    300s
  + 30s
web 代理对后端上限     UPSTREAM_TIMEOUT_MS          315s   = web 超时 - 15s，仅作"后端挂死"的兜底
web Cloud Run 超时     --timeout                    330s
```
- 真正结束一次生成的是应用层的 240s 上限；其余各层是逐级外扩的兜底，避免任何一层在内层完成清理之前就把连接掐断。
- `scripts/lib/cloud-run-release.sh` 在部署前强制校验：`生成上限 ≤ API 超时 − 30s`、`web 超时 ≥ API 超时 + 30s`，并把 `Chat__MaxGenerationSeconds` 与 `UPSTREAM_TIMEOUT_MS` 显式写入服务；覆盖 `API_TIMEOUT`/`WEB_TIMEOUT` 时请保持这个关系，否则部署会被拒绝。
- 旧默认值 web=60s 会在 60 秒时切断更长的生成，已不再使用。
- OpenAI 兼容供应商的 `HttpClient` 不再使用 100 秒默认超时（它会提前掐断首个 token 很慢的推理模型），时间上限统一由上面的生成上限负责。
- API 每 15 秒（`Chat__HeartbeatSeconds`）发送一次 SSE 注释 `: ping`。

### 终止状态
SSE `final` 事件的 `status` 取值：`completed`、`truncated`（输出上限，内容可用但不完整）、`cancelled`（用户停止/断线）、`failed`（上游错误、超时）。`succeeded` 仅在 `completed`/`truncated` 时为 true。没有收到 `final` 事件的流，前端一律显示为"连接中断"，不会当作成功。

### 部分回复的保存规则
| 状态 | 是否保存 model 消息 | 之后是否作为模型上下文 |
|---|---|---|
| completed | 是 | 是 |
| truncated | 是（status=truncated） | 是 |
| cancelled | 仅当已有内容（status=cancelled） | 是 |
| failed | 仅当已有内容（status=failed） | **否**（只展示给用户，不喂回模型） |
用户消息在生成开始前已保存。连续两个 user 回合（上一次没有回复）在下一次请求时合并，避免违反角色交替。每次请求只会执行一次保存与一次配额结算。

### 取消是否真的终止上游 —— 已验证与未验证
| 链路 | 结论 | 依据 |
|---|---|---|
| 浏览器 → web 代理 | 点击"停止"后，代理立即中止到 API 的请求 | `apps/web/test/proxy.test.mjs`、`test-e2e/stream.e2e.mjs`（真实浏览器 + 真实代理 + 假后端，已通过） |
| web 代理 → API | 上游连接关闭后，ASP.NET 的 `RequestAborted` 触发，取消传入编排器 | `StreamingTests`（端点级，已通过）。**未在真实 Cloud Run 上验证** |
| API → OpenAI 兼容供应商 | 令牌传入 `HttpClient`，取消会关闭响应流 | `OpenAiCancellationStopsReadingAndClosesUpstream`。供应商是否停止计费取决于对方 |
| API → Vertex AI（Google.GenAI 0.14.0） | **只能在"下一个数据块边界"终止**。SDK 的流式方法没有取消参数，`WithCancellation` 被忽略（本地假服务器实验：流照常跑完 40 块）；跳出循环会关闭连接（实验确认服务器侧看到断开）。代码把"等待下一块"与取消令牌竞速，使调用方立即返回并保存部分内容，随后在途读取结束时释放枚举器 | 本地实验 + `PromptCancellation*` 测试。**未用真实 Vertex 验证** |
限制：模型在长时间"静默思考"期间被取消时，Vertex 侧请求可能继续到下一个数据块到达或流结束，这段已生成的 token 仍会计费。根本解决需要升级到支持取消的 SDK（1.x 枚举已全部改名，需要单独迁移）。预热阶段的 `CountTokens`/摘要调用在取消时会被放弃等待，但调用本身不会被中止。

### Cloud Run 上线前请核对
- `gcloud run services describe <web|api> --format='value(spec.template.spec.timeoutSeconds)'` 均为 300。
- 经 Cloud Run 前端（GFE）时，容器中途断开连接是否会让浏览器看到错误尚未在真实环境验证；因此保留了应用层兜底（缺少 `final` 即显示"连接中断"）。
