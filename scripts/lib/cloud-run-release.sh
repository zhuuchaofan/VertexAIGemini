#!/usr/bin/env bash
# shellcheck shell=bash
#
# Release helpers shared by scripts/deploy-cloud-run.sh and .github/workflows/deploy-cloud-run.yml.
#
# TWO MODES, ONE PROCEDURE FOR BOTH ENVIRONMENTS (CRR_ENV = staging | production, default staging):
#
#   CRR_MODE=prepare   (DEFAULT)  build the image, deploy candidate revisions with NO traffic, test them
#                                 through their private tag URLs. It never moves traffic, never changes
#                                 IAM in production, and never creates a production service. Safe to run
#                                 for acceptance; nothing here can reach users.
#   CRR_MODE=promote              switch traffic to what `prepare` produced. It builds and deploys nothing.
#                                 It refuses to start unless CRR_CONFIRM_PROMOTE is exactly
#                                 "promote:<env>:<IMAGE_TAG>" (bound to the environment and the commit),
#                                 and for production also needs CRR_ACCEPTANCE_REF (the staging acceptance
#                                 run for the same commit). It promotes only a candidate whose image is the
#                                 confirmed commit.
#
# Staging is a separate set of services (names end in -staging) with its own data, attachment bucket and
# Firebase project; the isolation is verified before anything runs. A brand-new service cannot be
# protected by an older revision: its first deploy takes traffic and there is nothing to roll back to.
# That is acceptable for staging and is refused for production (production services must already exist).
#
# Safety properties (covered by scripts/test/cloud-run-release.test.sh with a stateful fake gcloud):
#   1. Configuration is MERGED (--update-env-vars / --update-secrets), never replaced, so anything
#      configured on the service by other means survives a deploy.
#   2. On an existing service a new revision is deployed with --no-traffic; the service keeps serving
#      the old revision until the whole release is promoted. Deploy-time flags that rewrite the
#      SERVICE-level IAM policy (--allow/--no-allow-unauthenticated) are used on first deploy only.
#   3. Nothing that is NOT undone by a traffic rollback changes before the candidates have passed their
#      checks. The only IAM change the release makes (granting the web service account run.invoker on the
#      API when it is missing) happens right before promotion and is removed again on rollback. A public
#      API (allUsers) is never "fixed" silently: the release stops and tells the operator.
#   4. Candidates are exercised through their 0%-traffic tag URLs BEFORE any traffic moves. If the private
#      API candidate cannot be exercised (no identity token) the release STOPS, unless the operator
#      explicitly accepts the risk with CRR_ALLOW_UNVERIFIED_API_CANDIDATE=true.
#   5. A traffic switch only counts when the service is really serving the new revision afterwards
#      (checked, not assumed). A failed or unverifiable switch fails the release; the smoke test can
#      never "pass" against the old revision and be mistaken for a successful release.
#   6. After promotion a smoke check runs; on any failure every service whose switch was ATTEMPTED is put
#      back on the revision that served before, and the result is verified.
#   7. Timeouts are layered with clean-up margins (see crr_validate_timeouts).
#   8. A revision that carries the local-only auth bypass (DISABLE_SERVERLESS_AUTH) is never promoted.
#   9. Nothing here ever prints or stores secret values: secrets are referenced by Secret Manager name.
#
# Required environment: PROJECT_ID REGION REPOSITORY API_SERVICE WEB_SERVICE RUNTIME_SERVICE_ACCOUNT
#   WEB_SERVICE_ACCOUNT WEB_INVOKER_SERVICE_ACCOUNT IMAGE_TAG FIREBASE_PROJECT_ID FIRESTORE_PROJECT_ID
#   FIREBASE_API_KEY FIREBASE_AUTH_DOMAIN FIREBASE_APP_ID DEFAULT_PROVIDER_ID ATTACHMENT_STORAGE_BUCKET
#   API_CPU API_MEMORY API_CONCURRENCY API_TIMEOUT API_MIN_INSTANCES API_MAX_INSTANCES
#   WEB_CPU WEB_MEMORY WEB_CONCURRENCY WEB_TIMEOUT WEB_MIN_INSTANCES WEB_MAX_INSTANCES
# Optional: DEPLOY_TARGET (all|api|web, default all), API_SECRETS (see docs/secrets.md),
#   CRR_IDENTITY_TOKEN_SA (service account to impersonate for the private API candidate check; the deploy
#   identity needs roles/iam.serviceAccountTokenCreator on it and the SA needs roles/run.invoker on the API),
#   CRR_ALLOW_UNVERIFIED_API_CANDIDATE (true = accept promoting an API candidate that could not be tested),
#   CHAT_MAX_GENERATION_SECONDS (default 240),
#   ACCESS_ALLOWED_EMAILS ACCESS_ALLOWED_EMAIL_DOMAINS ACCESS_ALLOWED_UIDS ACCESS_REQUIRE_VERIFIED_EMAIL
#   QUOTA_GLOBAL_DAILY_REQUEST_LIMIT QUOTA_GLOBAL_DAILY_TOKEN_LIMIT QUOTA_GLOBAL_DAILY_SEARCH_LIMIT

