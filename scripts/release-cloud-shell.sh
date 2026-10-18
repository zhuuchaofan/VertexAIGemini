#!/usr/bin/env bash
# Staged release of the new workspace to an EXISTING Cloud Run deployment, run from Cloud Shell.
#
# Every step is safe to run again. Nothing here asks for a value that Cloud Run already holds: service accounts,
# Firebase settings, model keys and Secret Manager references stay on the services; this script only adds the
# new settings and replaces the container images. Mutating steps print the commands they are about to run.
#
#   bash scripts/release-cloud-shell.sh plan          read-only: what exists, what will change
#   bash scripts/release-cloud-shell.sh backup        Firestore export + attachment copy (writes only to a backup bucket)
#   bash scripts/release-cloud-shell.sh indexes       create the Firestore indexes the new version needs, wait for READY
#   bash scripts/release-cloud-shell.sh build         Cloud Build both images, tagged with this commit
#   bash scripts/release-cloud-shell.sh deploy        API (v2 still off, same behaviour as before) + web as a no-traffic `next` revision
#   bash scripts/release-cloud-shell.sh enable-v2     turn the v2 run routes on (needs a recorded backup and READY indexes)
#   bash scripts/release-cloud-shell.sh verify        automated checks, no model calls
#   bash scripts/release-cloud-shell.sh promote       send all web traffic to the new revision (old UI still the default page)
#   bash scripts/release-cloud-shell.sh ui next|legacy   choose which UI "/" shows
#   bash scripts/release-cloud-shell.sh disable-v2    switch the v2 routes off again
#   bash scripts/release-cloud-shell.sh rollback      traffic back to the revisions recorded before the release
#   bash scripts/release-cloud-shell.sh status
#
# Settings (all optional; defaults are discovered):
#   PROJECT_ID  (default: gcloud's current project)   REGION (default asia-east1)
#   API_SERVICE (default vertex-ai-api)   WEB_SERVICE (default vertex-ai-web)
#   EXPECT_COMMIT   refuse to run unless HEAD starts with this
#   DRY_RUN=1       print mutating commands instead of running them
set -euo pipefail

cd "$(dirname "${BASH_SOURCE[0]}")/.."

COMMAND="${1:-}"
ARG="${2:-}"

REGION="${REGION:-asia-east1}"
API_SERVICE="${API_SERVICE:-vertex-ai-api}"
WEB_SERVICE="${WEB_SERVICE:-vertex-ai-web}"
WEB_TIMEOUT="${WEB_TIMEOUT:-300s}"
STATE_DIR="${RELEASE_STATE_DIR:-$HOME/vai-release}"
STATE_FILE="${STATE_DIR}/state.env"
DRY_RUN="${DRY_RUN:-0}"
TAG_NAME="next"

info() { echo "[INFO] $*"; }
warn() { echo "[WARN] $*" >&2; }
die() { echo "[ERROR] $*" >&2; exit 1; }

usage() {
  sed -n '2,23p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

case "${COMMAND}" in
  plan|backup|indexes|build|deploy|enable-v2|verify|promote|ui|disable-v2|rollback|status) ;;
  ""|--help|-h|help) usage; exit 0 ;;
  *) usage >&2; die "Unknown command: ${COMMAND}" ;;
esac

PROJECT_ID="${PROJECT_ID:-$(gcloud config get-value project 2>/dev/null || true)}"
[[ -n "${PROJECT_ID}" ]] || die "No project. Run: gcloud config set project <project-id>  (or export PROJECT_ID)"

mkdir -p "${STATE_DIR}"
touch "${STATE_FILE}"

# ---- helpers ---------------------------------------------------------------------------------------------

# Mutating gcloud calls go through here so DRY_RUN=1 shows them without running them.
mutate() {
  echo "+ $*"
  if [[ "${DRY_RUN}" == "1" ]]; then return 0; fi
  "$@"
}

state_get() { grep -E "^$1=" "${STATE_FILE}" | tail -1 | cut -d= -f2- || true; }
state_set() {
  [[ "${DRY_RUN}" == "1" ]] && return 0
  grep -vE "^$1=" "${STATE_FILE}" > "${STATE_FILE}.tmp" || true
  echo "$1=$2" >> "${STATE_FILE}.tmp"
  mv "${STATE_FILE}.tmp" "${STATE_FILE}"
}

