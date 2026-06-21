# P0 Remediation Checklist

Last updated: 2026-06-21

## P0-1 Gemini Safety Policy

Status: implemented in code; deploy with production config.

- Admin users: may disable Gemini safety settings for private/admin workflows.
- Normal users: should use a compliance safety profile by default.
- Role source: Firebase custom claim `admin=true`.
- Current code foundation: `AuthenticatedUser.IsAdmin` is populated from Firebase custom claims.
- Runtime policy: `ChatOrchestrator` passes the server-authenticated user to Gemini clients; `GeminiSafetyPolicy` chooses the Gemini safety threshold from that trusted user context.
- Default user threshold: `BLOCK_MEDIUM_AND_ABOVE`.
- Admin threshold: `OFF` when `VertexAI:Safety:AdminCanDisable` is enabled.

Configuration section:

```json
"VertexAI": {
  "Safety": {
    "DefaultThreshold": "BLOCK_MEDIUM_AND_ABOVE",
    "AdminThreshold": "OFF",
    "AdminCanDisable": true
  }
}
```

Maintenance:

- Keep normal-user production defaults at a compliance threshold unless a formal policy change is approved.
- Keep admin bypass tied to Firebase custom claim `admin=true`; never trust a browser-supplied admin flag.
- Regression coverage: `GeminiSafetyPolicy splits admin and default thresholds` and `ChatOrchestrator passes authenticated user to aware model clients`.

## P0-2 User Budget And Quota

Status: implemented foundation and admin observability endpoint.

Server-side daily quota is enforced before model calls and recorded in Firestore:

- Collection: `usageQuotas`
- Key shape: `{firebaseUid}_{yyyyMMdd}`
- Query fields: `userId`, `date`
- Limits:
  - daily requests
  - daily estimated tokens
  - daily actual tokens
  - daily web searches
  - daily attachment bytes
- Admin bypass: enabled through Firebase custom claim `admin=true`.
- Admin usage endpoint: `GET /api/admin/quotas/daily?date=yyyy-MM-dd&limit=100&userId=optional`
  - Requires server-authenticated Firebase custom claim `admin=true`.
  - Returns entries plus aggregate totals for requests, estimated tokens, actual tokens, web searches, and attachment bytes.
  - Date accepts `yyyy-MM-dd` or `yyyyMMdd` and is treated as a calendar date, not converted through local time zones.

Configuration section:

```json
"Quota": {
  "Enabled": true,
  "DailyRequestLimit": 100,
  "DailyTokenLimit": 200000,
  "DailySearchLimit": 30,
  "DailyAttachmentBytesLimit": 26214400,
  "AdminBypassEnabled": true
}
```

Follow-up:

- Tune production limits after real usage data.
- Add alerting for aggregate daily spend.
- Backfill `userId` and `date` onto older `usageQuotas` documents if historical admin reports need to include pre-observability records.

## P0-3 Storage Least Privilege

Status: implemented in GCP; keep deployment config aligned.

Target permissions for the Cloud Run API runtime service account on the attachment bucket:

- `storage.objects.create`
- `storage.objects.get`
- `storage.objects.delete`

Avoid broad roles such as `roles/storage.objectAdmin`.

Implemented:

- Custom role: `projects/copper-affinity-467409-k7/roles/vertexAiAttachmentObjectUser`
- Permissions:
  - `storage.objects.create`
  - `storage.objects.get`
  - `storage.objects.delete`
- Bucket binding: `gs://copper-affinity-467409-k7-vertex-ai-attachments`
- Runtime service account: `vertex-express@copper-affinity-467409-k7.iam.gserviceaccount.com`
- Removed broad `roles/storage.objectAdmin` from the attachment bucket.

Maintenance:

- Future deploys should bind `ATTACHMENT_STORAGE_ROLE`, not `roles/storage.objectAdmin`.
- Validate upload/read/delete after each IAM change.

## P0-4 Private API Boundary

Status: implemented and verified in Cloud Run on 2026-06-21.

Target architecture:

- Web Cloud Run service remains public.
- API Cloud Run service does not grant `roles/run.invoker` to `allUsers`.
- Web service calls API with a Cloud Run identity token in `X-Serverless-Authorization`.
- Browser Firebase ID token remains in `Authorization` and is still verified by the API application.

Current code foundation:

- `apps/web/server.mjs` fetches an identity token from the Cloud Run metadata server.
- The token audience is the API origin.
- The proxy sends it as `X-Serverless-Authorization` so the browser Firebase `Authorization` header remains untouched.
- `scripts/deploy-cloud-run.sh` and `.github/workflows/deploy-cloud-run.yml` deploy the API with `--no-allow-unauthenticated`, grant `roles/run.invoker` to the web service account, and remove stale `allUsers` invoker bindings.
- CI smoke checks now expect direct unauthenticated API access to return `403` and validate `/api/workspace/config` through the public web proxy.

Verified:

- API revision: `vertex-ai-api-00012-mlc`, serving 100% traffic.
- Web revision: `vertex-ai-web-00012-8m4`, serving 100% traffic.
- Direct unauthenticated API `/health/live` returns 403.
- Web `/` returns 200.
- Web `/api/workspace/config` returns 200 with provider catalog.
- Authenticated chat through the web proxy streams successfully.

## P0 Test Coverage

Status: endpoint-level coverage expanded; Firestore-backed integration tests still pending.

Implemented regression coverage:

- Gemini safety policy splits normal-user and admin thresholds.
- Chat orchestration passes server-authenticated user context to user-aware model clients.
- Admin quota usage endpoint requires admin and returns daily usage query parameters.
- Export endpoints require authentication, enforce conversation ownership, and include attachment metadata.
- Conversation delete endpoint requires authentication and dispatches deletion with the authenticated owner.

Remaining:

- Firebase-token integration tests around authenticated API endpoints.
- Firestore-backed integration coverage for actual conversation ownership queries, export hydration, attachment deletion, and message delete cascades.
- Live verification that Firestore composite indexes from `firestore.indexes.json` are in `READY` state after deployment.

## P0 Firestore Indexes

Status: deployment automation implemented and live readiness verified on 2026-06-21.

- Source of truth: `firestore.indexes.json`.
- Manual deploy: `FIRESTORE_PROJECT_ID=... node scripts/deploy-firestore-indexes.mjs`.
- Scripted deploy: `scripts/deploy-cloud-run.sh` runs the index deployment before building Cloud Run images.
- CI deploy: `.github/workflows/deploy-cloud-run.yml` runs the same script before deploying services.
- The script treats already-existing indexes as success so repeated deploys remain safe.

Verified:

- Target Firestore project: `my-agent-app-a5e42`.
- All three required composite indexes are `READY`.