crr_log() { echo "[release] $*"; }

# Name of the revision currently receiving 100% of traffic ("" when the service does not exist yet).
# Parsed from JSON because the order of status.traffic entries (tagged 0% entries) is not guaranteed.
crr_serving_revision() {
  local spec
  spec="$(gcloud run services describe "$1" --project="$PROJECT_ID" --region="$REGION" --format=json 2>/dev/null || true)"
  [[ -z "$spec" ]] && return 0
  python3 - "$spec" <<'PY' 2>/dev/null || true
import json, sys
try:
    traffic = json.loads(sys.argv[1]).get("status", {}).get("traffic", [])
except Exception:
    sys.exit(0)
best = next((t for t in traffic if t.get("percent") == 100 and t.get("revisionName")), None) \
    or next((t for t in traffic if t.get("percent", 0) > 0 and t.get("revisionName")), None)
print(best["revisionName"] if best else "")
PY
}

crr_latest_ready_revision() {
  gcloud run services describe "$1" --project="$PROJECT_ID" --region="$REGION" \
    --format='value(status.latestReadyRevisionName)' 2>/dev/null || true
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
    "Chat__MaxGenerationSeconds=${CHAT_MAX_GENERATION_SECONDS:-240}"
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

# --allow-unauthenticated / --no-allow-unauthenticated rewrite the SERVICE-level IAM policy immediately,
# even together with --no-traffic, and a traffic rollback does not undo them. They are therefore used
# only when the service is created; an existing service keeps the access policy it has.
crr_access_flag() { # $1 = previous revision, $2 = flag for a new service
  if [[ -z "$1" ]]; then
    echo "$2"
  fi
}

crr_deploy_api() {
  local image="$1" flags access
  flags="$(crr_traffic_flags "${PREV_API_REVISION:-}")"
  access="$(crr_access_flag "${PREV_API_REVISION:-}" --no-allow-unauthenticated)"
  local secret_args=()
  [[ -n "${API_SECRETS:-}" ]] && secret_args=("--update-secrets=${API_SECRETS}")

  # shellcheck disable=SC2086  # $flags / $access are deliberate, fixed word lists
  gcloud run deploy "$API_SERVICE" \
    --project="$PROJECT_ID" --region="$REGION" \
    --image="$image" \
    --service-account="$RUNTIME_SERVICE_ACCOUNT" \
    $access \
    --port=8080 \
    --cpu="$API_CPU" --memory="$API_MEMORY" \
    --concurrency="$API_CONCURRENCY" --timeout="$API_TIMEOUT" \
    --min-instances="$API_MIN_INSTANCES" --max-instances="$API_MAX_INSTANCES" \
    $flags \
    --update-env-vars="$(crr_api_env_vars)" \
    ${secret_args[@]+"${secret_args[@]}"}
}

crr_deploy_web() {
  local image="$1" api_url="$2" flags access
  flags="$(crr_traffic_flags "${PREV_WEB_REVISION:-}")"
  access="$(crr_access_flag "${PREV_WEB_REVISION:-}" --allow-unauthenticated)"

  # shellcheck disable=SC2086
  gcloud run deploy "$WEB_SERVICE" \
    --project="$PROJECT_ID" --region="$REGION" \
    --image="$image" \
    --service-account="$WEB_SERVICE_ACCOUNT" \
    $access \
    --port=8080 \
    --cpu="$WEB_CPU" --memory="$WEB_MEMORY" \
    --concurrency="$WEB_CONCURRENCY" --timeout="$WEB_TIMEOUT" \
    --min-instances="$WEB_MIN_INSTANCES" --max-instances="$WEB_MAX_INSTANCES" \
    $flags \
    --update-env-vars="^#^BACKEND_URL=${api_url}#UPSTREAM_TIMEOUT_MS=$(crr_upstream_timeout_ms)"
}

# --- infrastructure the release depends on (all additive and idempotent) ---------------------------------
# These run BEFORE any candidate is tested and are NOT undone by a traffic rollback, so they only ever
# create what is missing and never rewrite what already exists.

# Grants the API runtime service account object access on the attachment bucket, only if it lacks it.
# (The old code re-applied the binding on every run.)
crr_ensure_bucket_access() {
  local present
  present="$(gcloud storage buckets get-iam-policy "gs://${ATTACHMENT_STORAGE_BUCKET}" \
    --flatten=bindings --filter="bindings.role=${ATTACHMENT_STORAGE_ROLE} AND bindings.members:serviceAccount:${RUNTIME_SERVICE_ACCOUNT}" \
    --format='value(bindings.role)' 2>/dev/null || true)"
  if [[ -n "$present" ]]; then
    crr_log "bucket access already present for ${RUNTIME_SERVICE_ACCOUNT}; leaving it alone."
    return 0
  fi
  crr_log "Granting ${ATTACHMENT_STORAGE_ROLE} on gs://${ATTACHMENT_STORAGE_BUCKET} to ${RUNTIME_SERVICE_ACCOUNT} (missing)."
  gcloud storage buckets add-iam-policy-binding "gs://${ATTACHMENT_STORAGE_BUCKET}" \
    --member="serviceAccount:${RUNTIME_SERVICE_ACCOUNT}" \
    --role="${ATTACHMENT_STORAGE_ROLE}" >/dev/null
}

