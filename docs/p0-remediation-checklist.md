# P0 Remediation Checklist

Last updated: 2026-06-20

## P0-1 Gemini Safety Policy

Status: planned policy split.

- Admin users: may disable Gemini safety settings for private/admin workflows.
- Normal users: should use a compliance safety profile by default.
- Role source: Firebase custom claim `admin=true`.
- Current code foundation: `AuthenticatedUser.IsAdmin` is populated from Firebase custom claims.
- Next implementation: add a runtime safety policy option that chooses `OFF` for admins and a compliance threshold for normal users.

## P0-2 User Budget And Quota

Status: implemented foundation.

Server-side daily quota is enforced before model calls and recorded in Firestore:

- Collection: `usageQuotas`
- Key shape: `{firebaseUid}_{yyyyMMdd}`
- Limits:
  - daily requests
  - daily estimated tokens
  - daily actual tokens
  - daily web searches
  - daily attachment bytes
- Admin bypass: enabled through Firebase custom claim `admin=true`.

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
- Add admin UI or ops command to inspect daily usage.
- Add alerting for aggregate daily spend.

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

Status: in progress; code foundation implemented.

Target architecture:

- Web Cloud Run service remains public.
- API Cloud Run service does not grant `roles/run.invoker` to `allUsers`.
- Web service calls API with a Cloud Run identity token in `X-Serverless-Authorization`.
- Browser Firebase ID token remains in `Authorization` and is still verified by the API application.

Current code foundation:

- `apps/web/server.mjs` fetches an identity token from the Cloud Run metadata server.
- The token audience is the API origin.
- The proxy sends it as `X-Serverless-Authorization` so the browser Firebase `Authorization` header remains untouched.

Implementation plan:

1. Deploy the updated web service.
2. Grant `roles/run.invoker` on the API service to the web runtime service account.
3. Remove public `allUsers` invoker from the API service.
4. Validate:
   - Web `/` returns 200.
   - Web `/api/workspace/config` returns 200.
   - Direct unauthenticated API request returns 403.
   - Authenticated browser chat still streams responses.
