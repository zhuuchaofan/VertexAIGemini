#!/usr/bin/env bash
set -euo pipefail

PROJECT_ID="${PROJECT_ID:-}"
REGION="${REGION:-asia-east1}"
REPOSITORY="${REPOSITORY:-vertex-ai}"
API_SERVICE="${API_SERVICE:-vertex-ai-api}"
WEB_SERVICE="${WEB_SERVICE:-vertex-ai-web}"
SERVICE_ACCOUNT="${SERVICE_ACCOUNT:-}"

FIREBASE_PROJECT_ID="${FIREBASE_PROJECT_ID:-$PROJECT_ID}"
FIRESTORE_PROJECT_ID="${FIRESTORE_PROJECT_ID:-$FIREBASE_PROJECT_ID}"
FIREBASE_API_KEY="${FIREBASE_API_KEY:-}"
FIREBASE_AUTH_DOMAIN="${FIREBASE_AUTH_DOMAIN:-$FIREBASE_PROJECT_ID.firebaseapp.com}"
FIREBASE_APP_ID="${FIREBASE_APP_ID:-}"
DEFAULT_PROVIDER_ID="${DEFAULT_PROVIDER_ID:-gemini}"

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

API_IMAGE="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/vertex-ai-api:latest"
WEB_IMAGE="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/vertex-ai-web:latest"

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
  --allow-unauthenticated \
  --port=8080 \
  --set-env-vars="PROJECT_ID=${PROJECT_ID},VertexAI__ProjectId=${PROJECT_ID},FIREBASE_PROJECT_ID=${FIREBASE_PROJECT_ID},FIRESTORE_PROJECT_ID=${FIRESTORE_PROJECT_ID},Firebase__ProjectId=${FIREBASE_PROJECT_ID},Firebase__ApiKey=${FIREBASE_API_KEY},Firebase__AuthDomain=${FIREBASE_AUTH_DOMAIN},Firebase__AppId=${FIREBASE_APP_ID},Workspace__DefaultProviderId=${DEFAULT_PROVIDER_ID}"

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