# --- API access: read-only preflight, then one additive, tracked change right before promotion ----------

# Read-only. A public API (allUsers / allAuthenticatedUsers) is a security finding the release must not
# paper over: removing the grant is an immediate, un-rollbackable change to production access.
crr_preflight_api_access() {
  local public
  public="$(gcloud run services get-iam-policy "$API_SERVICE" --project="$PROJECT_ID" --region="$REGION" \
    --flatten=bindings --filter="bindings.members:allUsers OR bindings.members:allAuthenticatedUsers" \
    --format='value(bindings.role)' 2>/dev/null || true)"
  if [[ -n "$public" ]]; then
    crr_log "The API service grants access to allUsers/allAuthenticatedUsers. Remove it deliberately, then release again:"
    crr_log "  gcloud run services remove-iam-policy-binding ${API_SERVICE} --region=${REGION} --member=allUsers --role=roles/run.invoker"
    return 1
  fi
}

crr_api_invoker_present() {
  local found
  found="$(gcloud run services get-iam-policy "$API_SERVICE" --project="$PROJECT_ID" --region="$REGION" \
    --flatten=bindings --filter="bindings.role=roles/run.invoker AND bindings.members:serviceAccount:${WEB_INVOKER_SERVICE_ACCOUNT}" \
    --format='value(bindings.role)' 2>/dev/null || true)"
  [[ -n "$found" ]]
}

# Additive only, and only when missing. Remembered so a rollback can take it away again.
crr_grant_web_invoker() {
  IAM_GRANTED_BY_RELEASE=0
  if crr_api_invoker_present; then
    return 0
  fi
  crr_log "Granting roles/run.invoker on ${API_SERVICE} to ${WEB_INVOKER_SERVICE_ACCOUNT} (missing)."
  gcloud run services add-iam-policy-binding "$API_SERVICE" \
    --project="$PROJECT_ID" --region="$REGION" \
    --member="serviceAccount:${WEB_INVOKER_SERVICE_ACCOUNT}" \
    --role="roles/run.invoker" >/dev/null || return 1
  IAM_GRANTED_BY_RELEASE=1
}

crr_revoke_web_invoker_if_granted() {
  if [[ "${IAM_GRANTED_BY_RELEASE:-0}" == "1" ]]; then
    crr_log "Removing the invoker grant this release added."
    gcloud run services remove-iam-policy-binding "$API_SERVICE" \
      --project="$PROJECT_ID" --region="$REGION" \
      --member="serviceAccount:${WEB_INVOKER_SERVICE_ACCOUNT}" \
      --role="roles/run.invoker" >/dev/null 2>&1 || crr_log "WARNING: could not remove the grant; remove it by hand if it is unwanted."
    IAM_GRANTED_BY_RELEASE=0
  fi
}

# --- traffic -------------------------------------------------------------------------------------------

# A failed switch MUST fail the function. Removing the (cosmetic) candidate tag is best effort and must
# never be the command whose exit status the caller sees.
crr_promote() { # $1 = service
  if ! gcloud run services update-traffic "$1" \
      --project="$PROJECT_ID" --region="$REGION" --to-latest; then
    crr_log "Traffic switch FAILED for $1."
    return 1
  fi
  gcloud run services update-traffic "$1" \
    --project="$PROJECT_ID" --region="$REGION" --remove-tags=candidate >/dev/null 2>&1 \
    || crr_log "note: could not remove the candidate tag from $1 (harmless)."
  return 0
}

