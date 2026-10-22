#!/usr/bin/env bash
# Starts the real API and web images (the ones Cloud Run would run) and checks what can be checked without
# production credentials. Needs a Firestore emulator on FIRESTORE_EMULATOR_HOST (the host network is shared, so
# 127.0.0.1:<port> works from inside the containers).
#
#   firebase emulators:exec --only firestore --project demo-smoke "scripts/container-smoke.sh vai-api:test vai-web:test"
#
# What this does NOT prove: a signed-in request (Production refuses the dev fake user on purpose), Vertex, GCS, and
# Firebase sign-in. Those are in the post-deploy checklist.
set -euo pipefail

API_IMAGE="${1:?usage: container-smoke.sh <api-image> <web-image>}"
WEB_IMAGE="${2:?usage: container-smoke.sh <api-image> <web-image>}"
: "${FIRESTORE_EMULATOR_HOST:?start this under a Firestore emulator (FIRESTORE_EMULATOR_HOST is not set)}"

API_PORT="${SMOKE_API_PORT:-18080}"
WEB_PORT="${SMOKE_WEB_PORT:-18081}"
API_NAME="smoke-api-$$"
WEB_NAME="smoke-web-$$"
failures=0

# The Firebase Admin SDK looks for Application Default Credentials when it starts. Verifying a sign-in token only
# needs Google's public keys, so a made-up credential file is enough here; it is never used to call Google.
FAKE_ADC="$(mktemp)"
chmod 644 "${FAKE_ADC}"
echo '{"type":"authorized_user","client_id":"smoke","client_secret":"smoke","refresh_token":"smoke"}' > "${FAKE_ADC}"

cleanup() {
  docker rm -f "${API_NAME}" "${WEB_NAME}" >/dev/null 2>&1 || true
  rm -f "${FAKE_ADC}"
}
trap cleanup EXIT

pass() { echo "  ok   $*"; }
fail() { echo "  FAIL $*"; failures=$((failures + 1)); }

status_of() { curl -s -o /dev/null -w '%{http_code}' --max-time 10 "$@" || true; }

expect_status() {
  local want="$1" label="$2"
  shift 2
  local got
  got="$(status_of "$@")"
  if [[ "${got}" == "${want}" ]]; then pass "${label} -> ${got}"; else fail "${label}: wanted ${want}, got ${got}"; fi
}

wait_for() {
  local url="$1" label="$2" i
  for i in $(seq 1 60); do
    if [[ "$(status_of "${url}")" =~ ^(200|302|401|404)$ ]]; then return 0; fi
    sleep 1
  done
  fail "${label} did not answer within 60s"
  docker logs "${API_NAME}" 2>&1 | tail -20 || true
  return 1
}

wait_healthy() {
  local name="$1" i state
  for i in $(seq 1 90); do
    state="$(docker inspect -f '{{.State.Health.Status}}' "${name}" 2>/dev/null || echo none)"
    [[ "${state}" == "healthy" ]] && { pass "${name} container health check is healthy"; return 0; }
    sleep 1
  done
  fail "${name} container never became healthy (last state: ${state})"
}

start_api() {
  docker rm -f "${API_NAME}" >/dev/null 2>&1 || true
  docker run -d --name "${API_NAME}" --network host \
    -e PORT="${API_PORT}" -e FIRESTORE_EMULATOR_HOST \
    -v "${FAKE_ADC}:/tmp/smoke-adc.json:ro" -e GOOGLE_APPLICATION_CREDENTIALS=/tmp/smoke-adc.json \
    -e PROJECT_ID=demo-smoke -e VertexAI__ProjectId=demo-smoke -e FIREBASE_PROJECT_ID=demo-smoke \
    -e FIRESTORE_PROJECT_ID=demo-smoke -e Firebase__ProjectId=demo-smoke \
    "$@" "${API_IMAGE}" >/dev/null
  wait_for "http://127.0.0.1:${API_PORT}/health/live" "api"
}

start_web() {
  docker rm -f "${WEB_NAME}" >/dev/null 2>&1 || true
  docker run -d --name "${WEB_NAME}" --network host \
    -e PORT="${WEB_PORT}" -e BACKEND_URL="http://127.0.0.1:${API_PORT}" -e DISABLE_SERVERLESS_AUTH=true \
    "$@" "${WEB_IMAGE}" >/dev/null
  wait_for "http://127.0.0.1:${WEB_PORT}/" "web"
}

api="http://127.0.0.1:${API_PORT}"
web="http://127.0.0.1:${WEB_PORT}"

echo "API image, v2 routes off (the shipped default)"
start_api
expect_status 200 "GET /health/live" "${api}/health/live"
expect_status 200 "GET /health/ready (reaches Firestore)" "${api}/health/ready"
expect_status 200 "GET /api/workspace/config" "${api}/api/workspace/config"
expect_status 401 "GET /api/conversations without a token" "${api}/api/conversations"
expect_status 404 "GET /api/v2/me/settings (v2 dark)" "${api}/api/v2/me/settings"
wait_healthy "${API_NAME}"

echo "API image, v2 routes on"
start_api -e Runs__Enabled=true -e Runs__MaxDurationSeconds=270
expect_status 401 "GET /api/v2/me/settings without a token" "${api}/api/v2/me/settings"
expect_status 401 "POST /api/v2/runs without a token" -X POST -H 'content-type: application/json' -d '{}' "${api}/api/v2/runs"
expect_status 200 "GET /health/ready" "${api}/health/ready"

echo "Web image"
start_web
expect_status 200 "GET /healthz" "${web}/healthz"
expect_status 302 "GET / redirects" -I "${web}/"
location="$(curl -sI --max-time 10 "${web}/" | tr -d '\r' | awk 'tolower($1)=="location:" {print $2}')"
if [[ "${location}" == "/app/" ]]; then pass "/ redirects to /app/"; else fail "/ redirects to '${location}'"; fi
expect_status 404 "GET /legacy is gone" "${web}/legacy"
expect_status 308 "GET /app" -I "${web}/app"
expect_status 200 "GET /app/" "${web}/app/"
asset="$(curl -s --max-time 10 "${web}/app/" | grep -o '/app/assets/[^"]*\.js' | head -1 || true)"
if [[ -n "${asset}" ]]; then
  expect_status 200 "GET ${asset}" "${web}${asset}"
  if curl -sI --max-time 10 "${web}${asset}" | grep -qi 'cache-control: .*immutable'; then pass "hashed asset is cached immutable"; else fail "hashed asset is not immutable"; fi
else
  fail "the new UI page does not reference a built script"
fi
expect_status 200 "GET /api/workspace/config through the proxy" "${web}/api/workspace/config"
expect_status 401 "GET /api/conversations through the proxy" "${web}/api/conversations"
expect_status 401 "GET /api/v2/me/settings through the proxy (v2 on)" "${web}/api/v2/me/settings"
wait_healthy "${WEB_NAME}"

echo
if (( failures > 0 )); then
  echo "${failures} check(s) failed."
  exit 1
fi
echo "Container smoke checks passed."
