#!/usr/bin/env bash
set -euo pipefail

UPDATE_CONFIG=false
case "${1:-}" in
  "") ;;
  --update-config) UPDATE_CONFIG=true ;;
  --help|-h)
    echo "Usage: PROJECT_ID=project bash scripts/deploy-cloud-run.sh [--update-config]"
    echo "Existing services: rebuild and update images only. --update-config reapplies deployment settings."
    exit 0 ;;
  *) echo "[ERROR] Unknown option: $1" >&2; exit 1 ;;
esac
if (( $# > 1 )); then
  echo "[ERROR] Too many arguments." >&2
  exit 1
fi
cd "$(dirname "${BASH_SOURCE[0]}")/.."

PROJECT_ID="${PROJECT_ID:-}"
REGION="${REGION:-asia-east1}"
REPOSITORY="${REPOSITORY:-vertex-ai}"
API_SERVICE="${API_SERVICE:-vertex-ai-api}"
WEB_SERVICE="${WEB_SERVICE:-vertex-ai-web}"
SERVICE_ACCOUNT="${SERVICE_ACCOUNT:-}"
WEB_SERVICE_ACCOUNT="${WEB_SERVICE_ACCOUNT:-}"
WEB_INVOKER_SERVICE_ACCOUNT="${WEB_INVOKER_SERVICE_ACCOUNT:-}"
ATTACHMENT_STORAGE_ROLE="${ATTACHMENT_STORAGE_ROLE:-roles/storage.objectUser}"
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
WEB_TIMEOUT="${WEB_TIMEOUT:-300s}"
WEB_MIN_INSTANCES="${WEB_MIN_INSTANCES:-0}"
WEB_MAX_INSTANCES="${WEB_MAX_INSTANCES:-5}"
# The v2 run routes stay off until you choose otherwise.
RUNS_ENABLED="${RUNS_ENABLED:-false}"

FIREBASE_PROJECT_ID="${FIREBASE_PROJECT_ID:-$PROJECT_ID}"
FIRESTORE_PROJECT_ID="${FIRESTORE_PROJECT_ID:-$FIREBASE_PROJECT_ID}"
FIREBASE_API_KEY="${FIREBASE_API_KEY:-}"
FIREBASE_AUTH_DOMAIN="${FIREBASE_AUTH_DOMAIN:-$FIREBASE_PROJECT_ID.firebaseapp.com}"
FIREBASE_APP_ID="${FIREBASE_APP_ID:-}"
DEFAULT_PROVIDER_ID="${DEFAULT_PROVIDER_ID:-gemini}"
ATTACHMENT_STORAGE_BUCKET="${ATTACHMENT_STORAGE_BUCKET:-${PROJECT_ID}-vertex-ai-attachments}"

if [[ "$RUNS_ENABLED" != "true" && "$RUNS_ENABLED" != "false" ]]; then
  echo "[ERROR] RUNS_ENABLED must be true or false." >&2
  exit 1
fi

if [[ -z "$PROJECT_ID" ]]; then
  echo "[ERROR] PROJECT_ID is required, for example PROJECT_ID=my-gcp-project"
  exit 1
fi

# Lookup failures must not be mistaken for missing services.
SERVICES="$(gcloud run services list --project="$PROJECT_ID" --region="$REGION" --format='value(metadata.name)')"
API_EXISTS=false
WEB_EXISTS=false
while IFS= read -r service; do
  [[ "$service" != "$API_SERVICE" ]] || API_EXISTS=true
  [[ "$service" != "$WEB_SERVICE" ]] || WEB_EXISTS=true
done <<< "$SERVICES"

if [[ "$UPDATE_CONFIG" == false && "$API_EXISTS" == true && "$WEB_EXISTS" == true ]]; then
  echo "[INFO] Image-only deployment: preserving environment variables, secrets, identities, IAM and resource settings."
  for target in api web; do
    if [[ "$target" == api ]]; then
      context=VertexAI
      service="$API_SERVICE"
    else
      context=apps/web
      service="$WEB_SERVICE"
    fi
    image="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/vertex-ai-${target}:${IMAGE_TAG}"
    gcloud builds submit "$context" --project="$PROJECT_ID" --tag="$image"
    gcloud run services update "$service" --project="$PROJECT_ID" --region="$REGION" --image="$image"
  done
  for service in "$API_SERVICE" "$WEB_SERVICE"; do
    gcloud run services describe "$service" --project="$PROJECT_ID" --region="$REGION" --format='value(status.url)'
  done
  exit 0
fi

if [[ "$UPDATE_CONFIG" == false && ( "$API_EXISTS" == true || "$WEB_EXISTS" == true ) ]]; then
  echo "[ERROR] Only one service exists. Supply the initial configuration and use --update-config to resume safely." >&2
  exit 1
fi

if [[ -z "$SERVICE_ACCOUNT" ]]; then
  echo "[ERROR] SERVICE_ACCOUNT is required, for example SERVICE_ACCOUNT=cloud-run-runtime@${PROJECT_ID}.iam.gserviceaccount.com"
  exit 1
fi

WEB_SERVICE_ACCOUNT="${WEB_SERVICE_ACCOUNT:-${WEB_INVOKER_SERVICE_ACCOUNT:-$SERVICE_ACCOUNT}}"
WEB_INVOKER_SERVICE_ACCOUNT="${WEB_INVOKER_SERVICE_ACCOUNT:-$WEB_SERVICE_ACCOUNT}"

if [[ -z "$FIREBASE_PROJECT_ID" || -z "$FIRESTORE_PROJECT_ID" ]]; then
  echo "[ERROR] FIREBASE_PROJECT_ID and FIRESTORE_PROJECT_ID are required."
  exit 1
fi

if [[ -z "$FIREBASE_API_KEY" || -z "$FIREBASE_APP_ID" ]]; then
  echo "[ERROR] FIREBASE_API_KEY and FIREBASE_APP_ID are required."
  exit 1
fi

# A generation streams through the web proxy to the API, so the smaller of the two request timeouts is
# the real limit. The API ends a run itself 30 seconds before that, so a timeout is reported as a timeout
# (partial output saved) instead of looking like a dropped connection.
timeout_seconds() {
  local value="${1%s}"
  if [[ ! "$value" =~ ^[0-9]+$ ]]; then
    echo "[ERROR] Unsupported timeout '$1'; use whole seconds, for example 300s." >&2
    exit 1
  fi
  echo "$value"
}
API_TIMEOUT_SECONDS="$(timeout_seconds "$API_TIMEOUT")"
WEB_TIMEOUT_SECONDS="$(timeout_seconds "$WEB_TIMEOUT")"
RUN_MAX_DURATION_SECONDS=$(( (API_TIMEOUT_SECONDS < WEB_TIMEOUT_SECONDS ? API_TIMEOUT_SECONDS : WEB_TIMEOUT_SECONDS) - 30 ))
if (( RUN_MAX_DURATION_SECONDS < 60 )); then
  echo "[ERROR] WEB_TIMEOUT=${WEB_TIMEOUT} and API_TIMEOUT=${API_TIMEOUT} leave only ${RUN_MAX_DURATION_SECONDS}s for a generation (minimum 60s). Raise the smaller timeout, for example WEB_TIMEOUT=300s." >&2
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

echo "[INFO] Ensuring Firestore composite indexes in ${FIRESTORE_PROJECT_ID}..."
FIRESTORE_PROJECT_ID="$FIRESTORE_PROJECT_ID" node scripts/deploy-firestore-indexes.mjs

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
  --update-env-vars="PROJECT_ID=${PROJECT_ID},VertexAI__ProjectId=${PROJECT_ID},FIREBASE_PROJECT_ID=${FIREBASE_PROJECT_ID},FIRESTORE_PROJECT_ID=${FIRESTORE_PROJECT_ID},Firebase__ProjectId=${FIREBASE_PROJECT_ID},Firebase__ApiKey=${FIREBASE_API_KEY},Firebase__AuthDomain=${FIREBASE_AUTH_DOMAIN},Firebase__AppId=${FIREBASE_APP_ID},Workspace__DefaultProviderId=${DEFAULT_PROVIDER_ID},ATTACHMENT_STORAGE_BUCKET=${ATTACHMENT_STORAGE_BUCKET},Persistence__AttachmentStorageBucket=${ATTACHMENT_STORAGE_BUCKET},Runs__MaxDurationSeconds=${RUN_MAX_DURATION_SECONDS},Runs__Enabled=${RUNS_ENABLED}"

gcloud run services add-iam-policy-binding "$API_SERVICE" \
  --project="$PROJECT_ID" \
  --region="$REGION" \
  --member="serviceAccount:${WEB_INVOKER_SERVICE_ACCOUNT}" \
  --role="roles/run.invoker" >/dev/null

gcloud run services remove-iam-policy-binding "$API_SERVICE" \
  --project="$PROJECT_ID" \
  --region="$REGION" \
  --member="allUsers" \
  --role="roles/run.invoker" >/dev/null 2>&1 || true

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
  --service-account="$WEB_SERVICE_ACCOUNT" \
  --allow-unauthenticated \
  --port=8080 \
  --cpu="$WEB_CPU" \
  --memory="$WEB_MEMORY" \
  --concurrency="$WEB_CONCURRENCY" \
  --timeout="$WEB_TIMEOUT" \
  --min-instances="$WEB_MIN_INSTANCES" \
  --max-instances="$WEB_MAX_INSTANCES" \
  --update-env-vars="BACKEND_URL=${API_URL}"

WEB_URL="$(gcloud run services describe "$WEB_SERVICE" \
  --project="$PROJECT_ID" \
  --region="$REGION" \
  --format='value(status.url)')"

echo "[INFO] API URL: ${API_URL}"
echo "[INFO] Web URL: ${WEB_URL}"
echo "[INFO] Smoke checks:"
echo "curl -i ${API_URL}/health/live    # expected: 403 direct unauthenticated"
echo "curl -i ${WEB_URL}/api/workspace/config    # expected: 200 through web proxy"