# Trust but verify: a zero exit status is not proof that the new revision serves traffic.
crr_verify_serving_latest() { # $1 = service
  local latest serving
  latest="$(crr_latest_ready_revision "$1")"
  serving="$(crr_serving_revision "$1")"
  if [[ -z "$latest" || "$latest" != "$serving" ]]; then
    crr_log "$1 is serving '${serving:-none}' but the new revision is '${latest:-none}'; the switch did not take effect."
    return 1
  fi
}

crr_rollback() { # $1 = service, $2 = revision that was serving before the release
  if [[ -z "$2" ]]; then
    crr_log "No previous revision recorded for $1; nothing to roll back to."
    return 0
  fi
  crr_log "Rolling $1 back to revision $2"
  if ! gcloud run services update-traffic "$1" \
      --project="$PROJECT_ID" --region="$REGION" --to-revisions="$2=100"; then
    crr_log "ROLLBACK FAILED for $1. Run by hand: gcloud run services update-traffic $1 --region=${REGION} --to-revisions=$2=100"
    return 1
  fi
  local now
  now="$(crr_serving_revision "$1")"
  if [[ "$now" != "$2" ]]; then
    crr_log "ROLLBACK NOT CONFIRMED for $1 (serving '${now:-none}', wanted '$2'). Check it by hand."
    return 1
  fi
}

