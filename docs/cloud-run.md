# Cloud Run Deployment

This project runs as two containers:

- API: ASP.NET Core service in `VertexAI/`.
- Web: standalone Node static/proxy service in `apps/web/`.

The API is Firebase Auth + Firestore only. It does not need PostgreSQL, cookies, SMTP, or local session storage.

## Prerequisites

- Firebase Authentication enabled.
- Cloud Firestore enabled.
- Vertex AI enabled.
- Firestore composite indexes deployed from `firestore.indexes.json`.
- A runtime service account with these roles on the relevant projects:
  - Firebase Auth token verification through Firebase Admin credentials.
  - Firestore read/write access, for example `roles/datastore.user`.
  - Vertex AI access, for example `roles/aiplatform.user`.

If Firebase/Firestore and Vertex AI live in different Google Cloud projects, grant the Cloud Run runtime service account access to both projects.

## Firestore Indexes

Deploy indexes before routing traffic:

```bash
gcloud firestore indexes composite create \
  --project=my-agent-app-a5e42 \
  --collection-group=conversations \
  --query-scope=COLLECTION \
  --field-config=field-path=uid,order=ASCENDING \
  --field-config=field-path=updatedAt,order=DESCENDING

gcloud firestore indexes composite create \
  --project=my-agent-app-a5e42 \
  --collection-group=messages \
  --query-scope=COLLECTION \
  --field-config=field-path=uid,order=ASCENDING \
  --field-config=field-path=conversationId,order=ASCENDING \
  --field-config=field-path=createdAt,order=ASCENDING

gcloud firestore indexes composite create \
  --project=my-agent-app-a5e42 \
  --collection-group=messages \
  --query-scope=COLLECTION \
  --field-config=field-path=uid,order=ASCENDING \
  --field-config=field-path=conversationId,order=ASCENDING \
  --field-config=field-path=createdAt,order=DESCENDING
```

Check readiness:

```bash
gcloud firestore indexes composite list \
  --project=my-agent-app-a5e42 \
  --format='table(name.segment(-1),queryScope,state,fields[].fieldPath,fields[].order)'
```

## API Service

Build and push the API image:

```bash
gcloud builds submit VertexAI \
  --project=copper-affinity-467409-k7 \
  --tag=REGION-docker.pkg.dev/copper-affinity-467409-k7/REPOSITORY/vertex-ai-api:latest
```

Deploy:

```bash
gcloud run deploy vertex-ai-api \
  --project=copper-affinity-467409-k7 \
  --region=REGION \
  --image=REGION-docker.pkg.dev/copper-affinity-467409-k7/REPOSITORY/vertex-ai-api:latest \
  --service-account=SERVICE_ACCOUNT_EMAIL \
  --allow-unauthenticated \
  --set-env-vars=PROJECT_ID=copper-affinity-467409-k7,VertexAI__ProjectId=copper-affinity-467409-k7,FIREBASE_PROJECT_ID=my-agent-app-a5e42,FIRESTORE_PROJECT_ID=my-agent-app-a5e42,Firebase__ProjectId=my-agent-app-a5e42,Firebase__ApiKey=FIREBASE_WEB_API_KEY,Firebase__AuthDomain=my-agent-app-a5e42.firebaseapp.com,Firebase__AppId=FIREBASE_WEB_APP_ID,Workspace__DefaultProviderId=gemini
```

Cloud Run provides `PORT`; the API image respects it automatically.

## Web Service

Build and push the web image:

```bash
gcloud builds submit apps/web \
  --project=copper-affinity-467409-k7 \
  --tag=REGION-docker.pkg.dev/copper-affinity-467409-k7/REPOSITORY/vertex-ai-web:latest
```

Deploy the web service after the API service URL is known:

```bash
gcloud run deploy vertex-ai-web \
  --project=copper-affinity-467409-k7 \
  --region=REGION \
  --image=REGION-docker.pkg.dev/copper-affinity-467409-k7/REPOSITORY/vertex-ai-web:latest \
  --allow-unauthenticated \
  --set-env-vars=BACKEND_URL=https://vertex-ai-api-xxxxx-REGION.a.run.app
```

## Smoke Checks

```bash
curl -i https://API_URL/health/live
curl -i https://API_URL/health/ready
curl -i https://WEB_URL/
```

Then sign in through the web service and send a short chat message. A successful request should create:

- `users/{uid}`
- `conversations/{conversationId}`
- `messages/{messageId}` for the user message
- `messages/{messageId}` for the assistant message
