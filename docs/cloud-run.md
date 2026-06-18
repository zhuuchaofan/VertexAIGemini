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

## Scripted Deployment

The repository includes `scripts/deploy-cloud-run.sh`. It creates the Artifact Registry repository if missing, builds both images with Cloud Build, deploys the API service, reads its URL, then deploys the web service with `BACKEND_URL` pointing at the API.

```bash
SERVICE_ACCOUNT=cloud-run-runtime@your-gcp-project-id.iam.gserviceaccount.com \
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
API_SERVICE=vertex-ai-api
WEB_SERVICE=vertex-ai-web
FIREBASE_PROJECT_ID=your-firebase-project-id
FIRESTORE_PROJECT_ID=your-firebase-project-id
FIREBASE_AUTH_DOMAIN=your-firebase-project-id.firebaseapp.com
DEFAULT_PROVIDER_ID=gemini
```

After the web service URL is created, add its host to Firebase Authentication
authorized domains. Without that Firebase setting, browser login can fail even
when the Cloud Run services are healthy.

## Firestore Indexes

Deploy indexes before routing traffic:

```bash
gcloud firestore indexes composite create \
  --project=your-firebase-project-id \
  --collection-group=conversations \
  --query-scope=COLLECTION \
  --field-config=field-path=uid,order=ASCENDING \
  --field-config=field-path=updatedAt,order=DESCENDING

gcloud firestore indexes composite create \
  --project=your-firebase-project-id \
  --collection-group=messages \
  --query-scope=COLLECTION \
  --field-config=field-path=uid,order=ASCENDING \
  --field-config=field-path=conversationId,order=ASCENDING \
  --field-config=field-path=createdAt,order=ASCENDING

gcloud firestore indexes composite create \
  --project=your-firebase-project-id \
  --collection-group=messages \
  --query-scope=COLLECTION \
  --field-config=field-path=uid,order=ASCENDING \
  --field-config=field-path=conversationId,order=ASCENDING \
  --field-config=field-path=createdAt,order=DESCENDING
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
  --tag=REGION-docker.pkg.dev/your-gcp-project-id/REPOSITORY/vertex-ai-api:latest
```

Deploy:

```bash
gcloud run deploy vertex-ai-api \
  --project=your-gcp-project-id \
  --region=REGION \
  --image=REGION-docker.pkg.dev/your-gcp-project-id/REPOSITORY/vertex-ai-api:latest \
  --service-account=SERVICE_ACCOUNT_EMAIL \
  --allow-unauthenticated \
  --port=8080 \
  --set-env-vars=PROJECT_ID=your-gcp-project-id,VertexAI__ProjectId=your-gcp-project-id,FIREBASE_PROJECT_ID=your-firebase-project-id,FIRESTORE_PROJECT_ID=your-firebase-project-id,Firebase__ProjectId=your-firebase-project-id,Firebase__ApiKey=FIREBASE_WEB_API_KEY,Firebase__AuthDomain=your-firebase-project-id.firebaseapp.com,Firebase__AppId=FIREBASE_WEB_APP_ID,Workspace__DefaultProviderId=gemini
```

Cloud Run provides `PORT`; the API image respects it automatically.
The image defaults to port `8080`, while local Docker Compose overrides the
port to `8880` for the API container.

## Web Service

Build and push the web image:

```bash
gcloud builds submit apps/web \
  --project=your-gcp-project-id \
  --tag=REGION-docker.pkg.dev/your-gcp-project-id/REPOSITORY/vertex-ai-web:latest
```

Deploy the web service after the API service URL is known:

```bash
gcloud run deploy vertex-ai-web \
  --project=your-gcp-project-id \
  --region=REGION \
  --image=REGION-docker.pkg.dev/your-gcp-project-id/REPOSITORY/vertex-ai-web:latest \
  --allow-unauthenticated \
  --port=8080 \
  --set-env-vars=BACKEND_URL=https://vertex-ai-api-xxxxx-REGION.a.run.app
```

Cloud Run serves the web container on port `8080`. Local Docker Compose
overrides the same image to run the web server on port `5173` inside the Docker
network.

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