crr_http_status() { # url [bearer token]; overridable in tests
  if [[ -n "${2:-}" ]]; then
    curl -sS -o /dev/null -m 30 -w '%{http_code}' -H "Authorization: Bearer $2" "$1" || echo 000
  else
    curl -sS -o /dev/null -m 30 -w '%{http_code}' "$1" || echo 000
  fi
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

# Timeout layering, innermost first. Each outer layer must outlive the inner one by a clean-up margin so
# the inner layer can finish its own work (save the partial reply, settle quota, send the final event)
# instead of being cut off by the layer around it:
#
#   generation cap (app)      CHAT_MAX_GENERATION_SECONDS  default 240
#   API Cloud Run timeout     API_TIMEOUT                  default 300   >= cap + 30
#   web proxy upstream cap    UPSTREAM_TIMEOUT_MS          = WEB_TIMEOUT - 15 s
#   web Cloud Run timeout     WEB_TIMEOUT                  default 330   >= API_TIMEOUT + 30
crr_validate_timeouts() {
  local api web gen
  api="$(crr_seconds "$API_TIMEOUT")"
  web="$(crr_seconds "$WEB_TIMEOUT")"
  gen="${CHAT_MAX_GENERATION_SECONDS:-240}"
  if (( gen > api - 30 )); then
    crr_log "CHAT_MAX_GENERATION_SECONDS (${gen}) must be <= API_TIMEOUT (${API_TIMEOUT}) - 30s so the API can save and settle before the platform cuts the request."
    return 1
  fi
  if (( web < api + 30 )); then
    crr_log "WEB_TIMEOUT (${WEB_TIMEOUT}) must be >= API_TIMEOUT (${API_TIMEOUT}) + 30s; otherwise the web layer cuts streams the API is still finishing."
    return 1
  fi
}

crr_upstream_timeout_ms() { echo $(( ($(crr_seconds "$WEB_TIMEOUT") - 15) * 1000 )); }

# API_SECRETS must be Secret Manager references (ENV_NAME=secret-name:version), never secret values.
crr_validate_secrets() {
  [[ -z "${API_SECRETS:-}" ]] && return 0
  local entry
  IFS=',' read -ra entries <<< "$API_SECRETS"
  for entry in "${entries[@]}"; do
    if ! [[ "$entry" =~ ^[A-Z_][A-Z0-9_]*=[A-Za-z0-9_-]+:(latest|[0-9]+)$ ]]; then
      # Deliberately do not print the entry: if someone pasted a real key here it must not reach the log.
      crr_log "API_SECRETS has an entry that is not of the form ENV_NAME=secret-name:version. Use Secret Manager references, never values."
      return 1
    fi
  done
}

# The proxy's local-only switch that skips the service-to-service identity token must never exist in
# production, whether set by this script's environment or by hand on the service.
crr_assert_no_auth_bypass() {
  if [[ "${DISABLE_SERVERLESS_AUTH:-}" == "true" ]]; then
    crr_log "DISABLE_SERVERLESS_AUTH is set in the deploy environment; it is for local development only."
    return 1
  fi
  local spec
  spec="$(gcloud run services describe "$WEB_SERVICE" --project="$PROJECT_ID" --region="$REGION" --format=json 2>/dev/null || true)"
  if grep -q '"DISABLE_SERVERLESS_AUTH"' <<< "$spec"; then
    crr_log "The web service has DISABLE_SERVERLESS_AUTH configured. Remove it before releasing: gcloud run services update ${WEB_SERVICE} --remove-env-vars=DISABLE_SERVERLESS_AUTH"
    return 1
  fi
}

crr_warn_open_access() {
  if [[ -z "${ACCESS_ALLOWED_EMAILS:-}${ACCESS_ALLOWED_EMAIL_DOMAINS:-}${ACCESS_ALLOWED_UIDS:-}${ACCESS_REQUIRE_VERIFIED_EMAIL:-}" ]]; then
    crr_log "WARNING: no access policy variable is set. Settings already on the service are kept (merge), but if none exist registration is OPEN and only the daily quotas limit spend."
  fi
}

# Tag URL of the 0%-traffic candidate: the service host with "candidate---" in front.
crr_candidate_url() { # $1 = service url
  echo "${1/https:\/\//https://candidate---}"
}

# ID token for calling the PRIVATE api candidate. Federated (WIF) credentials cannot mint ID tokens
# directly, so CI sets CRR_IDENTITY_TOKEN_SA to a service account it may impersonate. Overridable in tests.
crr_identity_token() { # $1 = audience (the API service URL)
  if [[ -n "${CRR_IDENTITY_TOKEN_SA:-}" ]]; then
    gcloud auth print-identity-token --impersonate-service-account="$CRR_IDENTITY_TOKEN_SA" --audiences="$1" 2>/dev/null || true
  else
    gcloud auth print-identity-token --audiences="$1" 2>/dev/null || true
  fi
}

crr_retry() { # attempts delay cmd...
  local attempts="$1" delay="$2" i
  shift 2
  for ((i = 1; i <= attempts; i++)); do
    if "$@"; then return 0; fi
    if (( i < attempts )); then sleep "$delay"; fi
  done
  return 1
}

crr_expect_status() { # url want [token]
  local got
  got="$(crr_http_status "$1" "${3:-}")"
  crr_log "candidate check: ${1#https://} -> ${got} (want $2)"
  [[ "$got" == "$2" ]]
}

# Exercise the web candidate through its tag URL. Its /api/workspace/config goes through the proxy to the
# API, so a 200 proves the proxy -> API hop (including the service-to-service identity token).
crr_check_web_candidate() { # $1 = web service url
  local attempts="${CRR_SMOKE_RETRIES:-3}" delay="${CRR_SMOKE_DELAY:-5}" web_candidate
  web_candidate="$(crr_candidate_url "$1")"
  crr_retry "$attempts" "$delay" crr_expect_status "${web_candidate}/" 200 || return 1
  crr_retry "$attempts" "$delay" crr_expect_status "${web_candidate}/api/workspace/config" 200 || return 1
}

# Exercise the PRIVATE api candidate. Per the Cloud Run rule the token audience is the SERVICE url, also when
# the request goes to a tagged revision URL.
crr_check_api_candidate() { # $1 = api service url
  local attempts="${CRR_SMOKE_RETRIES:-3}" delay="${CRR_SMOKE_DELAY:-5}" token
  token="$(crr_identity_token "$1")"
  if [[ -z "$token" ]]; then
    if [[ "${CRR_ALLOW_UNVERIFIED_API_CANDIDATE:-}" == "true" ]]; then
      crr_log "WARNING: no identity token; the API candidate was NOT exercised (CRR_ALLOW_UNVERIFIED_API_CANDIDATE=true). Only the post-promotion smoke test and rollback protect production."
      return 0
    fi
    crr_log "Cannot exercise the private API candidate: no identity token. Stopping before any traffic moves."
    crr_log "Fix: set CRR_IDENTITY_TOKEN_SA to a service account the deploy identity may impersonate"
    crr_log "     (roles/iam.serviceAccountTokenCreator on it, roles/run.invoker for it on ${API_SERVICE}),"
    crr_log "     or accept the risk explicitly with CRR_ALLOW_UNVERIFIED_API_CANDIDATE=true."
    return 1
  fi
  crr_retry "$attempts" "$delay" crr_expect_status "$(crr_candidate_url "$1")/health/ready" 200 "$token"
}

# Candidate smoke test: "is it up and wired together". It is NOT business acceptance (real model calls,
# attachments, thinking, search, cancel, data saved) - that is scripts/acceptance/staging-acceptance.mjs.
crr_check_candidates() { # $1 = api url, $2 = web url
  local target="${DEPLOY_TARGET:-all}"
  if [[ -n "${PREV_WEB_REVISION:-}" && ( "$target" == "all" || "$target" == "web" ) ]]; then
    crr_check_web_candidate "$2" || return 1
  fi
  if [[ -n "${PREV_API_REVISION:-}" && ( "$target" == "all" || "$target" == "api" ) ]]; then
    crr_check_api_candidate "$1" || return 1
  fi
}

# --- modes, environments, confirmation ------------------------------------------------------------------

crr_env()  { echo "${CRR_ENV:-staging}"; }
crr_mode() { echo "${CRR_MODE:-prepare}"; }

crr_validate_mode_and_env() {
  case "$(crr_mode)" in
    prepare|promote) ;;
    *) crr_log "Unknown CRR_MODE '$(crr_mode)'. Use 'prepare' (default) or 'promote'."; return 1 ;;
  esac
  case "$(crr_env)" in
    staging|production) ;;
    *) crr_log "Unknown CRR_ENV '$(crr_env)'. Use 'staging' (default) or 'production'."; return 1 ;;
  esac
}

