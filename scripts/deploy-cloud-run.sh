#!/usr/bin/env bash
set -euo pipefail

PROJECT_ID="${PROJECT_ID:-}"
REGION="${REGION:-asia-east1}"
REPOSITORY="${REPOSITORY:-vertex-ai}"
API_SERVICE="${API_SERVICE:-vertex-ai-api}"
WEB_SERVICE="${WEB_SERVICE:-vertex-ai-web}"
SERVICE_ACCOUNT="${SERVICE_ACCOUNT:-}"
WEB_SERVICE_ACCOUNT="${WEB_SERVICE_ACCOUNT:-}"
WEB_INVOKER_SERVICE_ACCOUNT="${WEB_INVOKER_SERVICE_ACCOUNT:-}"
ATTACHMENT_STORAGE_ROLE="${ATTACHMENT_STORAGE_ROLE:-projects/${PROJECT_ID}/roles/vertexAiAttachmentObjectUser}"
IMAGE_TAG="${IMAGE_TAG:-$(git rev-parse --short=12 HEAD 2>/dev/null || date +%Y%m%d%H%M%S)}"
API_CPU="${API_CPU:-1}"
API_MEMORY="${API_MEMORY:-1Gi}"
API_CONCURRENCY="${API_CONCURRENCY:-20}"
API_TIMEOUT="${API_TIMEOUT:-300s}"
API_MIN_INSTANCES="${API_MIN_INSTANCES:-0}"
API_MAX_INSTANCES="${API_MAX_INSTANCES:-3}"
WEB_CPU="${WEB_CPU:-1}"
WEB_MEMORY="${WEB_MEMORY:-512Mi}"
WEB_CONCURRENCY="${WEB_CONCURRENCY:-80}"
WEB_TIMEOUT="${WEB_TIMEOUT:-330s}"
WEB_MIN_INSTANCES="${WEB_MIN_INSTANCES:-0}"
WEB_MAX_INSTANCES="${WEB_MAX_INSTANCES:-2}"

FIREBASE_PROJECT_ID="${FIREBASE_PROJECT_ID:-$PROJECT_ID}"
FIRESTORE_PROJECT_ID="${FIRESTORE_PROJECT_ID:-$FIREBASE_PROJECT_ID}"
FIREBASE_API_KEY="${FIREBASE_API_KEY:-}"
FIREBASE_AUTH_DOMAIN="${FIREBASE_AUTH_DOMAIN:-$FIREBASE_PROJECT_ID.firebaseapp.com}"
FIREBASE_APP_ID="${FIREBASE_APP_ID:-}"
DEFAULT_PROVIDER_ID="${DEFAULT_PROVIDER_ID:-gemini}"
DEPLOY_TARGET="${DEPLOY_TARGET:-all}"
API_SECRETS="${API_SECRETS:-}"
ATTACHMENT_STORAGE_BUCKET="${ATTACHMENT_STORAGE_BUCKET:-${PROJECT_ID}-vertex-ai-attachments}"

if [[ -z "$PROJECT_ID" ]]; then
  echo "[ERROR] PROJECT_ID is required, for example PROJECT_ID=my-gcp-project"
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

# Build, deploy as candidates, promote, smoke test, and roll back automatically if verification fails.
# See scripts/lib/cloud-run-release.sh for the safety properties (merge-only configuration, no-traffic
# deploys, automatic rollback).
export PROJECT_ID REGION REPOSITORY API_SERVICE WEB_SERVICE WEB_SERVICE_ACCOUNT WEB_INVOKER_SERVICE_ACCOUNT
export RUNTIME_SERVICE_ACCOUNT="$SERVICE_ACCOUNT" IMAGE_TAG DEPLOY_TARGET
export FIREBASE_PROJECT_ID FIRESTORE_PROJECT_ID FIREBASE_API_KEY FIREBASE_AUTH_DOMAIN FIREBASE_APP_ID
export DEFAULT_PROVIDER_ID ATTACHMENT_STORAGE_BUCKET
export API_CPU API_MEMORY API_CONCURRENCY API_TIMEOUT API_MIN_INSTANCES API_MAX_INSTANCES
export WEB_CPU WEB_MEMORY WEB_CONCURRENCY WEB_TIMEOUT WEB_MIN_INSTANCES WEB_MAX_INSTANCES
export CHAT_MAX_GENERATION_SECONDS="${CHAT_MAX_GENERATION_SECONDS:-240}"
export API_SECRETS ACCESS_ALLOWED_EMAILS ACCESS_ALLOWED_EMAIL_DOMAINS ACCESS_ALLOWED_UIDS ACCESS_REQUIRE_VERIFIED_EMAIL
export QUOTA_GLOBAL_DAILY_REQUEST_LIMIT QUOTA_GLOBAL_DAILY_TOKEN_LIMIT QUOTA_GLOBAL_DAILY_SEARCH_LIMIT

# shellcheck source=lib/cloud-run-release.sh
source "$(dirname "${BASH_SOURCE[0]}")/lib/cloud-run-release.sh"
crr_release
