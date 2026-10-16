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

The repository includes `scripts/deploy-cloud-run.sh`. It creates the Artifact
Registry repository if missing, builds both images with Cloud Build, deploys the
private API service, grants the web service account `roles/run.invoker` on that
API service, reads the API URL, then deploys the public web service with
`BACKEND_URL` pointing at the API.

All deployment values are provided through shell environment variables or CI
variables/secrets. The script does not read env files.

```bash
SERVICE_ACCOUNT=cloud-run-runtime@your-gcp-project-id.iam.gserviceaccount.com \
WEB_SERVICE_ACCOUNT=cloud-run-web@your-gcp-project-id.iam.gserviceaccount.com \
PROJECT_ID=your-gcp-project-id \
FIREBASE_API_KEY=your-firebase-web-api-key \
FIREBASE_APP_ID=your-firebase-web-app-id \
./scripts/deploy-cloud-run.sh
```

Optional overrides:

```bash
PROJECT_ID=your-gcp-project-id
REGION=asia-east1
REPOSITORY=vertex-ai
IMAGE_TAG=git-sha-or-release-id
API_SERVICE=vertex-ai-api
WEB_SERVICE=vertex-ai-web
WEB_SERVICE_ACCOUNT=cloud-run-web@your-gcp-project-id.iam.gserviceaccount.com
WEB_INVOKER_SERVICE_ACCOUNT=cloud-run-web@your-gcp-project-id.iam.gserviceaccount.com
FIREBASE_PROJECT_ID=your-firebase-project-id
FIRESTORE_PROJECT_ID=your-firebase-project-id
FIREBASE_AUTH_DOMAIN=your-firebase-project-id.firebaseapp.com
DEFAULT_PROVIDER_ID=gemini
ATTACHMENT_STORAGE_BUCKET=your-gcp-project-id-vertex-ai-attachments
```

## Automated Deployment

The mainstream production pattern is to deploy from CI. This repository
includes `.github/workflows/deploy-cloud-run.yml`, which builds, tests, deploys
Cloud Run revisions, and runs private-boundary smoke checks.

Recommended setup:

- Use GitHub Actions Workload Identity Federation for Google Cloud auth. Avoid
  long-lived JSON service account keys.
- Store non-secret deployment settings as GitHub Actions repository variables.
- Store Firebase web app values that should not live in source control as
  GitHub Actions repository secrets.
- Deploy immutable image tags using the Git SHA. Avoid relying on `latest` for
  automated deploys.

Required repository variables:

```text
GCP_PROJECT_ID
GCP_REGION
ARTIFACT_REPOSITORY
API_SERVICE
WEB_SERVICE
CLOUD_RUN_SERVICE_ACCOUNT
WEB_SERVICE_ACCOUNT
GCP_DEPLOY_SERVICE_ACCOUNT
WIF_PROVIDER
FIREBASE_PROJECT_ID
FIRESTORE_PROJECT_ID
FIREBASE_AUTH_DOMAIN
DEFAULT_PROVIDER_ID
ATTACHMENT_STORAGE_BUCKET
```

Required repository secrets:

```text
FIREBASE_API_KEY
FIREBASE_APP_ID
```

The workflow runs automatically on pushes to `main` or `master`. It can also be
started manually from GitHub Actions with a deploy target of `all`, `api`, or
`web`.

After the web service URL is created, add its host to Firebase Authentication
authorized domains. Without that Firebase setting, browser login can fail even
when the Cloud Run services are healthy.

## Private API Boundary

The production boundary is:

- `vertex-ai-web` is public.
- `vertex-ai-api` is private and deployed with `--no-allow-unauthenticated`.
- The web service runs as `WEB_SERVICE_ACCOUNT`.
- The API grants `roles/run.invoker` only to the web service account.
- The web proxy sends Cloud Run's identity token in
  `X-Serverless-Authorization`.
- Browser Firebase ID tokens stay in `Authorization` and are verified by the
  API application.

The deployment script and CI remove any stale `allUsers` `roles/run.invoker`
binding from the API service after deployment.

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

## 发布流程（推荐使用脚本/工作流）

`scripts/deploy-cloud-run.sh` 与 GitHub 工作流共用 `scripts/lib/cloud-run-release.sh`：
1. 记录两个服务当前承载流量的修订版；
2. 现有服务以 `--no-traffic --tag=candidate` 部署新修订版（旧修订版继续服务；新服务首次部署无法保护）；
3. API 与 web 都部署成功后才切流；
4. 冒烟：直连 API 应为 403、web 首页与 `/api/workspace/config` 应为 200（带重试）；
5. 任一步失败：已切流的服务自动回到第 1 步记录的修订版，命令以非零状态退出。
配置一律用 `--update-env-vars` / `--update-secrets` 合并，不会覆盖手工配置（见 `docs/secrets.md`）。下面的手工命令同理。

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