# Staging must be unmistakably separate from production: service names, attachment bucket, Firestore and
# Firebase project. This is what stops "staging" tests from touching real users' data.
crr_validate_environment_isolation() {
  if [[ "$(crr_env)" == "staging" ]]; then
    if [[ "$API_SERVICE" != *-staging || "$WEB_SERVICE" != *-staging ]]; then
      crr_log "Staging service names must end with '-staging' (got api='${API_SERVICE}', web='${WEB_SERVICE}'), so they can never be mistaken for production."
      return 1
    fi
    if [[ "$ATTACHMENT_STORAGE_BUCKET" != *staging* ]]; then
      crr_log "The staging attachment bucket name must contain 'staging' (got '${ATTACHMENT_STORAGE_BUCKET}')."
      return 1
    fi
    if [[ -n "${PRODUCTION_FIRESTORE_PROJECT_ID:-}" && "$FIRESTORE_PROJECT_ID" == "$PRODUCTION_FIRESTORE_PROJECT_ID" ]]; then
      crr_log "Staging would use the PRODUCTION Firestore project (${FIRESTORE_PROJECT_ID}). Refusing: staging must have its own data."
      return 1
    fi
    if [[ -n "${PRODUCTION_FIREBASE_PROJECT_ID:-}" && "$FIREBASE_PROJECT_ID" == "$PRODUCTION_FIREBASE_PROJECT_ID" ]]; then
      crr_log "Staging would use the PRODUCTION Firebase project (${FIREBASE_PROJECT_ID}), i.e. real users' accounts. Refusing."
      return 1
    fi
    if [[ -n "${PRODUCTION_ATTACHMENT_STORAGE_BUCKET:-}" && "$ATTACHMENT_STORAGE_BUCKET" == "$PRODUCTION_ATTACHMENT_STORAGE_BUCKET" ]]; then
      crr_log "Staging would use the PRODUCTION attachment bucket. Refusing."
      return 1
    fi
  else
    if [[ "$API_SERVICE" == *-staging || "$WEB_SERVICE" == *-staging || "$ATTACHMENT_STORAGE_BUCKET" == *staging* ]]; then
      crr_log "A production release was pointed at a staging-looking resource (api='${API_SERVICE}', web='${WEB_SERVICE}', bucket='${ATTACHMENT_STORAGE_BUCKET}'). Refusing."
      return 1
    fi
  fi
}

crr_expected_confirmation() { echo "promote:$(crr_env):${IMAGE_TAG}"; }

# Checked BEFORE any gcloud call, so a missing or wrong confirmation changes nothing.
crr_validate_promote_authorization() {
  if [[ "${CRR_CONFIRM_PROMOTE:-}" != "$(crr_expected_confirmation)" ]]; then
    crr_log "Promotion needs explicit confirmation. Set CRR_CONFIRM_PROMOTE to exactly:"
    crr_log "  $(crr_expected_confirmation)"
    crr_log "(it names the environment and the commit, so a confirmation for one release cannot approve another.)"
    return 1
  fi
  if [[ "$(crr_env)" == "production" && -z "${CRR_ACCEPTANCE_REF:-}" ]]; then
    crr_log "A production promotion needs CRR_ACCEPTANCE_REF: the link or ID of the staging acceptance run that passed for this commit."
    return 1
  fi
}

crr_revision_image() {
  gcloud run revisions describe "$1" --project="$PROJECT_ID" --region="$REGION" \
    --format='value(spec.containers[0].image)' 2>/dev/null || true
}

# absent | mismatch | live | prepared  (for the commit named by IMAGE_TAG)
crr_candidate_state() { # $1 = service
  local serving latest image
  serving="$(crr_serving_revision "$1")"
  [[ -z "$serving" ]] && { echo absent; return 0; }
  latest="$(crr_latest_ready_revision "$1")"
  image="$(crr_revision_image "$latest")"
  if [[ -z "$latest" || "$image" != *":${IMAGE_TAG}"* ]]; then echo mismatch; return 0; fi
  if [[ "$latest" == "$serving" ]]; then echo live; else echo prepared; fi
}

