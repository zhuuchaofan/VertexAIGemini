#!/usr/bin/env bash
# Staged release of the new workspace to an EXISTING Cloud Run deployment, run from Cloud Shell.
#
# Every step is safe to run again. Nothing here asks for a value that Cloud Run already holds: service accounts,
# Firebase settings, model keys and Secret Manager references stay on the services; this script only adds the
# new settings and replaces the container images. Mutating steps print the commands they are about to run.
#
#   bash scripts/release-cloud-shell.sh plan          read-only: what exists, what will change
#   bash scripts/release-cloud-shell.sh backup        Firestore export + attachment copy (writes only to a backup bucket)
#   bash scripts/release-cloud-shell.sh indexes       create the Firestore indexes the new version needs, check each one is READY
#   bash scripts/release-cloud-shell.sh build         Cloud Build both images, tagged with this commit
#   bash scripts/release-cloud-shell.sh deploy        API: new revision takes production traffic at once (v2 still off);
#                                                     web: new revision with no traffic, tagged `next`
#   bash scripts/release-cloud-shell.sh auth-domain [add]   check (or add) the test URL's host in Firebase authorized domains
#   bash scripts/release-cloud-shell.sh enable-v2     turn the v2 run routes on (needs a fresh backup and READY indexes, re-checked now)
#   bash scripts/release-cloud-shell.sh verify        automated checks of THIS release's candidate revisions; fails without one
#   bash scripts/release-cloud-shell.sh promote       send all web traffic to the candidate (old UI still the default page)
#   bash scripts/release-cloud-shell.sh ui next|legacy   choose which UI "/" shows
#   bash scripts/release-cloud-shell.sh disable-v2    switch the v2 routes off again
#   bash scripts/release-cloud-shell.sh rollback [api|web|all]   traffic back to the revisions recorded before this release
#   bash scripts/release-cloud-shell.sh status
#
# Progress is recorded per release: one state file per project, region, services and commit, so a backup or
# rollback point from another release, project or service is never reused.
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
BACKUP_MAX_AGE_HOURS="${BACKUP_MAX_AGE_HOURS:-72}"
STATE_FILE=""
DRY_RUN="${DRY_RUN:-0}"
TAG_NAME="next"

info() { echo "[INFO] $*"; }
warn() { echo "[WARN] $*" >&2; }
die() { echo "[ERROR] $*" >&2; exit 1; }

usage() {
  sed -n '2,32p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'
}

case "${COMMAND}" in
  plan|backup|indexes|build|deploy|auth-domain|enable-v2|verify|promote|ui|disable-v2|rollback|status) ;;
  ""|--help|-h|help) usage; exit 0 ;;
  *) usage >&2; die "Unknown command: ${COMMAND}" ;;
esac

PROJECT_ID="${PROJECT_ID:-$(gcloud config get-value project 2>/dev/null || true)}"
[[ -n "${PROJECT_ID}" ]] || die "No project. Run: gcloud config set project <project-id>  (or export PROJECT_ID)"

mkdir -p "${STATE_DIR}"

# ---- helpers ---------------------------------------------------------------------------------------------

# Mutating gcloud calls go through here so DRY_RUN=1 shows them without running them.
mutate() {
  echo "+ $*"
  if [[ "${DRY_RUN}" == "1" ]]; then return 0; fi
  "$@"
}

state_get() { [[ -f "${STATE_FILE}" ]] && { grep -E "^$1=" "${STATE_FILE}" | tail -1 | cut -d= -f2- || true; } || true; }
state_set() {
  [[ "${DRY_RUN}" == "1" ]] && return 0
  touch "${STATE_FILE}"
  grep -vE "^$1=" "${STATE_FILE}" > "${STATE_FILE}.tmp" || true
  echo "$1=$2" >> "${STATE_FILE}.tmp"
  mv "${STATE_FILE}.tmp" "${STATE_FILE}"
}

