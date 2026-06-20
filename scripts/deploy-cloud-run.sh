#!/usr/bin/env bash
set -euo pipefail

PROJECT_ID="${PROJECT_ID:-}"
REGION="${REGION:-asia-east1}"
REPOSITORY="${REPOSITORY:-vertex-ai}"
API_SERVICE="${API_SERVICE:-vertex-ai-api}"
WEB_SERVICE="${WEB_SERVICE:-vertex-ai-web}"
SERVICE_ACCOUNT="${SERVICE_ACCOUNT:-}"
WEB_INVOKER_SERVICE_ACCOUNT="${WEB_INVOKER_SERVICE_ACCOUNT:-151587524132-compute@developer.gserviceaccount.com}"
ATTACHMENT_STORAGE_ROLE="${ATTACHMENT_STORAGE_ROLE:-projects/${PROJECT_ID}/roles/vertexAiAttachmentObjectUser}"
IMAGE_TAG="${IMAGE_TAG:-$(git rev-parse --short=12 HEAD 2>/dev/null || date +%Y%m%d%H%M%S)}"
API_CPU="${API_CPU:-1}"
API_MEMORY="${API_MEMORY:-1Gi}"
API_CONCURRENCY="${API_CONCURRENCY:-20}"
API_TIMEOUT="${API_TIMEOUT:-300s}"
API_MIN_INSTANCES="${API_MIN_INSTANCES:-0}"
API_MAX_INSTANCES="${API_MAX_INSTANCES:-10}"
WEB_CPU="${WEB_CPU:-1}"
WEB_MEMORY="${WEB_MEMORY:-512Mi}"
WEB_CONCURRENCY="${WEB_CONCURRENCY:-80}"
WEB_TIMEOUT="${WEB_TIMEOUT:-60s}"
WEB_MIN_INSTANCES="${WEB_MIN_INSTANCES:-0}"
WEB_MAX_INSTANCES="${WEB_MAX_INSTANCES:-5}"

FIREBASE_PROJECT_ID="${FIREBASE_PROJECT_ID:-$PROJECT_ID}"
FIRESTORE_PROJECT_ID="${FIRESTORE_PROJECT_ID:-$FIREBASE_PROJECT_ID}"
FIREBASE_API_KEY="${FIREBASE_API_KEY:-}"
FIREBASE_AUTH_DOMAIN="${FIREBASE_AUTH_DOMAIN:-$FIREBASE_PROJECT_ID.firebaseapp.com}"
FIREBASE_APP_ID="${FIREBASE_APP_ID:-}"
DEFAULT_PROVIDER_ID="${DEFAULT_PROVIDER_ID:-gemini}"
ATTACHMENT_STORAGE_BUCKET="${ATTACHMENT_STORAGE_BUCKET:-${PROJECT_ID}-vertex-ai-attachments}"

if [[ -z "$PROJECT_ID" ]]; then
  echo "[ERROR] PROJECT_ID is required, for example PROJECT_ID=my-gcp-project"
  exit 1
fi

if [[ -z "$SERVICE_ACCOUNT" ]]; then
  echo "[ERROR] SERVICE_ACCOUNT is required, for example SERVICE_ACCOUNT=cloud-run-runtime@${PROJECT_ID}.iam.gserviceaccount.com"
  exit 1
fi

if [[ -z "$FIREBASE_PROJECT_ID" || -z "$FIRESTORE_PROJECT_ID" ]]; then
  echo "[ERROR] FIREBASE_PROJECT_ID and FIRESTORE_PROJECT_ID are required."
  exit 1
fi

if [[ -z "$FIREBASE_API_KEY" || -z "$FIREBASE_APP_ID" ]]; then
  echo "[ERROR] FIREBASE_API_KEY and FIREBASE_APP_ID are required."
  exit 1
fi

API_IMAGE="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/vertex-ai-api:${IMAGE_TAG}"
WEB_IMAGE="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/vertex-ai-web:${IMAGE_TAG}"

echo "[INFO] Ensuring Artifact Registry repository ${REPOSITORY} in ${REGION}..."
if ! gcloud artifacts repositories describe "$REPOSITORY" \
  --project="$PROJECT_ID" \
  --location="$REGION" >/dev/null 2>&1; then
  gcloud artifacts repositories create "$REPOSITORY" \
    --project="$PROJECT_ID" \
    --location="$REGION" \
    --repository-format=docker \
    --description="Vertex AI workspace containers"