crr_common_validation() {
  crr_validate_mode_and_env || return 1
  crr_validate_environment_isolation || return 1
  crr_validate_timeouts || return 1
  crr_validate_secrets || return 1
  command -v python3 >/dev/null || { crr_log "python3 is required (used to read the serving revision)."; return 1; }
}

# --- PREPARE: build, deploy candidates, smoke-test them. Never moves traffic. -----------------------------

crr_run_prepare() {
  local target="${DEPLOY_TARGET:-all}" api_image web_image api_url web_url
  local do_api=0 do_web=0 env
  env="$(crr_env)"
  [[ "$target" == "all" || "$target" == "api" ]] && do_api=1
  [[ "$target" == "all" || "$target" == "web" ]] && do_web=1

  crr_assert_no_auth_bypass || return 1
  crr_warn_open_access
  PREV_API_REVISION="$(crr_serving_revision "$API_SERVICE")"
  PREV_WEB_REVISION="$(crr_serving_revision "$WEB_SERVICE")"
  crr_log "mode=prepare env=${env}; serving now: api=${PREV_API_REVISION:-none} web=${PREV_WEB_REVISION:-none}"

  # A production service is never created by this tool: with nothing serving there is no candidate to
  # protect and no revision to roll back to.
  if [[ "$env" == "production" ]]; then
    if (( do_api )) && [[ -z "$PREV_API_REVISION" ]]; then crr_log "Production API service '${API_SERVICE}' does not exist. Refusing to create a production service."; return 1; fi
    if (( do_web )) && [[ -z "$PREV_WEB_REVISION" ]]; then crr_log "Production web service '${WEB_SERVICE}' does not exist. Refusing to create a production service."; return 1; fi
  fi

  api_image="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/vertex-ai-api:${IMAGE_TAG}"
  web_image="${REGION}-docker.pkg.dev/${PROJECT_ID}/${REPOSITORY}/vertex-ai-web:${IMAGE_TAG}"

  if (( do_api )) && [[ -n "$PREV_API_REVISION" ]]; then
    crr_preflight_api_access || return 1
  fi

  if (( do_api )); then
    crr_build_image VertexAI "$api_image" || return 1
    crr_deploy_api "$api_image" || return 1
  fi
  api_url="$(crr_service_url "$API_SERVICE")" || return 1
  if (( do_web )); then
    crr_build_image apps/web "$web_image" || return 1
    crr_deploy_web "$web_image" "$api_url" || return 1
  fi

  crr_assert_no_auth_bypass || return 1
  web_url="$(crr_service_url "$WEB_SERVICE")" || return 1

  # Staging only: let the new web talk to the API (an additive, staging-only grant). Production IAM is never
  # touched by prepare; it happens in promote, right before the traffic switch.
  if [[ "$env" == "staging" ]] && (( do_api )); then
    crr_grant_web_invoker || return 1
  fi

  if ! crr_check_candidates "$api_url" "$web_url"; then
    crr_log "Candidate failed its smoke checks. No traffic was moved and production access is unchanged."
    return 1
  fi

  crr_log "PREPARED (${env}). Smoke checks only - this is NOT business acceptance."
  if [[ -n "$PREV_WEB_REVISION" ]]; then
    crr_log "Candidate web: $(crr_candidate_url "$web_url")   Candidate api (private): $(crr_candidate_url "$api_url")"
  else
    crr_log "These ${env} services were just created and are already serving (no earlier revision exists, so there is nothing to roll back to)."
  fi
  crr_log "Next: run the acceptance suite, then promote with CRR_MODE=promote CRR_CONFIRM_PROMOTE=$(crr_expected_confirmation)"
  return 0
}

# --- PROMOTE: switch traffic to the prepared candidates, verify, roll back on failure. -------------------

