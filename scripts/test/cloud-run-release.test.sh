#!/usr/bin/env bash
# Tests scripts/lib/cloud-run-release.sh against a fake `gcloud`. Nothing here touches Google Cloud.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "$WORK"' EXIT
mkdir -p "$WORK/bin"
LOG="$WORK/gcloud.log"

cat > "$WORK/bin/gcloud" <<'FAKE'
#!/usr/bin/env bash
echo "gcloud $*" >> "$FAKE_LOG"
case "$*" in
  *"run services describe"*"status.traffic[0].revisionName"*)
    case "$*" in
      *vertex-ai-api*) echo "${FAKE_PREV_API:-}" ;;
      *vertex-ai-web*) echo "${FAKE_PREV_WEB:-}" ;;
    esac ;;
  *"run services describe"*"status.url"*)
    case "$*" in
      *vertex-ai-api*) echo "https://api.example.run.app" ;;
      *) echo "https://web.example.run.app" ;;
    esac ;;
  *"run services describe"*"--format=json"*) echo "${FAKE_WEB_JSON:-{\}}" ;;
  *"auth print-identity-token"*) echo "${FAKE_TOKEN-fake-id-token}" ;;
  *"run deploy vertex-ai-api"*) [[ "${FAKE_FAIL_DEPLOY:-}" == "api" ]] && exit 1 ;;
  *"run deploy vertex-ai-web"*) [[ "${FAKE_FAIL_DEPLOY:-}" == "web" ]] && exit 1 ;;
esac
exit 0
FAKE
chmod +x "$WORK/bin/gcloud"
export PATH="$WORK/bin:$PATH" FAKE_LOG="$LOG"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }
expect_log()     { grep -qE -- "$1" "$LOG" && pass "$2" || { fail "$2"; echo "  missing /$1/ in:"; sed 's/^/    /' "$LOG"; }; }
expect_no_log()  { grep -qE -- "$1" "$LOG" && { fail "$2"; grep -E -- "$1" "$LOG" | sed 's/^/    /'; } || pass "$2"; }

base_env() {
  export PROJECT_ID=proj REGION=asia-east1 REPOSITORY=repo API_SERVICE=vertex-ai-api WEB_SERVICE=vertex-ai-web
  export RUNTIME_SERVICE_ACCOUNT=run@proj.iam WEB_SERVICE_ACCOUNT=web@proj.iam WEB_INVOKER_SERVICE_ACCOUNT=web@proj.iam
  export IMAGE_TAG=abc123 FIREBASE_PROJECT_ID=fb FIRESTORE_PROJECT_ID=fb FIREBASE_API_KEY=public-web-key
  export FIREBASE_AUTH_DOMAIN=fb.firebaseapp.com FIREBASE_APP_ID=app DEFAULT_PROVIDER_ID=gemini ATTACHMENT_STORAGE_BUCKET=bucket
  export API_CPU=1 API_MEMORY=1Gi API_CONCURRENCY=20 API_TIMEOUT=300s API_MIN_INSTANCES=0 API_MAX_INSTANCES=3
  export WEB_CPU=1 WEB_MEMORY=512Mi WEB_CONCURRENCY=80 WEB_TIMEOUT=330s WEB_MIN_INSTANCES=0 WEB_MAX_INSTANCES=2
  export CRR_SMOKE_RETRIES=2 CRR_SMOKE_DELAY=0 DEPLOY_TARGET=all
  unset API_SECRETS ACCESS_ALLOWED_EMAILS ACCESS_ALLOWED_EMAIL_DOMAINS ACCESS_ALLOWED_UIDS ACCESS_REQUIRE_VERIFIED_EMAIL
  unset QUOTA_GLOBAL_DAILY_REQUEST_LIMIT QUOTA_GLOBAL_DAILY_TOKEN_LIMIT QUOTA_GLOBAL_DAILY_SEARCH_LIMIT FAKE_FAIL_DEPLOY
  unset CHAT_MAX_GENERATION_SECONDS DISABLE_SERVERLESS_AUTH FAKE_WEB_JSON FAKE_TOKEN
  export FAKE_PREV_API=api-00005-abc FAKE_PREV_WEB=web-00007-xyz
  SMOKE_API=403 SMOKE_WEB=200 SMOKE_CONFIG=200 CAND_WEB=200 CAND_CONFIG=200 CAND_API=200
  : > "$LOG"
}