service_json() {
  gcloud run services describe "$1" --project="${PROJECT_ID}" --region="${REGION}" --format=json
}

# json_field <json> <python expression over d>; prints nothing when the path does not exist.
json_field() {
  python3 -c '
import json, sys
d = json.loads(sys.argv[1])
def env(name, container=0):
    c = d["spec"]["template"]["spec"]["containers"][container]
    for item in c.get("env", []):
        if item.get("name") == name:
            return item.get("value", "(secret reference)" if "valueFrom" in item else "")
    return None
try:
    v = eval(sys.argv[2])
except (KeyError, IndexError, TypeError):
    v = None
print("" if v is None else v)
' "$1" "$2"
}

require_services() {
  API_JSON="$(service_json "${API_SERVICE}")" || die "Cannot read ${API_SERVICE} in ${PROJECT_ID}/${REGION}. Wrong project or region? Try: gcloud run services list --project=${PROJECT_ID}"
  WEB_JSON="$(service_json "${WEB_SERVICE}")" || die "Cannot read ${WEB_SERVICE} in ${PROJECT_ID}/${REGION}."
  API_IMAGE_NOW="$(json_field "${API_JSON}" 'd["spec"]["template"]["spec"]["containers"][0]["image"]')"
  WEB_IMAGE_NOW="$(json_field "${WEB_JSON}" 'd["spec"]["template"]["spec"]["containers"][0]["image"]')"
  API_TIMEOUT_NOW="$(json_field "${API_JSON}" 'd["spec"]["template"]["spec"]["timeoutSeconds"]')"
  WEB_TIMEOUT_NOW="$(json_field "${WEB_JSON}" 'd["spec"]["template"]["spec"]["timeoutSeconds"]')"
  RUNS_ENABLED_NOW="$(json_field "${API_JSON}" 'env("Runs__Enabled")')"
  UI_NOW="$(json_field "${WEB_JSON}" 'env("WEB_DEFAULT_UI")')"
  FIRESTORE_PROJECT="$(json_field "${API_JSON}" 'env("FIRESTORE_PROJECT_ID") or env("Firebase__ProjectId")')"
  FIRESTORE_PROJECT="${FIRESTORE_PROJECT:-${PROJECT_ID}}"
  ATTACHMENT_BUCKET="$(json_field "${API_JSON}" 'env("Persistence__AttachmentStorageBucket") or env("ATTACHMENT_STORAGE_BUCKET")')"
  API_URL="$(json_field "${API_JSON}" 'd["status"]["url"]')"
  WEB_URL="$(json_field "${WEB_JSON}" 'd["status"]["url"]')"
  API_REV_NOW="$(json_field "${API_JSON}" '[t["revisionName"] for t in d["status"]["traffic"] if t.get("percent") and not t.get("tag")][0]')"
  WEB_REV_NOW="$(json_field "${WEB_JSON}" '[t["revisionName"] for t in d["status"]["traffic"] if t.get("percent") and not t.get("tag")][0]')"
  # The Artifact Registry path the services already use, so images land next to the existing ones.
  IMAGE_PREFIX="${API_IMAGE_NOW%/*}"
  [[ "${IMAGE_PREFIX}" == */* ]] || IMAGE_PREFIX="${REGION}-docker.pkg.dev/${PROJECT_ID}/vertex-ai"
}

check_checkout() {
  COMMIT="$(git rev-parse HEAD)"
  SHORT="$(git rev-parse --short=12 HEAD)"
  if [[ -n "${EXPECT_COMMIT:-}" && "${COMMIT}" != "${EXPECT_COMMIT}"* ]]; then
    die "This checkout is ${COMMIT} but EXPECT_COMMIT=${EXPECT_COMMIT}. Run: git fetch origin && git checkout ${EXPECT_COMMIT}"
  fi
  if [[ -n "$(git status --porcelain)" && "${ALLOW_DIRTY:-0}" != "1" ]]; then
    die "The checkout has local changes; deploy a clean checkout (or set ALLOW_DIRTY=1)."
  fi
  API_IMAGE="${IMAGE_PREFIX}/vertex-ai-api:${SHORT}"
  WEB_IMAGE="${IMAGE_PREFIX}/vertex-ai-web:${SHORT}"
}

# A generation streams through the web proxy to the API, so the smaller request timeout is the real limit.
# The API ends a run 30 seconds before it, so a timeout is reported as a timeout with the partial reply kept.
run_max_seconds() {
  local web="${WEB_TIMEOUT%s}" api="${API_TIMEOUT_NOW:-300}"
  [[ "${web}" =~ ^[0-9]+$ ]] || die "WEB_TIMEOUT must be whole seconds such as 300s (got ${WEB_TIMEOUT})."
  local smaller=$(( api < web ? api : web ))
  local max=$(( smaller - 30 ))
  (( max >= 60 )) || die "Timeouts (web ${web}s, api ${api}s) leave only ${max}s for a generation; the minimum is 60s."
  echo "${max}"
}

image_exists() {
  gcloud artifacts docker images describe "$1" --project="${PROJECT_ID}" >/dev/null 2>&1
}

tag_url() {
  json_field "$(service_json "${WEB_SERVICE}")" "[t[\"url\"] for t in d[\"status\"][\"traffic\"] if t.get(\"tag\") == \"${TAG_NAME}\"][0]"
}

tag_revision() {
  json_field "$(service_json "${WEB_SERVICE}")" "[t[\"revisionName\"] for t in d[\"status\"][\"traffic\"] if t.get(\"tag\") == \"${TAG_NAME}\"][0]"
}

# ---- commands --------------------------------------------------------------------------------------------

cmd_plan() {
  require_services
  check_checkout
  local run_max
  run_max="$(run_max_seconds)"
  cat <<EOF
Release plan (read-only)
  Project / region        ${PROJECT_ID} / ${REGION}
  Checkout                ${COMMIT}
  API                     ${API_SERVICE}  (serving ${API_REV_NOW})
  Web                     ${WEB_SERVICE}  (serving ${WEB_REV_NOW})
  Images that will be built
                          ${API_IMAGE}
                          ${WEB_IMAGE}
  Kept as they are        service accounts, Firebase settings, model keys, Secret Manager references, scaling, IAM
  Settings that change
    API  Runs__MaxDurationSeconds  -> ${run_max}
    API  Runs__Enabled             ${RUNS_ENABLED_NOW:-(not set)} -> stays ${RUNS_ENABLED_NOW:-false} until 'enable-v2'
    Web  request timeout           ${WEB_TIMEOUT_NOW}s -> ${WEB_TIMEOUT}
    Web  WEB_DEFAULT_UI            ${UI_NOW:-(not set: old UI)} -> stays ${UI_NOW:-legacy} until 'ui next'
  Data
    Firestore project       ${FIRESTORE_PROJECT}
    Attachment bucket       ${ATTACHMENT_BUCKET:-(none configured: attachments would be sent inline)}
    Recorded backup         $(state_get BACKUP_PREFIX || true)
EOF
  [[ "${WEB_TIMEOUT_NOW}" != "${WEB_TIMEOUT%s}" ]] && info "The web request timeout will be raised to ${WEB_TIMEOUT}; with ${WEB_TIMEOUT_NOW}s a long reply would be cut off."
  [[ -n "${ATTACHMENT_BUCKET}" ]] || warn "No attachment bucket is configured on the API. Set ATTACHMENT_STORAGE_BUCKET on the service before relying on attachments."
  return 0
}

cmd_backup() {
  require_services
  local stamp location backup_bucket prefix
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  backup_bucket="${BACKUP_BUCKET:-${FIRESTORE_PROJECT}-firestore-backups}"
  location="$(gcloud firestore databases describe --project="${FIRESTORE_PROJECT}" --database='(default)' --format='value(locationId)')"
  [[ -n "${location}" ]] || die "Cannot read the Firestore location of ${FIRESTORE_PROJECT}."
  prefix="gs://${backup_bucket}/pre-v2-${stamp}"
  info "Firestore ${FIRESTORE_PROJECT} is in ${location}; backing up to ${prefix}"

  if ! gcloud storage buckets describe "gs://${backup_bucket}" >/dev/null 2>&1; then
    mutate gcloud storage buckets create "gs://${backup_bucket}" --project="${FIRESTORE_PROJECT}" \
      --location="${location}" --uniform-bucket-level-access
  fi

  mutate gcloud firestore export "${prefix}" --project="${FIRESTORE_PROJECT}" --database='(default)'
  if [[ "${DRY_RUN}" != "1" ]]; then
    gcloud storage ls -r "${prefix}" | grep -q 'overall_export_metadata' \
      || die "The export finished but ${prefix} has no overall_export_metadata. Do not continue."
  fi

  if [[ -n "${ATTACHMENT_BUCKET}" ]]; then
    info "Copying attachments (copy only, nothing is deleted at the destination)"
    mutate gcloud storage rsync --recursive "gs://${ATTACHMENT_BUCKET}" "${prefix}-attachments"
  else
    info "No attachment bucket configured, nothing to copy."
  fi

  state_set BACKUP_PREFIX "${prefix}"
  state_set BACKUP_AT "${stamp}"
  info "Backup recorded: ${prefix}"
}

cmd_indexes() {
  require_services
  info "Ensuring Firestore indexes in ${FIRESTORE_PROJECT} (existing indexes are left alone)"
  if [[ "${DRY_RUN}" == "1" ]]; then
    echo "+ FIRESTORE_PROJECT_ID=${FIRESTORE_PROJECT} node scripts/deploy-firestore-indexes.mjs"
    return 0
  fi
  FIRESTORE_PROJECT_ID="${FIRESTORE_PROJECT}" node scripts/deploy-firestore-indexes.mjs
  local i
  for i in $(seq 1 60); do
    if indexes_ready; then
      state_set INDEXES_READY "$(date -u +%Y%m%dT%H%M%SZ)"
      info "Firestore indexes are READY."
      return 0
    fi
    info "Waiting for indexes to build (${i}/60)..."
    sleep 20
  done
  die "Indexes are still building. Run this command again later; nothing else is affected."
}

indexes_ready() {
  local listing
  listing="$(gcloud firestore indexes composite list --project="${FIRESTORE_PROJECT}" --format='value(name,state)')"
  # Every index in firestore.indexes.json must be READY; match by collection group.
  python3 - "${listing}" <<'PY'
import json, sys
listing = sys.argv[1].splitlines()
manifest = json.load(open("firestore.indexes.json"))
need = {i["collectionGroup"] for i in manifest["indexes"]}
ready = {line.split("/collectionGroups/")[1].split("/")[0] for line in listing if "/collectionGroups/" in line and line.rstrip().endswith("READY")}
sys.exit(0 if need <= ready else 1)
PY
}

cmd_build() {
  require_services
  check_checkout
  for target in api web; do
    local image context
    if [[ "${target}" == api ]]; then image="${API_IMAGE}"; context=VertexAI; else image="${WEB_IMAGE}"; context=apps/web; fi
    if image_exists "${image}"; then
      info "${image} already exists, skipping build."
    else
      mutate gcloud builds submit "${context}" --project="${PROJECT_ID}" --tag="${image}"
    fi
  done
  state_set BUILT_COMMIT "${COMMIT}"
}

cmd_deploy() {
  require_services
  check_checkout
  local run_max
  run_max="$(run_max_seconds)"
  if [[ "${DRY_RUN}" != "1" ]]; then
    image_exists "${API_IMAGE}" && image_exists "${WEB_IMAGE}" || die "Images for ${SHORT} are missing. Run: bash scripts/release-cloud-shell.sh build"
  fi

  # Remember what is serving now, once, so 'rollback' knows where to go. A re-run keeps the first record.
  if [[ -z "$(state_get PREV_API_REVISION)" ]]; then
    state_set PREV_API_REVISION "${API_REV_NOW}"
    state_set PREV_WEB_REVISION "${WEB_REV_NOW}"
    state_set PREV_WEB_TIMEOUT "${WEB_TIMEOUT_NOW}"
  fi

  info "API: new image, generation time limit ${run_max}s, v2 routes ${RUNS_ENABLED_NOW:-false}. Everything else is kept."
  mutate gcloud run deploy "${API_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" \
    --image="${API_IMAGE}" \
    --update-env-vars="Runs__MaxDurationSeconds=${run_max},Runs__Enabled=${RUNS_ENABLED_NOW:-false}"

  info "Web: new image as revision tagged '${TAG_NAME}' with no traffic; timeout ${WEB_TIMEOUT}. Visitors still get the current revision."
  mutate gcloud run deploy "${WEB_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" \
    --image="${WEB_IMAGE}" --no-traffic --tag="${TAG_NAME}" \
    --timeout="${WEB_TIMEOUT}" \
    --update-env-vars="WEB_DEFAULT_UI=${UI_NOW:-legacy}"

  state_set DEPLOYED_COMMIT "${COMMIT}"
  if [[ "${DRY_RUN}" != "1" ]]; then
    info "Test URL for the new web revision: $(tag_url)/app/"
  fi
  info "Next: enable-v2, then verify, then try the real checklist at the test URL, then promote."
}

cmd_enable_v2() {
  require_services
  if [[ "${SKIP_BACKUP_CHECK:-0}" != "1" ]]; then
    [[ -n "$(state_get BACKUP_PREFIX)" ]] || die "No backup recorded in ${STATE_FILE}. Run 'backup' first (docs/refactor/backup-and-restore.md)."
    [[ -n "$(state_get INDEXES_READY)" ]] || die "Indexes are not recorded as READY. Run 'indexes' first."
  fi
  info "Turning the v2 run routes on. The old UI does not use them, so it keeps working exactly as before."
  mutate gcloud run services update "${API_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" \
    --update-env-vars="Runs__Enabled=true"
  state_set V2_ENABLED_AT "$(date -u +%Y%m%dT%H%M%SZ)"
}

cmd_disable_v2() {
  require_services
  info "Turning the v2 run routes off. Stored v2 data stays; the old UI is unaffected. Runs still in flight finish on their own."
  mutate gcloud run services update "${API_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" \
    --update-env-vars="Runs__Enabled=false"
}

http_status() { curl -s -o /dev/null -w '%{http_code}' --max-time 20 "$@" || true; }

VERIFY_FAILED=0
check() {
  local label="$1" want="$2" got="$3"
  if [[ "${got}" == "${want}" ]]; then echo "  ok    ${label} -> ${got}"; else echo "  FAIL  ${label}: wanted ${want}, got ${got}"; VERIFY_FAILED=$((VERIFY_FAILED + 1)); fi
}

cmd_verify() {
  require_services
  local token tag_base v2_want
  token="$(gcloud auth print-identity-token 2>/dev/null || true)"
  tag_base="$(tag_url || true)"
  v2_want=404
  [[ "$(json_field "$(service_json "${API_SERVICE}")" 'env("Runs__Enabled")')" == "true" ]] && v2_want=401

  echo "API (private)"
  check "direct unauthenticated /health/live is refused" 403 "$(http_status "${API_URL}/health/live")"
  if [[ -n "${token}" ]]; then
    check "/health/ready with your identity (reaches Firestore)" 200 "$(http_status -H "Authorization: Bearer ${token}" "${API_URL}/health/ready")"
  else
    warn "No identity token available; skipping the authenticated readiness check."
  fi

  echo "Web, current traffic (${WEB_URL})"
  check "/api/workspace/config" 200 "$(http_status "${WEB_URL}/api/workspace/config")"
  check "/api/conversations without sign-in" 401 "$(http_status "${WEB_URL}/api/conversations")"
  check "/api/v2/me/settings without sign-in (v2 $([[ ${v2_want} == 401 ]] && echo on || echo off))" "${v2_want}" "$(http_status "${WEB_URL}/api/v2/me/settings")"

  if [[ -n "${tag_base}" ]]; then
    echo "Web, new revision (${tag_base})"
    check "/app/ serves the new UI" 200 "$(http_status "${tag_base}/app/")"
    check "/legacy serves the old UI" 200 "$(http_status "${tag_base}/legacy")"
    check "/api/workspace/config" 200 "$(http_status "${tag_base}/api/workspace/config")"
    check "/api/v2/me/settings without sign-in" "${v2_want}" "$(http_status "${tag_base}/api/v2/me/settings")"
  else
    echo "Web, new revision: no '${TAG_NAME}' tag (already promoted, or 'deploy' not run)"
  fi

  echo "Settings"
  check "web request timeout (seconds)" "${WEB_TIMEOUT%s}" "${WEB_TIMEOUT_NOW}"
  if (( VERIFY_FAILED > 0 )); then die "${VERIFY_FAILED} check(s) failed."; fi
  info "All automated checks passed. They do not sign in, call a model or touch attachments: see the real checklist in docs/refactor/cloud-shell-release.md."
}

cmd_promote() {
  require_services
  local rev latest
  rev="$(tag_revision || true)"
  [[ -n "${rev}" ]] || die "There is no '${TAG_NAME}' revision. Run 'deploy' first (or it was already promoted: see 'status')."
  latest="$(json_field "${WEB_JSON}" 'd["status"]["latestReadyRevisionName"]')"
  [[ "${rev}" == "${latest}" ]] || die "Tag ${TAG_NAME} is on ${rev} but the latest ready revision is ${latest}. Run 'deploy' again."
  info "Sending all web traffic to ${rev} (previous: ${WEB_REV_NOW}); later settings changes will follow the latest revision."
  mutate gcloud run services update-traffic "${WEB_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" --to-latest
  mutate gcloud run services update-traffic "${WEB_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" --remove-tags="${TAG_NAME}"
  state_set PROMOTED_REVISION "${rev}"
}

cmd_ui() {
  [[ "${ARG}" == "next" || "${ARG}" == "legacy" ]] || die "Usage: release-cloud-shell.sh ui next|legacy"
  require_services
  info "Setting the page at / to the ${ARG} UI. The other one stays reachable at $([[ ${ARG} == next ]] && echo /legacy || echo /app/)."
  mutate gcloud run services update "${WEB_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" \
    --update-env-vars="WEB_DEFAULT_UI=${ARG}"
}

cmd_rollback() {
  require_services
  local prev_web prev_api
  prev_web="${PREV_WEB_REVISION:-$(state_get PREV_WEB_REVISION)}"
  prev_api="${PREV_API_REVISION:-$(state_get PREV_API_REVISION)}"
  [[ -n "${prev_web}" && -n "${prev_api}" ]] || die "No previous revisions recorded. List them with: gcloud run revisions list --service=${WEB_SERVICE} --region=${REGION}  then set PREV_WEB_REVISION / PREV_API_REVISION."
  info "Web back to ${prev_web}, API back to ${prev_api}. Data is not touched; v2 data is additive and harmless to the old code."
  mutate gcloud run services update-traffic "${WEB_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" --to-revisions="${prev_web}=100"
  mutate gcloud run services update-traffic "${API_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" --to-revisions="${prev_api}=100"
}

cmd_status() {
  require_services
  cat <<EOF
Project / region  ${PROJECT_ID} / ${REGION}
API  ${API_SERVICE}: serving ${API_REV_NOW}, image ${API_IMAGE_NOW}, v2 routes ${RUNS_ENABLED_NOW:-false}, timeout ${API_TIMEOUT_NOW}s
Web  ${WEB_SERVICE}: serving ${WEB_REV_NOW}, image ${WEB_IMAGE_NOW}, "/" shows ${UI_NOW:-legacy}, timeout ${WEB_TIMEOUT_NOW}s
Test revision    $(tag_url || true)
Recorded state (${STATE_FILE}):
$(sed 's/^/  /' "${STATE_FILE}")
EOF
}

case "${COMMAND}" in
  plan) cmd_plan ;;
  backup) cmd_backup ;;
  indexes) cmd_indexes ;;
  build) cmd_build ;;
  deploy) cmd_deploy ;;
  enable-v2) cmd_enable_v2 ;;
  disable-v2) cmd_disable_v2 ;;
  verify) cmd_verify ;;
  promote) cmd_promote ;;
  ui) cmd_ui ;;
  rollback) cmd_rollback ;;
  status) cmd_status ;;
esac