crr_run_promote() {
  local target="${DEPLOY_TARGET:-all}" api_url web_url env
  local do_api=0 do_web=0 failed=0 attempted=()
  env="$(crr_env)"
  [[ "$target" == "all" || "$target" == "api" ]] && do_api=1
  [[ "$target" == "all" || "$target" == "web" ]] && do_web=1

  crr_assert_no_auth_bypass || return 1
  PREV_API_REVISION="$(crr_serving_revision "$API_SERVICE")"
  PREV_WEB_REVISION="$(crr_serving_revision "$WEB_SERVICE")"
  crr_log "mode=promote env=${env} commit=${IMAGE_TAG}; serving now: api=${PREV_API_REVISION:-none} web=${PREV_WEB_REVISION:-none}"

  # Only what `prepare` produced for THIS commit can be promoted.
  local api_state="n/a" web_state="n/a"
  if (( do_api )); then api_state="$(crr_candidate_state "$API_SERVICE")"; fi
  if (( do_web )); then web_state="$(crr_candidate_state "$WEB_SERVICE")"; fi
  local svc state
  for svc in "api:$api_state" "web:$web_state"; do
    state="${svc#*:}"
    case "$state" in
      prepared|live|n/a) ;;
      absent)   crr_log "The ${svc%%:*} service does not exist. Nothing to promote (promote never creates services)."; return 1 ;;
      mismatch) crr_log "The newest ${svc%%:*} revision was not built from commit ${IMAGE_TAG}. Run prepare for this commit first."; return 1 ;;
    esac
  done
  local need_api=0 need_web=0
  [[ "$api_state" == "prepared" ]] && need_api=1
  [[ "$web_state" == "prepared" ]] && need_web=1
  if (( need_api == 0 && need_web == 0 )); then
    crr_log "Nothing to promote: the selected service(s) already serve commit ${IMAGE_TAG}."
    return 0
  fi
  api_url="$(crr_service_url "$API_SERVICE")" || return 1
  web_url="$(crr_service_url "$WEB_SERVICE")" || return 1

  if (( do_api )) && [[ -n "$PREV_API_REVISION" ]]; then
    crr_preflight_api_access || return 1
  fi

  # Fresh smoke checks of the candidates right before the switch (they may have changed since prepare).
  if (( need_web )); then
    crr_check_web_candidate "$web_url" || { crr_log "Web candidate failed its smoke checks. No traffic was moved and no access was changed."; return 1; }
  fi
  if (( need_api )); then
    crr_check_api_candidate "$api_url" || { crr_log "API candidate failed its smoke checks. No traffic was moved and no access was changed."; return 1; }
  fi

  IAM_GRANTED_BY_RELEASE=0
  if (( need_api )); then
    crr_grant_web_invoker || { crr_log "Could not grant the web service account access to the API; nothing was promoted."; return 1; }
  fi

  if (( need_api )); then
    attempted+=("api")
    if ! crr_promote "$API_SERVICE" || ! crr_verify_serving_latest "$API_SERVICE"; then failed=1; fi
  fi

  # The web candidate still has no traffic but its proxy now reaches the NEW api: this is the first moment
  # the new web and the new api are exercised together, and it happens before the web switch.
  if (( failed == 0 && need_web && need_api )); then
    if ! crr_check_web_candidate "$web_url"; then
      crr_log "The web candidate does not work against the newly promoted API."
      failed=1
    fi
  fi

  if (( failed == 0 && need_web )); then
    attempted+=("web")
    if ! crr_promote "$WEB_SERVICE" || ! crr_verify_serving_latest "$WEB_SERVICE"; then failed=1; fi
  fi

  if (( failed == 0 )); then
    web_url="$(crr_service_url "$WEB_SERVICE")" || failed=1
  fi
  if (( failed == 0 )) && ! crr_smoke_with_retry "$api_url" "$web_url"; then
    failed=1
  fi

  if (( failed != 0 )); then
    crr_log "Promotion failed; rolling back every service whose traffic switch was attempted."
    local rollback_failed=0 i
    for (( i = ${#attempted[@]} - 1; i >= 0; i-- )); do
      if [[ "${attempted[i]}" == "web" ]]; then crr_rollback "$WEB_SERVICE" "${PREV_WEB_REVISION:-}" || rollback_failed=1; fi
      if [[ "${attempted[i]}" == "api" ]]; then crr_rollback "$API_SERVICE" "${PREV_API_REVISION:-}" || rollback_failed=1; fi
    done
    crr_revoke_web_invoker_if_granted
    if (( rollback_failed )); then
      crr_log "PRODUCTION MAY BE ON THE NEW REVISION. Follow the manual commands printed above."
    fi
    return 1
  fi

  crr_log "PROMOTED (${env}, commit ${IMAGE_TAG}). API URL: ${api_url}  Web URL: ${web_url}"
  [[ -n "${CRR_ACCEPTANCE_REF:-}" ]] && crr_log "Acceptance reference: ${CRR_ACCEPTANCE_REF}"
  return 0
}

# Entry point. The default is `prepare`: acceptance can never promote by accident.
crr_release() {
  crr_common_validation || return 1
  case "$(crr_mode)" in
    prepare)
      crr_run_prepare ;;
    promote)
      crr_validate_promote_authorization || return 1
      crr_run_promote ;;
  esac
}