# Run one release in a subshell so state never leaks between cases.
run_release() {
  (
    # shellcheck source=../lib/cloud-run-release.sh
    source "$ROOT/scripts/lib/cloud-run-release.sh"
    crr_http_status() {
      case "$1" in
        https://candidate---api*/health/ready) [[ -n "${2:-}" ]] && echo "$CAND_API" || echo 401 ;;
        https://candidate---*/api/workspace/config) echo "$CAND_CONFIG" ;;
        https://candidate---*) echo "$CAND_WEB" ;;
        */health/live) echo "$SMOKE_API" ;;
        */api/workspace/config) echo "$SMOKE_CONFIG" ;;
        *) echo "$SMOKE_WEB" ;;
      esac
    }
    crr_release
  ) > "$WORK/out.txt" 2>&1
}

echo "== static checks"
offenders="$(grep -rnE --include='*.sh' --include='*.yml' -e '--set-env-vars|--set-secrets' \
  "$ROOT/scripts/lib" "$ROOT/scripts/deploy-cloud-run.sh" "$ROOT/.github/workflows" | grep -vE ':[0-9]+:\s*#' || true)"
if [[ -n "$offenders" ]]; then
  fail "deploy paths must not use --set-env-vars/--set-secrets (they replace existing configuration)"
  echo "$offenders" | sed 's/^/    /'
else
  pass "no replace-style env/secret flags in deploy paths"
fi

echo "== successful release on an existing service"
base_env
run_release && pass "release succeeds" || { fail "release succeeds"; cat "$WORK/out.txt"; }
expect_log 'run deploy vertex-ai-api .*--no-traffic --tag=candidate' "API deployed without traffic"
expect_log 'run deploy vertex-ai-web .*--no-traffic --tag=candidate' "web deployed without traffic"
expect_log 'run deploy vertex-ai-api .*--update-env-vars=\^#\^PROJECT_ID=proj#' "API env merged with # delimiter"
expect_log 'run deploy vertex-ai-web .*--update-env-vars=\^#\^BACKEND_URL=https://api.example.run.app#UPSTREAM_TIMEOUT_MS=' "web env merged"
expect_log 'update-traffic vertex-ai-api .*--to-latest' "API promoted"
expect_log 'update-traffic vertex-ai-web .*--to-latest' "web promoted"
expect_no_log 'to-revisions' "no rollback on success"
expect_no_log 'update-secrets' "no secrets flag when API_SECRETS is empty"
expect_log 'remove-iam-policy-binding vertex-ai-api .*allUsers' "public invoker access removed from API"

echo "== candidates are deployed before anything is promoted"
first_promote=$(grep -n 'update-traffic .*--to-latest' "$LOG" | head -1 | cut -d: -f1)
last_deploy=$(grep -n 'run deploy' "$LOG" | tail -1 | cut -d: -f1)
[[ -n "$first_promote" && -n "$last_deploy" && "$last_deploy" -lt "$first_promote" ]] \
  && pass "all deploys precede the first promotion" || fail "all deploys precede the first promotion"

echo "== failed smoke test rolls both services back"
base_env; SMOKE_WEB=500
run_release && fail "release must fail when smoke fails" || pass "release fails when smoke fails"
expect_log 'update-traffic vertex-ai-api .*--to-revisions=api-00005-abc=100' "API rolled back to previous revision"
expect_log 'update-traffic vertex-ai-web .*--to-revisions=web-00007-xyz=100' "web rolled back to previous revision"