fi

echo "[INFO] Ensuring attachment storage bucket gs://${ATTACHMENT_STORAGE_BUCKET}..."
if ! gcloud storage buckets describe "gs://${ATTACHMENT_STORAGE_BUCKET}" >/dev/null 2>&1; then
  gcloud storage buckets create "gs://${ATTACHMENT_STORAGE_BUCKET}" \
    --project="$PROJECT_ID" \
    --location="$REGION" \
    --uniform-bucket-level-access
fi

echo "[INFO] Granting Cloud Run service account object access to gs://${ATTACHMENT_STORAGE_BUCKET}..."
gcloud storage buckets add-iam-policy-binding "gs://${ATTACHMENT_STORAGE_BUCKET}" \
  --member="serviceAccount:${SERVICE_ACCOUNT}" \
  --role="${ATTACHMENT_STORAGE_ROLE}" >/dev/null

echo "[INFO] Building API image ${API_IMAGE}..."
gcloud builds submit VertexAI \
  --project="$PROJECT_ID" \
  --tag="$API_IMAGE"

echo "[INFO] Deploying API service ${API_SERVICE}..."
gcloud run deploy "$API_SERVICE" \
  --project="$PROJECT_ID" \
  --region="$REGION" \
  --image="$API_IMAGE" \
  --service-account="$SERVICE_ACCOUNT" \
  --no-allow-unauthenticated \
  --port=8080 \
  --cpu="$API_CPU" \
  --memory="$API_MEMORY" \
  --concurrency="$API_CONCURRENCY" \
  --timeout="$API_TIMEOUT" \
  --min-instances="$API_MIN_INSTANCES" \
  --max-instances="$API_MAX_INSTANCES" \
  --set-env-vars="PROJECT_ID=${PROJECT_ID},VertexAI__ProjectId=${PROJECT_ID},FIREBASE_PROJECT_ID=${FIREBASE_PROJECT_ID},FIRESTORE_PROJECT_ID=${FIRESTORE_PROJECT_ID},Firebase__ProjectId=${FIREBASE_PROJECT_ID},Firebase__ApiKey=${FIREBASE_API_KEY},Firebase__AuthDomain=${FIREBASE_AUTH_DOMAIN},Firebase__AppId=${FIREBASE_APP_ID},Workspace__DefaultProviderId=${DEFAULT_PROVIDER_ID},ATTACHMENT_STORAGE_BUCKET=${ATTACHMENT_STORAGE_BUCKET},Persistence__AttachmentStorageBucket=${ATTACHMENT_STORAGE_BUCKET}"

gcloud run services add-iam-policy-binding "$API_SERVICE" \
  --project="$PROJECT_ID" \
  --region="$REGION" \
  --member="serviceAccount:${WEB_INVOKER_SERVICE_ACCOUNT}" \
  --role="roles/run.invoker" >/dev/null

API_URL="$(gcloud run services describe "$API_SERVICE" \
  --project="$PROJECT_ID" \
  --region="$REGION" \
  --format='value(status.url)')"

echo "[INFO] Building web image ${WEB_IMAGE}..."
gcloud builds submit apps/web \
  --project="$PROJECT_ID" \
  --tag="$WEB_IMAGE"

echo "[INFO] Deploying web service ${WEB_SERVICE}..."
gcloud run deploy "$WEB_SERVICE" \
  --project="$PROJECT_ID" \
  --region="$REGION" \
  --image="$WEB_IMAGE" \
  --allow-unauthenticated \
  --port=8080 \
  --cpu="$WEB_CPU" \
  --memory="$WEB_MEMORY" \
  --concurrency="$WEB_CONCURRENCY" \
  --timeout="$WEB_TIMEOUT" \
  --min-instances="$WEB_MIN_INSTANCES" \
  --max-instances="$WEB_MAX_INSTANCES" \
  --set-env-vars="BACKEND_URL=${API_URL}"

WEB_URL="$(gcloud run services describe "$WEB_SERVICE" \
  --project="$PROJECT_ID" \
  --region="$REGION" \
  --format='value(status.url)')"

echo "[INFO] API URL: ${API_URL}"
echo "[INFO] Web URL: ${WEB_URL}"
echo "[INFO] Smoke checks:"
echo "curl -i ${API_URL}/health/live"
echo "curl -i ${API_URL}/health/ready"
