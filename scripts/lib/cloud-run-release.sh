#!/usr/bin/env bash
# shellcheck shell=bash
#
# Release helpers shared by scripts/deploy-cloud-run.sh and .github/workflows/deploy-cloud-run.yml.
#
# Safety properties (covered by scripts/test/cloud-run-release.test.sh with a fake gcloud):
#   1. Configuration is MERGED (--update-env-vars / --update-secrets), never replaced, so anything
#      configured on the service by other means survives a deploy.
#   2. On an existing service a new revision is deployed with --no-traffic; the service keeps serving
#      the old revision until the whole release is promoted.
#   3. After promotion a smoke check runs; if it fails every service that was promoted is rolled back
#      to the revision that was serving before this release.
#   4. Nothing here ever prints or stores secret values: secrets are referenced by Secret Manager name.
#
# Required environment: PROJECT_ID REGION REPOSITORY API_SERVICE WEB_SERVICE RUNTIME_SERVICE_ACCOUNT
#   WEB_SERVICE_ACCOUNT WEB_INVOKER_SERVICE_ACCOUNT IMAGE_TAG FIREBASE_PROJECT_ID FIRESTORE_PROJECT_ID
#   FIREBASE_API_KEY FIREBASE_AUTH_DOMAIN FIREBASE_APP_ID DEFAULT_PROVIDER_ID ATTACHMENT_STORAGE_BUCKET
#   API_CPU API_MEMORY API_CONCURRENCY API_TIMEOUT API_MIN_INSTANCES API_MAX_INSTANCES
#   WEB_CPU WEB_MEMORY WEB_CONCURRENCY WEB_TIMEOUT WEB_MIN_INSTANCES WEB_MAX_INSTANCES
# Optional: DEPLOY_TARGET (all|api|web, default all), API_SECRETS (see docs/secrets.md),
#   ACCESS_ALLOWED_EMAILS ACCESS_ALLOWED_EMAIL_DOMAINS ACCESS_ALLOWED_UIDS ACCESS_REQUIRE_VERIFIED_EMAIL
#   QUOTA_GLOBAL_DAILY_REQUEST_LIMIT QUOTA_GLOBAL_DAILY_TOKEN_LIMIT QUOTA_GLOBAL_DAILY_SEARCH_LIMIT

crr_log() { echo "[release] $*"; }

# Name of the revision currently receiving traffic ("" when the service does not exist yet).
crr_serving_revision() {
  gcloud run services describe "$1" \
    --project="$PROJECT_ID" --region="$REGION" \
    --format='value(status.traffic[0].revisionName)' 2>/dev/null || true
}

crr_service_url() {
  gcloud run services describe "$1" \
    --project="$PROJECT_ID" --region="$REGION" \
    --format='value(status.url)'
}

# Non-secret API settings as a gcloud env list using '#' as the delimiter, so values may contain commas
# (allow-lists). Optional settings are only emitted when set, so an unset variable never wipes a value
# that was configured elsewhere.
crr_api_env_vars() {
  local out="" pair
  local pairs=(
    "PROJECT_ID=${PROJECT_ID}"
    "VertexAI__ProjectId=${PROJECT_ID}"
    "FIREBASE_PROJECT_ID=${FIREBASE_PROJECT_ID}"
    "FIRESTORE_PROJECT_ID=${FIRESTORE_PROJECT_ID}"
    "Firebase__ProjectId=${FIREBASE_PROJECT_ID}"
    "Firebase__ApiKey=${FIREBASE_API_KEY}"
    "Firebase__AuthDomain=${FIREBASE_AUTH_DOMAIN}"
    "Firebase__AppId=${FIREBASE_APP_ID}"
    "Workspace__DefaultProviderId=${DEFAULT_PROVIDER_ID}"
    "ATTACHMENT_STORAGE_BUCKET=${ATTACHMENT_STORAGE_BUCKET}"
    "Persistence__AttachmentStorageBucket=${ATTACHMENT_STORAGE_BUCKET}"
  )
  [[ -n "${ACCESS_ALLOWED_EMAILS:-}" ]] && pairs+=("Access__AllowedEmailsCsv=${ACCESS_ALLOWED_EMAILS}")
  [[ -n "${ACCESS_ALLOWED_EMAIL_DOMAINS:-}" ]] && pairs+=("Access__AllowedEmailDomainsCsv=${ACCESS_ALLOWED_EMAIL_DOMAINS}")
  [[ -n "${ACCESS_ALLOWED_UIDS:-}" ]] && pairs+=("Access__AllowedUidsCsv=${ACCESS_ALLOWED_UIDS}")
  [[ -n "${ACCESS_REQUIRE_VERIFIED_EMAIL:-}" ]] && pairs+=("Access__RequireVerifiedEmail=${ACCESS_REQUIRE_VERIFIED_EMAIL}")
  [[ -n "${QUOTA_GLOBAL_DAILY_REQUEST_LIMIT:-}" ]] && pairs+=("Quota__GlobalDailyRequestLimit=${QUOTA_GLOBAL_DAILY_REQUEST_LIMIT}")
  [[ -n "${QUOTA_GLOBAL_DAILY_TOKEN_LIMIT:-}" ]] && pairs+=("Quota__GlobalDailyTokenLimit=${QUOTA_GLOBAL_DAILY_TOKEN_LIMIT}")
  [[ -n "${QUOTA_GLOBAL_DAILY_SEARCH_LIMIT:-}" ]] && pairs+=("Quota__GlobalDailySearchLimit=${QUOTA_GLOBAL_DAILY_SEARCH_LIMIT}")

  for pair in "${pairs[@]}"; do
    out+="${out:+#}${pair}"
  done
  printf '^#^%s' "$out"
}