echo "== exposed API (not 403) also fails the release"
base_env; SMOKE_API=200
run_release && fail "public API must fail the release" || pass "public API fails the release"
expect_log 'to-revisions=api-00005-abc=100' "rollback after exposed API"

echo "== a failed deploy never promotes"
base_env; export FAKE_FAIL_DEPLOY=web
run_release && fail "failed deploy fails the release" || pass "failed deploy fails the release"
expect_no_log 'update-traffic .*--to-latest' "nothing promoted after failed deploy"
expect_no_log 'to-revisions' "nothing to roll back after failed deploy"

echo "== first deploy of a brand-new service"
base_env; export FAKE_PREV_API="" FAKE_PREV_WEB=""
run_release && pass "first release succeeds" || { fail "first release succeeds"; cat "$WORK/out.txt"; }
expect_no_log 'no-traffic' "first deploy takes traffic (nothing to protect)"

echo "== first deploy with failing smoke has no revision to roll back to"
base_env; export FAKE_PREV_API="" FAKE_PREV_WEB=""; SMOKE_WEB=500
run_release && fail "first release with failing smoke must fail" || pass "first release with failing smoke fails"
expect_no_log 'to-revisions' "no rollback target on first deploy"
grep -q "nothing to roll back to" "$WORK/out.txt" && pass "operator is told there is no rollback target" || fail "operator is told there is no rollback target"

echo "== secrets and optional settings"
base_env; export API_SECRETS="DEEPSEEK_API_KEY=deepseek-key:latest" ACCESS_ALLOWED_EMAILS="a@x.com,b@y.com" QUOTA_GLOBAL_DAILY_REQUEST_LIMIT=200
run_release || { fail "release with secrets"; cat "$WORK/out.txt"; }
expect_log '--update-secrets=DEEPSEEK_API_KEY=deepseek-key:latest' "secrets mounted by reference"
expect_log 'Access__AllowedEmailsCsv=a@x.com,b@y.com' "allow-list with commas survives the delimiter"
expect_log 'Quota__GlobalDailyRequestLimit=200' "global request cap passed"
expect_no_log 'deepseek-key[^:]' "no secret value can appear (only a reference)"

echo "== deploying only the web service"
base_env; export DEPLOY_TARGET=web
run_release || { fail "web-only release"; cat "$WORK/out.txt"; }
expect_no_log 'run deploy vertex-ai-api' "API untouched in web-only release"
expect_no_log 'update-traffic vertex-ai-api' "API traffic untouched in web-only release"

echo "== candidates are exercised BEFORE traffic moves"
base_env
run_release || { fail "release with candidate checks"; cat "$WORK/out.txt"; }
grep -q "candidate check: candidate---web.example.run.app/ -> 200" "$WORK/out.txt" && pass "web candidate tag URL checked" || fail "web candidate tag URL checked"
grep -q "candidate check: candidate---api.example.run.app/health/ready -> 200" "$WORK/out.txt" && pass "API candidate checked with an identity token" || fail "API candidate checked with an identity token"

base_env; CAND_WEB=500
run_release && fail "bad web candidate must block the release" || pass "bad web candidate blocks the release"
expect_no_log 'update-traffic' "no traffic moved for a bad web candidate"
grep -q "live revisions are untouched" "$WORK/out.txt" && pass "operator told live revisions are untouched" || fail "operator told live revisions are untouched"

base_env; CAND_API=503
run_release && fail "bad API candidate must block the release" || pass "bad API candidate blocks the release"
expect_no_log 'update-traffic' "no traffic moved for a bad API candidate"

base_env; export FAKE_TOKEN=""
run_release && pass "missing identity token only skips the private API check" || { fail "missing identity token only skips the private API check"; cat "$WORK/out.txt"; }
grep -q "private API candidate was not exercised" "$WORK/out.txt" && pass "skip is reported loudly" || fail "skip is reported loudly"
expect_log 'update-traffic vertex-ai-api .*--to-latest' "release continues to promotion (smoke + rollback still protect)"