# One state file per project, region, services and commit. The header is checked on every load, so a file that was
# copied or renamed from another release cannot lend its backup or rollback point to this one.
init_state() {
  local key
  key="$(printf '%s' "${PROJECT_ID}_${REGION}_${API_SERVICE}_${WEB_SERVICE}_${SHORT}" | tr -c 'A-Za-z0-9._-' '_')"
  STATE_FILE="${STATE_DIR}/release-${key}.env"
  local field want have
  for field in PROJECT_ID REGION API_SERVICE WEB_SERVICE; do
    want="${!field}"
    have="$(state_get "${field}")"
    if [[ -n "${have}" && "${have}" != "${want}" ]]; then
      die "${STATE_FILE} belongs to ${field}=${have}, not ${want}. Remove it only if you are sure it is stale."
    fi
  done
  # Files written before release ids existed carry the full commit instead.
  have="$(state_get RELEASE_ID)"
  if [[ -n "${have}" && "${have}" != "${SHORT}" ]]; then
    die "${STATE_FILE} belongs to RELEASE_ID=${have}, not ${SHORT}. Remove it only if you are sure it is stale."
  fi
  have="$(state_get COMMIT)"
  if [[ -z "$(state_get RELEASE_ID)" && -n "${have}" && "${have}" != "${SHORT}"* ]]; then
    die "${STATE_FILE} belongs to COMMIT=${have}, not release ${SHORT}. Remove it only if you are sure it is stale."
  fi
  if [[ ! -f "${STATE_FILE}" ]]; then
    state_set PROJECT_ID "${PROJECT_ID}"
    state_set REGION "${REGION}"
    state_set API_SERVICE "${API_SERVICE}"
    state_set WEB_SERVICE "${WEB_SERVICE}"
    state_set RELEASE_ID "${SHORT}"
    state_set COMMIT "${COMMIT}"
    state_set CREATED_AT "$(date -u +%Y%m%dT%H%M%SZ)"
  fi
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

# Everything that records or relies on release progress starts here.
init_release() {
  require_services
  check_checkout
  init_state
}

check_checkout() {
  COMMIT="$(git rev-parse HEAD)"
  # The release id names the images and the state file. It is the short commit unless you keep going with an
  # earlier release's images and records from a newer checkout that changed only these scripts (RELEASE_ID=<short>).
  SHORT="${RELEASE_ID:-$(git rev-parse --short=12 HEAD)}"
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

revision_image() {
  gcloud run revisions describe "$1" --project="${PROJECT_ID}" --region="${REGION}" --format='value(spec.containers[0].image)'
}

image_digest() {
  gcloud artifacts docker images describe "$1" --project="${PROJECT_ID}" --format='value(image_summary.digest)' 2>/dev/null
}

# Cloud Run records a revision's image as <repo>/<name>@sha256:<digest> and drops the tag. An image therefore matches
# when it is exactly the expected tag, that tag pinned to a digest, or the same repository path at the digest the
# expected tag points to in Artifact Registry.
image_matches() {
  local actual="$1" expected="$2" digest
  [[ -n "${actual}" ]] || return 1
  [[ "${actual}" == "${expected}" || "${actual}" == "${expected}"@* ]] && return 0
  if [[ "${actual}" == *@sha256:* && "${actual%@*}" == "${expected%:*}" ]]; then
    digest="$(image_digest "${expected}" || true)"
    [[ -n "${digest}" && "${actual##*@}" == "${digest}" ]] && return 0
  fi
  return 1
}

tag_url() {
  json_field "$(service_json "${WEB_SERVICE}")" "[t[\"url\"] for t in d[\"status\"][\"traffic\"] if t.get(\"tag\") == \"${TAG_NAME}\"][0]"
}

tag_revision() {
  json_field "$(service_json "${WEB_SERVICE}")" "[t[\"revisionName\"] for t in d[\"status\"][\"traffic\"] if t.get(\"tag\") == \"${TAG_NAME}\"][0]"
}

# ---- commands --------------------------------------------------------------------------------------------

cmd_plan() {
  init_release
  local run_max
  run_max="$(run_max_seconds)"
  cat <<EOF
Release plan (read-only)
  Project / region        ${PROJECT_ID} / ${REGION}
  Checkout                ${COMMIT}
  Release id (images, records)  ${SHORT}
  State file              ${STATE_FILE}
  API                     ${API_SERVICE}  (serving ${API_REV_NOW})
  Web                     ${WEB_SERVICE}  (serving ${WEB_REV_NOW})
  Images that will be built
                          ${API_IMAGE}
                          ${WEB_IMAGE}
  Kept as they are        service accounts, Firebase settings, model keys, Secret Manager references, scaling, IAM
  Settings that change
    API  Runs__MaxDurationSeconds  -> ${run_max}
    API  Runs__Enabled             ${RUNS_ENABLED_NOW:-(not set)} -> stays ${RUNS_ENABLED_NOW:-false} until 'enable-v2'
    Web  request timeout           ${WEB_TIMEOUT_NOW}s -> ${WEB_TIMEOUT} (on the candidate revision)
    Web  WEB_DEFAULT_UI            ${UI_NOW:-(not set: old UI)} -> stays ${UI_NOW:-legacy} until 'ui next'
  Production traffic impact
    API  'deploy' gives the new API revision ALL production traffic immediately. Today's users of the old UI
         are then served by the new API code (old endpoints unchanged in behaviour, v2 routes off). Undo with:
         rollback api   (see the manual, section 5.2)
    Web  the new revision gets no traffic until 'promote'
  Data
    Firestore project       ${FIRESTORE_PROJECT}
    Attachment bucket       ${ATTACHMENT_BUCKET:-(none configured: attachments would be sent inline)}
    Backup recorded for this release   $(state_get BACKUP_ROOT)
EOF
  [[ "${WEB_TIMEOUT_NOW}" != "${WEB_TIMEOUT%s}" ]] && info "The web request timeout will be raised to ${WEB_TIMEOUT}; with ${WEB_TIMEOUT_NOW}s a long reply would be cut off."
  [[ -n "${ATTACHMENT_BUCKET}" ]] || warn "No attachment bucket is configured on the API. Set ATTACHMENT_STORAGE_BUCKET on the service before relying on attachments."
  return 0
}

cmd_backup() {
  init_release
  local stamp location backup_bucket root
  stamp="$(date -u +%Y%m%dT%H%M%SZ)"
  backup_bucket="${BACKUP_BUCKET:-${FIRESTORE_PROJECT}-firestore-backups}"
  location="$(gcloud firestore databases describe --project="${FIRESTORE_PROJECT}" --database='(default)' --format='value(locationId)')"
  [[ -n "${location}" ]] || die "Cannot read the Firestore location of ${FIRESTORE_PROJECT}."
  # One root per backup: <root>/firestore is the managed export, <root>/attachments is the object copy.
  root="gs://${backup_bucket}/pre-v2-${stamp}"
  info "Firestore ${FIRESTORE_PROJECT} is in ${location}; backing up to ${root}"

  if ! gcloud storage buckets describe "gs://${backup_bucket}" >/dev/null 2>&1; then
    mutate gcloud storage buckets create "gs://${backup_bucket}" --project="${FIRESTORE_PROJECT}" \
      --location="${location}" --uniform-bucket-level-access
  fi

  mutate gcloud firestore export "${root}/firestore" --project="${FIRESTORE_PROJECT}" --database='(default)'
  if [[ "${DRY_RUN}" != "1" ]]; then
    gcloud storage ls -r "${root}/firestore" | grep -q 'overall_export_metadata' \
      || die "The export finished but ${root}/firestore has no overall_export_metadata. Do not continue."
  fi

  if [[ -n "${ATTACHMENT_BUCKET}" ]]; then
    info "Copying attachments (copy only, nothing is deleted at the destination)"
    mutate gcloud storage rsync --recursive "gs://${ATTACHMENT_BUCKET}" "${root}/attachments"
  else
    info "No attachment bucket configured, nothing to copy."
  fi

  state_set BACKUP_ROOT "${root}"
  state_set BACKUP_EPOCH "$(date -u +%s)"
  info "Backup recorded for this release: ${root}"
  info "Restore paths: Firestore ${root}/firestore, attachments ${root}/attachments (docs/refactor/backup-and-restore.md)"
}

# Every index in firestore.indexes.json, by its full definition, against what exists right now.
indexes_report() {
  gcloud firestore indexes composite list --project="${FIRESTORE_PROJECT}" --format=json \
    | python3 scripts/check-firestore-indexes.py firestore.indexes.json
}

cmd_indexes() {
  init_release
  info "Ensuring Firestore indexes in ${FIRESTORE_PROJECT} (existing indexes are left alone)"
  if [[ "${DRY_RUN}" == "1" ]]; then
    echo "+ FIRESTORE_PROJECT_ID=${FIRESTORE_PROJECT} node scripts/deploy-firestore-indexes.mjs"
    return 0
  fi
  FIRESTORE_PROJECT_ID="${FIRESTORE_PROJECT}" node scripts/deploy-firestore-indexes.mjs
  local i report
  for i in $(seq 1 "${INDEX_WAIT_ROUNDS:-60}"); do
    if report="$(indexes_report)"; then
      echo "${report}"
      info "All indexes are READY."
      return 0
    fi
    echo "${report}"
    info "Waiting for indexes to build (${i}/${INDEX_WAIT_ROUNDS:-60})..."
    sleep "${INDEX_WAIT_SECONDS:-20}"
  done
  die "Indexes are still not all READY. Run this command again later; nothing else is affected."
}

cmd_build() {
  init_release
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
  init_release
  local run_max
  run_max="$(run_max_seconds)"
  if [[ "${DRY_RUN}" != "1" ]]; then
    image_exists "${API_IMAGE}" && image_exists "${WEB_IMAGE}" || die "Images for ${SHORT} are missing. Run: bash scripts/release-cloud-shell.sh build"
  fi

  # Remember what is serving now, once per release, so 'rollback' knows where to go. A re-run keeps the first record.
  if [[ -z "$(state_get PREV_API_REVISION)" ]]; then
    state_set PREV_API_REVISION "${API_REV_NOW}"
    state_set PREV_WEB_REVISION "${WEB_REV_NOW}"
    state_set PREV_WEB_TIMEOUT "${WEB_TIMEOUT_NOW}"
  fi

  warn "The API step below is NOT a no-traffic release: the new API revision takes production traffic as soon as it is ready."
  warn "Old-UI users are served by the new API code from that moment. To undo: bash scripts/release-cloud-shell.sh rollback api"
  info "API: new image, generation time limit ${run_max}s, v2 routes ${RUNS_ENABLED_NOW:-false}. Everything else is kept."
  mutate gcloud run deploy "${API_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" \
    --image="${API_IMAGE}" \
    --update-env-vars="Runs__MaxDurationSeconds=${run_max},Runs__Enabled=${RUNS_ENABLED_NOW:-false}"

  info "Web: new image as revision tagged '${TAG_NAME}' with no traffic; timeout ${WEB_TIMEOUT}. Visitors still get the current revision."
  mutate gcloud run deploy "${WEB_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" \
    --image="${WEB_IMAGE}" --no-traffic --tag="${TAG_NAME}" \
    --timeout="${WEB_TIMEOUT}" \
    --update-env-vars="WEB_DEFAULT_UI=${UI_NOW:-legacy}"

  if [[ "${DRY_RUN}" != "1" ]]; then
    local api_candidate web_candidate
    api_candidate="$(json_field "$(service_json "${API_SERVICE}")" 'd["status"]["latestReadyRevisionName"]')"
    web_candidate="$(tag_revision || true)"
    [[ -n "${api_candidate}" && -n "${web_candidate}" ]] || die "Could not determine the candidate revisions after deploying."
    image_matches "$(revision_image "${api_candidate}")" "${API_IMAGE}" || die "API revision ${api_candidate} does not run ${API_IMAGE}."
    image_matches "$(revision_image "${web_candidate}")" "${WEB_IMAGE}" || die "Web revision ${web_candidate} does not run ${WEB_IMAGE}."
    state_set CANDIDATE_API_REVISION "${api_candidate}"
    state_set CANDIDATE_WEB_REVISION "${web_candidate}"
    state_set DEPLOYED_COMMIT "${COMMIT}"
    info "Candidates recorded: API ${api_candidate}, web ${web_candidate}"
    info "Test URL for the candidate web revision: $(tag_url)/app/"
  fi
  info "Next: auth-domain add, enable-v2, verify, the real checklist at the test URL, then promote."
}

firebase_project() {
  json_field "${API_JSON}" 'env("FIREBASE_PROJECT_ID") or env("Firebase__ProjectId")'
}

auth_config() {
  local fb="$1"
  curl -sS --fail --max-time 30 \
    -H "Authorization: Bearer $(gcloud auth print-access-token)" -H "x-goog-user-project: ${fb}" \
    "https://identitytoolkit.googleapis.com/admin/v2/projects/${fb}/config"
}

# Sign-in with a pop-up or redirect and password-reset links only work on hosts in Firebase "Authorized domains".
# The candidate revision has its own host (next---<service>-….run.app), which is not there until you add it.
cmd_auth_domain() {
  init_release
  local fb host domains
  fb="$(firebase_project)"
  [[ -n "${fb}" ]] || die "The API has no FIREBASE_PROJECT_ID / Firebase__ProjectId."
  host="$(tag_url | sed -e 's#^https\{0,1\}://##' -e 's#/.*$##')"
  [[ -n "${host}" ]] || die "There is no '${TAG_NAME}' revision. Run 'deploy' first."
  domains="$(auth_config "${fb}" | python3 -c 'import json,sys; print("\n".join(json.load(sys.stdin).get("authorizedDomains", [])))')"
  if grep -qxF "${host}" <<< "${domains}"; then
    info "${host} is already an authorized domain of Firebase project ${fb}."
    return 0
  fi
  if [[ "${ARG}" != "add" ]]; then
    echo "  not authorized: ${host}  (Firebase project ${fb})"
    info "Add it with: bash scripts/release-cloud-shell.sh auth-domain add"
    info "or in the console: Authentication > Settings > Authorized domains > Add domain."
    return 1
  fi
  local body
  body="$(python3 -c 'import json,sys; d=sys.stdin.read().split(); d.append(sys.argv[1]); print(json.dumps({"authorizedDomains": d}))' "${host}" <<< "${domains}")"
  info "Adding ${host} to the authorized domains of ${fb} (existing domains are kept)."
  if [[ "${DRY_RUN}" == "1" ]]; then echo "+ PATCH authorizedDomains ${body}"; return 0; fi
  curl -sS --fail --max-time 30 -X PATCH \
    -H "Authorization: Bearer $(gcloud auth print-access-token)" -H "x-goog-user-project: ${fb}" \
    -H "content-type: application/json" -d "${body}" \
    "https://identitytoolkit.googleapis.com/admin/v2/projects/${fb}/config?updateMask=authorizedDomains" >/dev/null
  info "Done. Remove the test host later in the console if you like; it is harmless after promote."
}

cmd_enable_v2() {
  init_release
  if [[ "${SKIP_BACKUP_CHECK:-0}" != "1" ]]; then
    local root epoch age_hours
    root="$(state_get BACKUP_ROOT)"
    epoch="$(state_get BACKUP_EPOCH)"
    [[ -n "${root}" && -n "${epoch}" ]] || die "No backup recorded for THIS release (${STATE_FILE}). Run 'backup' first (docs/refactor/backup-and-restore.md)."
    age_hours=$(( ( $(date -u +%s) - epoch ) / 3600 ))
    (( age_hours <= BACKUP_MAX_AGE_HOURS )) || die "The recorded backup is ${age_hours}h old (limit ${BACKUP_MAX_AGE_HOURS}h). Run 'backup' again."
    gcloud storage ls -r "${root}/firestore" 2>/dev/null | grep -q 'overall_export_metadata' \
      || die "The recorded backup ${root}/firestore no longer has its export metadata. Run 'backup' again."
  fi
  local report
  if ! report="$(indexes_report)"; then
    echo "${report}"
    die "Not every index in firestore.indexes.json is READY right now. Run 'indexes' and wait; v2 stays off."
  fi
  echo "${report}"
  info "Turning the v2 run routes on. The old UI does not use them, so it keeps working exactly as before."
  mutate gcloud run services update "${API_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" \
    --update-env-vars="Runs__Enabled=true"
  state_set V2_ENABLED_AT "$(date -u +%Y%m%dT%H%M%SZ)"
  if [[ "${DRY_RUN}" != "1" ]]; then
    # The env change made a new API revision; it is the candidate from now on.
    state_set CANDIDATE_API_REVISION "$(json_field "$(service_json "${API_SERVICE}")" 'd["status"]["latestReadyRevisionName"]')"
  fi
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
verify_fail() { echo "  FAIL  $*"; VERIFY_FAILED=$((VERIFY_FAILED + 1)); }

# Verifies the candidate revisions this release recorded, not whatever happens to be serving.
cmd_verify() {
  init_release
  local api_candidate web_candidate token base v2_want web_tag_rev
  api_candidate="$(state_get CANDIDATE_API_REVISION)"
  web_candidate="$(state_get CANDIDATE_WEB_REVISION)"
  [[ -n "${api_candidate}" && -n "${web_candidate}" ]] \
    || die "No candidate revisions recorded for this release (${STATE_FILE}). Run 'deploy' first; verify will not test whatever is serving."

  echo "Candidates for ${SHORT}: API ${api_candidate}, web ${web_candidate}"

  echo "Revisions"
  local api_image web_image
  api_image="$(revision_image "${api_candidate}" || true)"
  web_image="$(revision_image "${web_candidate}" || true)"
  if image_matches "${api_image}" "${API_IMAGE}"; then echo "  ok    API candidate runs ${API_IMAGE}"; else verify_fail "API candidate ${api_candidate} runs '${api_image}', expected ${API_IMAGE}"; fi
  if image_matches "${web_image}" "${WEB_IMAGE}"; then echo "  ok    web candidate runs ${WEB_IMAGE}"; else verify_fail "web candidate ${web_candidate} runs '${web_image}', expected ${WEB_IMAGE}"; fi
  if [[ "${API_REV_NOW}" == "${api_candidate}" ]]; then echo "  ok    the API candidate is the revision serving production traffic"; else verify_fail "API serving ${API_REV_NOW}, not the candidate ${api_candidate}"; fi

  web_tag_rev="$(tag_revision || true)"
  if [[ -n "${web_tag_rev}" ]]; then
    base="$(tag_url)"
    if [[ "${web_tag_rev}" == "${web_candidate}" ]]; then echo "  ok    tag ${TAG_NAME} points at the web candidate"; else verify_fail "tag ${TAG_NAME} points at ${web_tag_rev}, not the candidate ${web_candidate}"; fi
  elif [[ -n "$(state_get PROMOTED_REVISION)" && "${WEB_REV_NOW}" == "${web_candidate}" ]]; then
    base="${WEB_URL}"
    echo "  ok    the web candidate has been promoted and is serving"
  else
    verify_fail "the web candidate ${web_candidate} has no ${TAG_NAME} tag and is not serving: it cannot be tested"
    base=""
  fi

  v2_want=404
  [[ "$(json_field "$(service_json "${API_SERVICE}")" 'env("Runs__Enabled")')" == "true" ]] && v2_want=401
  token="$(gcloud auth print-identity-token 2>/dev/null || true)"

  echo "API (private)"
  check "direct unauthenticated /health/live is refused" 403 "$(http_status "${API_URL}/health/live")"
  if [[ -n "${token}" ]]; then
    check "/health/ready with your identity (reaches Firestore)" 200 "$(http_status -H "Authorization: Bearer ${token}" "${API_URL}/health/ready")"
  else
    verify_fail "no identity token available for the authenticated readiness check"
  fi

  if [[ -n "${base}" ]]; then
    echo "Web candidate (${base})"
    check "/app/ serves the new UI" 200 "$(http_status "${base}/app/")"
    check "/legacy serves the old UI" 200 "$(http_status "${base}/legacy")"
    check "/api/workspace/config" 200 "$(http_status "${base}/api/workspace/config")"
    check "/api/conversations without sign-in" 401 "$(http_status "${base}/api/conversations")"
    check "/api/v2/me/settings without sign-in (v2 $([[ ${v2_want} == 401 ]] && echo on || echo off))" "${v2_want}" "$(http_status "${base}/api/v2/me/settings")"
  fi

  echo "Settings"
  local web_timeout_candidate
  web_timeout_candidate="$(gcloud run revisions describe "${web_candidate}" --project="${PROJECT_ID}" --region="${REGION}" --format='value(spec.timeoutSeconds)' 2>/dev/null || true)"
  check "web candidate request timeout (seconds)" "${WEB_TIMEOUT%s}" "${web_timeout_candidate}"
  if (( VERIFY_FAILED > 0 )); then die "${VERIFY_FAILED} check(s) failed."; fi
  info "All automated checks passed for the candidate revisions. They do not sign in, call a model or touch attachments: see the real checklist in docs/refactor/cloud-shell-release.md."
}

cmd_promote() {
  init_release
  local rev candidate latest
  candidate="$(state_get CANDIDATE_WEB_REVISION)"
  rev="$(tag_revision || true)"
  [[ -n "${rev}" ]] || die "There is no '${TAG_NAME}' revision. Run 'deploy' first (or it was already promoted: see 'status')."
  [[ -n "${candidate}" && "${rev}" == "${candidate}" ]] || die "Tag ${TAG_NAME} is on ${rev} but this release's candidate is '${candidate}'. Run 'deploy' again."
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
  local scope="${ARG:-all}"
  [[ "${scope}" == "api" || "${scope}" == "web" || "${scope}" == "all" ]] || die "Usage: release-cloud-shell.sh rollback [api|web|all]"
  init_release
  local prev_web prev_api
  prev_web="${PREV_WEB_REVISION:-$(state_get PREV_WEB_REVISION)}"
  prev_api="${PREV_API_REVISION:-$(state_get PREV_API_REVISION)}"
  if [[ "${scope}" != "web" ]]; then
    [[ -n "${prev_api}" ]] || die "No previous API revision recorded for this release. List them with: gcloud run revisions list --service=${API_SERVICE} --region=${REGION}  then set PREV_API_REVISION."
    if [[ "${RUNS_ENABLED_NOW}" == "true" ]]; then
      warn "v2 routes are ON. Run 'disable-v2' first and let in-flight replies finish (about a minute); the older API does not know v2 runs and would leave them 'running'."
      [[ "${FORCE_ROLLBACK:-0}" == "1" ]] || die "Refusing to roll the API back while v2 is on. Run disable-v2, or set FORCE_ROLLBACK=1."
    fi
  fi
  if [[ "${scope}" != "api" ]]; then
    [[ -n "${prev_web}" ]] || die "No previous web revision recorded for this release. List them with: gcloud run revisions list --service=${WEB_SERVICE} --region=${REGION}  then set PREV_WEB_REVISION."
  fi
  info "Rolling back (${scope}). Data is not touched; v2 data is additive and harmless to the old code."
  if [[ "${scope}" != "api" ]]; then
    mutate gcloud run services update-traffic "${WEB_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" --to-revisions="${prev_web}=100"
  fi
  if [[ "${scope}" != "web" ]]; then
    mutate gcloud run services update-traffic "${API_SERVICE}" --project="${PROJECT_ID}" --region="${REGION}" --to-revisions="${prev_api}=100"
  fi
}

cmd_status() {
  require_services
  cat <<EOF
Project / region  ${PROJECT_ID} / ${REGION}
API  ${API_SERVICE}: serving ${API_REV_NOW}, image ${API_IMAGE_NOW}, v2 routes ${RUNS_ENABLED_NOW:-false}, timeout ${API_TIMEOUT_NOW}s
Web  ${WEB_SERVICE}: serving ${WEB_REV_NOW}, image ${WEB_IMAGE_NOW}, "/" shows ${UI_NOW:-legacy}, timeout ${WEB_TIMEOUT_NOW}s
Test revision    $(tag_url || true)
Release state files (${STATE_DIR}):
$(ls -1 "${STATE_DIR}" 2>/dev/null | sed 's/^/  /')
EOF
}

case "${COMMAND}" in
  plan) cmd_plan ;;
  backup) cmd_backup ;;
  indexes) cmd_indexes ;;
  build) cmd_build ;;
  deploy) cmd_deploy ;;
  auth-domain) cmd_auth_domain ;;
  enable-v2) cmd_enable_v2 ;;
  disable-v2) cmd_disable_v2 ;;
  verify) cmd_verify ;;
  promote) cmd_promote ;;
  ui) cmd_ui ;;
  rollback) cmd_rollback ;;
  status) cmd_status ;;
esac