crr_build_image() { # $1 = source dir, $2 = image
  gcloud builds submit "$1" --project="$PROJECT_ID" --tag="$2"
}

# Deploy without sending traffic when the service already serves a revision (keeps the old one live).
crr_traffic_flags() { # $1 = previous revision
  if [[ -n "$1" ]]; then
    echo "--no-traffic --tag=candidate"
  fi
}

crr_deploy_api() {
  local image="$1" flags
  flags="$(crr_traffic_flags "${PREV_API_REVISION:-}")"
  local secret_args=()
  [[ -n "${API_SECRETS:-}" ]] && secret_args=("--update-secrets=${API_SECRETS}")

  # shellcheck disable=SC2086  # $flags is a deliberate, fixed word list
  gcloud run deploy "$API_SERVICE" \
    --project="$PROJECT_ID" --region="$REGION" \
    --image="$image" \
    --service-account="$RUNTIME_SERVICE_ACCOUNT" \
    --no-allow-unauthenticated \
    --port=8080 \
    --cpu="$API_CPU" --memory="$API_MEMORY" \
    --concurrency="$API_CONCURRENCY" --timeout="$API_TIMEOUT" \
    --min-instances="$API_MIN_INSTANCES" --max-instances="$API_MAX_INSTANCES" \
    $flags \
    --update-env-vars="$(crr_api_env_vars)" \
    ${secret_args[@]+"${secret_args[@]}"}
}

crr_deploy_web() {
  local image="$1" api_url="$2" flags
  flags="$(crr_traffic_flags "${PREV_WEB_REVISION:-}")"

  # shellcheck disable=SC2086
  gcloud run deploy "$WEB_SERVICE" \
    --project="$PROJECT_ID" --region="$REGION" \
    --image="$image" \
    --service-account="$WEB_SERVICE_ACCOUNT" \
    --allow-unauthenticated \
    --port=8080 \
    --cpu="$WEB_CPU" --memory="$WEB_MEMORY" \
    --concurrency="$WEB_CONCURRENCY" --timeout="$WEB_TIMEOUT" \
    --min-instances="$WEB_MIN_INSTANCES" --max-instances="$WEB_MAX_INSTANCES" \
    $flags \
    --update-env-vars="BACKEND_URL=${api_url}"
}

# Only the web service account may invoke the private API; remove any public access.
crr_ensure_api_access() {
  gcloud run services add-iam-policy-binding "$API_SERVICE" \
    --project="$PROJECT_ID" --region="$REGION" \
    --member="serviceAccount:${WEB_INVOKER_SERVICE_ACCOUNT}" \
    --role="roles/run.invoker" >/dev/null
  gcloud run services remove-iam-policy-binding "$API_SERVICE" \
    --project="$PROJECT_ID" --region="$REGION" \
    --member="allUsers" --role="roles/run.invoker" >/dev/null 2>&1 || true
}

crr_promote() { # $1 = service
  gcloud run services update-traffic "$1" \
    --project="$PROJECT_ID" --region="$REGION" --to-latest
  gcloud run services update-traffic "$1" \
    --project="$PROJECT_ID" --region="$REGION" --remove-tags=candidate >/dev/null 2>&1 || true
}

