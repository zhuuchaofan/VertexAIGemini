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
  --set-env-vars=PROJECT_ID=your-gcp-project-id,VertexAI__ProjectId=your-gcp-project-id,FIREBASE_PROJECT_ID=your-firebase-project-id,FIRESTORE_PROJECT_ID=your-firebase-project-id,Firebase__ProjectId=your-firebase-project-id,Firebase__ApiKey=FIREBASE_WEB_API_KEY,Firebase__AuthDomain=your-firebase-project-id.firebaseapp.com,Firebase__AppId=FIREBASE_WEB_APP_ID,Workspace__DefaultProviderId=gemini,ATTACHMENT_STORAGE_BUCKET=your-gcp-project-id-vertex-ai-attachments,Persistence__AttachmentStorageBucket=your-gcp-project-id-vertex-ai-attachments
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
  --timeout=60s \
  --min-instances=0 \
  --max-instances=5 \
  --set-env-vars=BACKEND_URL=https://vertex-ai-api-xxxxx-REGION.a.run.app
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