base_env; export FAKE_PREV_API="" FAKE_PREV_WEB=""
run_release || fail "first deploy"
grep -q "candidate check" "$WORK/out.txt" && fail "first deploy has no candidate to test" || pass "first deploy skips candidate checks (nothing is live to protect)"

echo "== local-only auth bypass can never reach production"
base_env; export DISABLE_SERVERLESS_AUTH=true
run_release && fail "bypass flag in deploy env must abort" || pass "bypass flag in deploy env aborts"
expect_no_log 'run deploy' "nothing deployed when the bypass flag is set"

base_env; export FAKE_WEB_JSON='{"spec":{"template":{"spec":{"containers":[{"env":[{"name":"BACKEND_URL"},{"name":"DISABLE_SERVERLESS_AUTH","value":"true"}]}]}}}}'
run_release && fail "bypass flag on the service must abort" || pass "bypass flag already on the service aborts"
expect_no_log 'update-traffic' "no traffic moved while the bypass flag is present"
grep -q "remove-env-vars=DISABLE_SERVERLESS_AUTH" "$WORK/out.txt" && pass "operator gets the exact fix command" || fail "operator gets the exact fix command"

echo "== API_SECRETS must be references, never values"
base_env; export API_SECRETS="DEEPSEEK_API_KEY=sk-1234567890abcdef"
run_release && fail "a raw secret value must be rejected" || pass "a raw secret value is rejected"
grep -q "sk-1234567890abcdef" "$WORK/out.txt" "$LOG" && fail "rejected value must not be echoed anywhere" || pass "rejected value is not echoed to the log"
expect_no_log 'run deploy' "nothing deployed with an invalid secrets spec"
for bad in "lower=name:1" "A=b" "A=b:v1" "A=b:latest,C=d"; do
  base_env; export API_SECRETS="$bad"
  run_release && fail "invalid API_SECRETS '$bad' accepted" || pass "invalid API_SECRETS rejected: $bad"
done
base_env; export API_SECRETS="A_KEY=my-secret:12,B_KEY=other_secret:latest"
run_release && pass "valid references accepted" || fail "valid references accepted"

echo "== timeout layering keeps clean-up margins"
base_env
run_release || fail "default timeouts"
expect_log 'Chat__MaxGenerationSeconds=240' "generation cap passed to the API"
expect_log 'UPSTREAM_TIMEOUT_MS=315000' "proxy cap = web timeout - 15s passed to the web service"
for combo in "API_TIMEOUT=300s WEB_TIMEOUT=300s" "API_TIMEOUT=300s WEB_TIMEOUT=315s" "API_TIMEOUT=300s WEB_TIMEOUT=330s CHAT_MAX_GENERATION_SECONDS=280"; do
  base_env; for kv in $combo; do export "$kv"; done
  run_release && fail "must reject: $combo" || pass "rejects: $combo"
  expect_no_log 'run deploy' "nothing deployed for: $combo"
done
base_env; export API_TIMEOUT=600s WEB_TIMEOUT=660s CHAT_MAX_GENERATION_SECONDS=500
run_release && pass "a consistent longer configuration is accepted" || { fail "a consistent longer configuration is accepted"; cat "$WORK/out.txt"; }
expect_log 'UPSTREAM_TIMEOUT_MS=645000' "proxy cap follows the web timeout"

echo "== open-access warning"
base_env
run_release; grep -q "registration is OPEN" "$WORK/out.txt" && pass "warns when no access policy is configured" || fail "warns when no access policy is configured"
base_env; export ACCESS_ALLOWED_EMAILS="me@example.com"
run_release; grep -q "registration is OPEN" "$WORK/out.txt" && fail "no warning when a policy is set" || pass "no warning when a policy is set"

echo
if (( failures > 0 )); then echo "$failures check(s) failed"; exit 1; fi
echo "all release checks passed"