crr_rollback() { # $1 = service, $2 = revision that was serving before the release
  if [[ -z "$2" ]]; then
    crr_log "No previous revision recorded for $1; nothing to roll back to."
    return 0
  fi
  crr_log "Rolling $1 back to revision $2"
  gcloud run services update-traffic "$1" \
    --project="$PROJECT_ID" --region="$REGION" --to-revisions="$2=100"
}

crr_http_status() { # overridable in tests
  curl -sS -o /dev/null -m 30 -w '%{http_code}' "$1" || echo 000
}

# Succeeds only when the private API refuses direct anonymous calls and the public site works.
crr_smoke() { # $1 = api url, $2 = web url
  local api_status web_status config_status
  api_status="$(crr_http_status "$1/health/live")"
  web_status="$(crr_http_status "$2/")"
  config_status="$(crr_http_status "$2/api/workspace/config")"
  crr_log "smoke: direct-api=${api_status} (want 403) web=${web_status} (want 200) config=${config_status} (want 200)"
  [[ "$api_status" == "403" && "$web_status" == "200" && "$config_status" == "200" ]]
}

# The new traffic assignment can take a few seconds to reach every front end, so retry before failing.
crr_smoke_with_retry() {
  local attempts="${CRR_SMOKE_RETRIES:-3}" delay="${CRR_SMOKE_DELAY:-5}" i
  for ((i = 1; i <= attempts; i++)); do
    if crr_smoke "$1" "$2"; then return 0; fi
    if (( i < attempts )); then sleep "$delay"; fi
  done
  return 1
}

# "300s" -> 300. Only whole seconds are used by this project.
crr_seconds() { echo "${1%s}"; }

# The web proxy sits in front of the API: if it times out first, long generations are cut mid-stream.
crr_validate_timeouts() {
  local api web
  api="$(crr_seconds "$API_TIMEOUT")"
  web="$(crr_seconds "$WEB_TIMEOUT")"
  if (( web < api )); then
    crr_log "WEB_TIMEOUT (${WEB_TIMEOUT}) must be >= API_TIMEOUT (${API_TIMEOUT}); streaming replies would be cut off."
    return 1
  fi
}

crr_release() {
  crr_validate_timeouts || return 1
  local target="${DEPLOY_TARGET:-all}" api_image web_image api_url web_url
  local promoted=()

  PREV_API_REVISION="$(crr_serving_revision "$API_SERVICE")"
  PREV_WEB_REVISION="$(crr_serving_revision "$WEB_SERVICE")"
  crr_log "serving before release: api=${PREV_API_REVISION:-none} web=${PREV_WEB_REVISION:-none}"

  api_image="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/vertex-ai-api:${IMAGE_TAG}"
  web_image="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/vertex-ai-web:${IMAGE_TAG}"

  # Phase 1: build and deploy candidates. A failure here leaves the live revisions untouched.
  if [[ "$target" == "all" || "$target" == "api" ]]; then
    crr_build_image VertexAI "$api_image" || return 1
    crr_deploy_api "$api_image" || return 1
    crr_ensure_api_access || return 1
  fi
  api_url="$(crr_service_url "$API_SERVICE")" || return 1

  if [[ "$target" == "all" || "$target" == "web" ]]; then
    crr_build_image apps/web "$web_image" || return 1
    crr_deploy_web "$web_image" "$api_url" || return 1
  fi

  # Phase 2: promote, then verify; undo everything promoted in this run if anything goes wrong.
  local failed=0
  if [[ "$target" == "all" || "$target" == "api" ]]; then
    crr_promote "$API_SERVICE" && promoted+=("api") || failed=1
  fi
  if [[ "$failed" == 0 && ( "$target" == "all" || "$target" == "web" ) ]]; then
    crr_promote "$WEB_SERVICE" && promoted+=("web") || failed=1
  fi

  web_url="$(crr_service_url "$WEB_SERVICE")" || failed=1
  if [[ "$failed" == 0 ]] && ! crr_smoke_with_retry "$api_url" "$web_url"; then
    failed=1
  fi

  if [[ "$failed" != 0 ]]; then
    crr_log "Release failed verification; rolling back."
    local item
    for item in "${promoted[@]+"${promoted[@]}"}"; do
      if [[ "$item" == "api" ]]; then crr_rollback "$API_SERVICE" "${PREV_API_REVISION:-}"; fi
      if [[ "$item" == "web" ]]; then crr_rollback "$WEB_SERVICE" "${PREV_WEB_REVISION:-}"; fi
    done
    return 1
  fi

  crr_log "API URL: ${api_url}"
  crr_log "Web URL: ${web_url}"
}
