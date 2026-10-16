#!/usr/bin/env bash
# Tests scripts/lib/cloud-run-release.sh against a STATEFUL fake `gcloud` that models serving revisions,
# the latest revision, and the API's IAM policy. Nothing here touches Google Cloud.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
WORK="$(mktemp -d)"
trap 'rm -rf "${WORK:?}"' EXIT
mkdir -p "$WORK/bin" "$WORK/state"
LOG="$WORK/gcloud.log"
export FAKE_STATE="$WORK/state" FAKE_LOG="$LOG"

cat > "$WORK/bin/gcloud" <<'FAKE'
#!/usr/bin/env bash
echo "gcloud $*" >> "$FAKE_LOG"
S="$FAKE_STATE"
svc() { case "$*" in *vertex-ai-api*) echo api ;; *vertex-ai-web*) echo web ;; esac; }
contains() { [[ " ${1:-} " == *" $2 "* ]]; }
read_state() { cat "$S/$1" 2>/dev/null || true; }
name=$(svc "$@")

case "$*" in
  "run services describe"*"--format=json"*)
    serving=$(read_state "serving_$name"); latest=$(read_state "latest_$name")
    [[ -z "$serving" ]] && exit 1
    extra=""; [[ "$name" == "web" && -n "${FAKE_WEB_ENV_EXTRA:-}" ]] && extra=",${FAKE_WEB_ENV_EXTRA}"
    # tagged 0% entry deliberately FIRST: callers must not rely on traffic[0]
    cat <<JSON
{"spec":{"template":{"spec":{"containers":[{"env":[{"name":"BACKEND_URL"}${extra}]}]}}},
 "status":{"latestReadyRevisionName":"$latest","url":"https://$name.example.run.app",
  "traffic":[{"tag":"candidate","revisionName":"$latest","percent":0},{"revisionName":"$serving","percent":100}]}}
JSON
    ;;
  "run services describe"*"status.latestReadyRevisionName"*) read_state "latest_$name" ;;
  "run services describe"*"status.url"*) echo "https://$name.example.run.app" ;;
  "run revisions describe"*)
    rev="${3:-}"; for a in "$@"; do case "$a" in vertex-ai-*|api-*|web-*) rev="$a" ;; esac; done
    read_state "rev_image_$rev" ;;
  "run deploy"*)
    contains "${FAKE_FAIL_DEPLOY:-}" "$name" && exit 1
    n=$(( $(read_state "deploys_$name" | tr -d '\n' | wc -c) + 1 )); printf x >> "$S/deploys_$name"
    new="$name-new-$n"
    img=""; for a in "$@"; do case "$a" in --image=*) img="${a#--image=}" ;; esac; done
    echo "$img" > "$S/rev_image_$new"
    echo "$new" > "$S/latest_$name"
    [[ "$*" != *"--no-traffic"* ]] && echo "$new" > "$S/serving_$name"
    ;;
  "run services update-traffic"*"--to-latest"*)
    contains "${FAKE_PROMOTE_FAILS:-}" "$name" && { echo "ERROR: update-traffic failed" >&2; exit 1; }
    if contains "${FAKE_PROMOTE_APPLIES_THEN_FAILS:-}" "$name"; then read_state "latest_$name" > "$S/serving_$name"; exit 1; fi
    contains "${FAKE_PROMOTE_NOOP:-}" "$name" && exit 0
    read_state "latest_$name" > "$S/serving_$name"
    ;;
  "run services update-traffic"*"--remove-tags"*) exit "${FAKE_REMOVE_TAGS_RC:-0}" ;;
  "run services update-traffic"*"--to-revisions="*)
    contains "${FAKE_ROLLBACK_FAILS:-}" "$name" && exit 1
    args="$*"; rev="${args#*--to-revisions=}"; rev="${rev%%=100*}"
    echo "$rev" > "$S/serving_$name"
    ;;
  "run services get-iam-policy"*)
    if [[ "$*" == *"allUsers"* ]]; then [[ "${FAKE_API_PUBLIC:-0}" == "1" ]] && echo "roles/run.invoker"; exit 0; fi
    [[ "$(read_state binding)" == "1" ]] && echo "roles/run.invoker"; exit 0 ;;
  "storage buckets get-iam-policy"*) [[ "$(read_state bucket_binding)" == "1" ]] && echo "projects/proj/roles/attachments"; exit 0 ;;
  "storage buckets add-iam-policy-binding"*) echo 1 > "$S/bucket_binding" ;;
  "run services add-iam-policy-binding"*) echo 1 > "$S/binding" ;;
  "run services remove-iam-policy-binding"*) echo 0 > "$S/binding" ;;
  "auth print-identity-token"*) echo "${FAKE_TOKEN-fake-id-token}" ;;
esac
exit 0
FAKE
chmod +x "$WORK/bin/gcloud"
export PATH="$WORK/bin:$PATH"

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }
expect_log()     { grep -qE -- "$1" "$LOG" && pass "$2" || { fail "$2"; echo "  missing /$1/ in:"; sed 's/^/    /' "$LOG" | cut -c1-200; }; }
expect_no_log()  { grep -qE -- "$1" "$LOG" && { fail "$2"; grep -E -- "$1" "$LOG" | sed 's/^/    /' | cut -c1-200; } || pass "$2"; }
expect_out()     { grep -qE -- "$1" "$WORK/out.txt" && pass "$2" || { fail "$2"; sed 's/^/    /' "$WORK/out.txt" | tail -15; }; }
expect_serving() { [[ "$(cat "$FAKE_STATE/serving_$1" 2>/dev/null)" == "$2" ]] && pass "$3" || fail "$3 (serving '$(cat "$FAKE_STATE/serving_$1" 2>/dev/null)', want '$2')"; }
expect_binding() { [[ "$(cat "$FAKE_STATE/binding" 2>/dev/null)" == "$1" ]] && pass "$2" || fail "$2"; }
line_of()        { grep -nE -- "$1" "$LOG" | head -1 | cut -d: -f1; }

reset_state() { rm -f "${FAKE_STATE:?}"/*; }

base_env() { # $1 = production (default) | staging
  local kind="${1:-production}"
  export CRR_ENV="$kind"
  if [[ "$kind" == "staging" ]]; then
    export API_SERVICE=vertex-ai-api-staging WEB_SERVICE=vertex-ai-web-staging ATTACHMENT_STORAGE_BUCKET=bucket-staging
    export FIREBASE_PROJECT_ID=stg-fb FIRESTORE_PROJECT_ID=stg-fb
  else
    export API_SERVICE=vertex-ai-api WEB_SERVICE=vertex-ai-web ATTACHMENT_STORAGE_BUCKET=bucket
    export FIREBASE_PROJECT_ID=fb FIRESTORE_PROJECT_ID=fb
  fi
  export PRODUCTION_FIREBASE_PROJECT_ID=fb PRODUCTION_FIRESTORE_PROJECT_ID=fb PRODUCTION_ATTACHMENT_STORAGE_BUCKET=bucket
  export PROJECT_ID=proj REGION=asia-east1 REPOSITORY=repo
  export RUNTIME_SERVICE_ACCOUNT=run@proj.iam WEB_SERVICE_ACCOUNT=web@proj.iam WEB_INVOKER_SERVICE_ACCOUNT=web@proj.iam
  export IMAGE_TAG=abc123 FIREBASE_API_KEY=public-web-key
  export FIREBASE_AUTH_DOMAIN=fb.firebaseapp.com FIREBASE_APP_ID=app DEFAULT_PROVIDER_ID=gemini
  export ATTACHMENT_STORAGE_ROLE=projects/proj/roles/attachments
  export API_CPU=1 API_MEMORY=1Gi API_CONCURRENCY=20 API_TIMEOUT=300s API_MIN_INSTANCES=0 API_MAX_INSTANCES=3
  export WEB_CPU=1 WEB_MEMORY=512Mi WEB_CONCURRENCY=80 WEB_TIMEOUT=330s WEB_MIN_INSTANCES=0 WEB_MAX_INSTANCES=2
  export CRR_SMOKE_RETRIES=2 CRR_SMOKE_DELAY=0 DEPLOY_TARGET=all
  unset CRR_MODE CRR_CONFIRM_PROMOTE CRR_ACCEPTANCE_REF
  unset API_SECRETS ACCESS_ALLOWED_EMAILS ACCESS_ALLOWED_EMAIL_DOMAINS ACCESS_ALLOWED_UIDS ACCESS_REQUIRE_VERIFIED_EMAIL
  unset QUOTA_GLOBAL_DAILY_REQUEST_LIMIT QUOTA_GLOBAL_DAILY_TOKEN_LIMIT QUOTA_GLOBAL_DAILY_SEARCH_LIMIT
  unset CHAT_MAX_GENERATION_SECONDS DISABLE_SERVERLESS_AUTH CRR_IDENTITY_TOKEN_SA CRR_ALLOW_UNVERIFIED_API_CANDIDATE
  unset FAKE_FAIL_DEPLOY FAKE_WEB_ENV_EXTRA FAKE_TOKEN FAKE_PROMOTE_FAILS FAKE_PROMOTE_APPLIES_THEN_FAILS FAKE_PROMOTE_NOOP
  unset FAKE_REMOVE_TAGS_RC FAKE_ROLLBACK_FAILS FAKE_API_PUBLIC
  # existing services: both serve an old revision built from an OLD commit; the web invoker grant is present
  reset_state
  echo api-00005 > "$FAKE_STATE/serving_api"; echo api-00005 > "$FAKE_STATE/latest_api"; echo "repo/vertex-ai-api:old000" > "$FAKE_STATE/rev_image_api-00005"
  echo web-00007 > "$FAKE_STATE/serving_web"; echo web-00007 > "$FAKE_STATE/latest_web"; echo "repo/vertex-ai-web:old000" > "$FAKE_STATE/rev_image_web-00007"
  echo 1 > "$FAKE_STATE/binding"
  SMOKE_API=403 SMOKE_WEB=200 SMOKE_CONFIG=200 CAND_WEB=200 CAND_CONFIG=200 CAND_API=200
  : > "$LOG"
}
fresh_services() { rm -f "${FAKE_STATE:?}"/serving_* "${FAKE_STATE:?}"/latest_*; echo 0 > "$FAKE_STATE/binding"; }

# One invocation of the release library in a subshell, so state never leaks between invocations.
run_mode() { # $1 = mode (empty = leave CRR_MODE unset: the default)
  (
    [[ -n "${1:-}" ]] && export CRR_MODE="$1"
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
run_prepare() { run_mode prepare; }
confirm() { export CRR_CONFIRM_PROMOTE="promote:${CRR_ENV}:${IMAGE_TAG}" CRR_ACCEPTANCE_REF="https://example.invalid/acceptance/1"; }
run_promote() { run_mode promote; }
# prepare, then (with the operator's confirmation) promote - the whole release
run_release() { run_prepare && { confirm; run_promote; }; }
mutating_calls() { grep -cE 'builds submit|run deploy|update-traffic|iam-policy-binding|services update' "$LOG"; }
expect_untouched() { [[ "$(mutating_calls)" == "0" ]] && pass "$1" || { fail "$1"; grep -E 'builds submit|run deploy|update-traffic|iam-policy-binding' "$LOG" | sed 's/^/    /' | cut -c1-160; }; }
expect_log_empty() { [[ ! -s "$LOG" ]] && pass "$1" || { fail "$1"; sed 's/^/    /' "$LOG" | cut -c1-160; }; }

echo "== static checks"
offenders="$(grep -rnE --include='*.sh' --include='*.yml' -e '--set-env-vars|--set-secrets' \
  "$ROOT/scripts/lib" "$ROOT/scripts/deploy-cloud-run.sh" "$ROOT/.github/workflows" | grep -vE ':[0-9]+:\s*#' || true)"
[[ -z "$offenders" ]] && pass "no replace-style env/secret flags in deploy paths" || { fail "no replace-style env/secret flags in deploy paths"; echo "$offenders"; }

echo "== successful release on existing services"
base_env
run_release && pass "release succeeds" || { fail "release succeeds"; cat "$WORK/out.txt"; }
expect_log 'run deploy vertex-ai-api .*--no-traffic --tag=candidate' "API deployed without traffic"
expect_log 'run deploy vertex-ai-web .*--no-traffic --tag=candidate' "web deployed without traffic"
expect_log 'run deploy vertex-ai-api .*--update-env-vars=\^#\^PROJECT_ID=proj#' "API env merged with # delimiter"
expect_log 'run deploy vertex-ai-web .*--update-env-vars=\^#\^BACKEND_URL=https://api.example.run.app#UPSTREAM_TIMEOUT_MS=' "web env merged"
expect_serving api api-new-1 "API ends up serving the NEW revision"
expect_serving web web-new-1 "web ends up serving the NEW revision"
expect_no_log 'to-revisions' "no rollback on success"
expect_no_log 'update-secrets' "no secrets flag when API_SECRETS is empty"
last_deploy=$(grep -n 'run deploy' "$LOG" | tail -1 | cut -d: -f1); first_promote=$(line_of 'update-traffic .*--to-latest')
[[ "$last_deploy" -lt "$first_promote" ]] && pass "all deploys precede the first traffic switch" || fail "all deploys precede the first traffic switch"

echo "== failed traffic switches (the crr_promote error-propagation bug)"
base_env; export FAKE_PROMOTE_FAILS="api"
run_release && fail "API switch failure must fail the release" || pass "API switch failure fails the release"
expect_no_log 'update-traffic vertex-ai-web .*--to-latest' "web is not switched after the API switch failed"
expect_serving api api-00005 "API still serves the old revision"
expect_serving web web-00007 "web still serves the old revision"
expect_out "Traffic switch FAILED for vertex-ai-api" "failure is reported"
expect_log 'update-traffic vertex-ai-api .*--to-revisions=api-00005=100' "rollback is attempted for the service whose switch failed"

base_env; export FAKE_PROMOTE_FAILS="web"
run_release && fail "web switch failure must fail the release" || pass "web switch failure fails the release"
expect_serving api api-00005 "API (switched earlier) is rolled back"
expect_serving web web-00007 "web keeps the old revision"
expect_log 'update-traffic vertex-ai-api .*--to-revisions=api-00005=100' "partial success: the already-switched API is restored"
expect_out "Traffic switch FAILED for vertex-ai-web" "web failure is reported"

base_env; export FAKE_PROMOTE_APPLIES_THEN_FAILS="api"
run_release && fail "an ambiguous failure (applied but errored) must fail" || pass "ambiguous API switch fails the release"
expect_serving api api-00005 "API is restored even though the failing command had taken effect"

base_env; export FAKE_PROMOTE_NOOP="api"
run_release && fail "a switch that exits 0 but changes nothing must fail" || pass "switch that did nothing fails the release"
expect_out "did not take effect" "operator is told the switch did not take effect"
expect_serving api api-00005 "API unchanged"
expect_serving web web-00007 "web unchanged (never switched)"

base_env; export FAKE_PROMOTE_NOOP="web"
run_release && fail "healthy OLD web must not be mistaken for a successful release" || pass "smoke success on the old revision is not release success"
expect_serving api api-00005 "API rolled back after web verification failed"

base_env; export FAKE_REMOVE_TAGS_RC=1
run_release && pass "failing to remove the cosmetic tag does not fail a good release" || { fail "tag removal must not fail a good release"; cat "$WORK/out.txt"; }
expect_serving api api-new-1 "good release still switched"
base_env; export FAKE_REMOVE_TAGS_RC=1 FAKE_PROMOTE_FAILS="api"
run_release && fail "tag removal must not mask a failed switch" || pass "a failed switch is not masked by the tag step"

base_env; SMOKE_WEB=500
run_release && fail "release must fail when smoke fails" || pass "smoke failure fails the release"
expect_serving api api-00005 "API rolled back after smoke failure"
expect_serving web web-00007 "web rolled back after smoke failure"
base_env; SMOKE_API=200
run_release && fail "public API must fail the release" || pass "directly reachable API fails the release"
expect_serving api api-00005 "API rolled back after exposure check"

base_env; export FAKE_ROLLBACK_FAILS="api"; SMOKE_WEB=500
run_release && fail "failed rollback must fail the release" || pass "failed rollback fails the release"
expect_out "ROLLBACK FAILED for vertex-ai-api" "failed rollback is loud and prints the manual command"
expect_out "PRODUCTION MAY BE ON THE NEW REVISION" "operator told production may be on the new revision"
expect_serving web web-00007 "the other service is still restored"

echo "== nothing outside traffic changes before the candidates pass"
base_env
run_release || fail "release"
expect_no_log 'allow-unauthenticated' "existing services: no SERVICE-level IAM flags on deploy"
expect_no_log 'iam-policy-binding' "grant already present: no IAM change at all"

base_env; echo 0 > "$FAKE_STATE/binding"
run_release || { fail "release with missing grant"; cat "$WORK/out.txt"; }
expect_log 'add-iam-policy-binding vertex-ai-api .*serviceAccount:web@proj.iam' "missing invoker grant is added"
add_line=$(line_of 'add-iam-policy-binding'); last_deploy=$(grep -n 'run deploy' "$LOG" | tail -1 | cut -d: -f1); first_promote=$(line_of 'update-traffic .*--to-latest')
[[ "$add_line" -gt "$last_deploy" && "$add_line" -lt "$first_promote" ]] && pass "grant happens after deploys and right before the switch" || fail "grant ordering (deploy=$last_deploy add=$add_line promote=$first_promote)"
expect_binding 1 "grant stays after a successful release"
expect_no_log 'remove-iam-policy-binding' "nothing is ever removed automatically"

base_env; echo 0 > "$FAKE_STATE/binding"; CAND_WEB=500
run_release && fail "bad candidate" || pass "bad candidate blocks the release"
expect_no_log 'iam-policy-binding' "a rejected candidate caused ZERO IAM changes"
expect_no_log 'update-traffic' "a rejected candidate moved no traffic"

base_env; echo 0 > "$FAKE_STATE/binding"; export FAKE_PROMOTE_FAILS="api"
run_release && fail "failed switch" || pass "failed switch fails the release"
expect_binding 0 "the grant THIS release added is taken away on rollback"
base_env; export FAKE_PROMOTE_FAILS="api"
run_release && fail "failed switch" || true
expect_binding 1 "a grant that existed before is NOT touched by rollback"

base_env; export FAKE_API_PUBLIC=1
run_release && fail "public API must stop the release" || pass "a public API stops the release in preflight"
expect_no_log 'run deploy' "preflight stops before anything is built or deployed"
expect_no_log 'iam-policy-binding' "a public API is never changed silently"
expect_out "remove-iam-policy-binding vertex-ai-api" "operator gets the deliberate fix command"

base_env; export DEPLOY_TARGET=web
run_release || { fail "web-only release"; cat "$WORK/out.txt"; }
expect_no_log 'run deploy vertex-ai-api' "API untouched in web-only release"
expect_no_log 'get-iam-policy|iam-policy-binding' "no API IAM activity in web-only release"
expect_serving api api-00005 "API revision unchanged"

echo "== pre-release infrastructure is additive: a grant that exists is never re-applied"
base_env
( source "$ROOT/scripts/lib/cloud-run-release.sh"; crr_ensure_bucket_access ) > "$WORK/out.txt" 2>&1
expect_log 'storage buckets add-iam-policy-binding gs://bucket .*serviceAccount:run@proj.iam' "missing bucket grant is added"
: > "$LOG"
( source "$ROOT/scripts/lib/cloud-run-release.sh"; crr_ensure_bucket_access ) > "$WORK/out.txt" 2>&1
expect_no_log 'add-iam-policy-binding' "second run: existing bucket grant is left alone (idempotent)"
expect_out "already present" "operator sees why nothing changed"

echo "== first deploy of brand-new STAGING services (production services are never created)"
base_env staging; fresh_services
run_prepare && pass "first staging prepare succeeds" || { fail "first staging prepare succeeds"; cat "$WORK/out.txt"; }
expect_log 'run deploy vertex-ai-api[a-z-]* .*--no-allow-unauthenticated' "new API is created private"
expect_log 'run deploy vertex-ai-web[a-z-]* .*--allow-unauthenticated' "new web is created public"
expect_no_log 'no-traffic' "first deploy takes traffic (nothing live to protect)"
expect_log 'add-iam-policy-binding vertex-ai-api' "web invoker grant is created on first deploy"
grep -q "candidate check" "$WORK/out.txt" && fail "first deploy has no candidate to test" || pass "first deploy skips candidate checks"

expect_out "nothing to roll back to" "operator told a new service has no earlier revision to fall back to"
expect_out "already serving" "operator told the new staging services are already serving"
base_env staging; fresh_services
run_prepare && confirm && run_promote && pass "promote after a first deploy is an idempotent no-op" || fail "promote after a first deploy is an idempotent no-op"
expect_out "Nothing to promote" "nothing to switch: the new services already serve this commit"
expect_no_log 'update-traffic' "no traffic command for services that already serve the commit"

echo "== candidates are exercised before traffic moves"
base_env
run_release || { fail "release with candidate checks"; cat "$WORK/out.txt"; }
expect_out "candidate check: candidate---web.example.run.app/ -> 200" "web candidate tag URL checked"
expect_out "candidate check: candidate---api.example.run.app/health/ready -> 200" "API candidate checked with an identity token"
base_env; CAND_WEB=500
run_release && fail "bad web candidate" || pass "bad web candidate blocks the release"
expect_no_log 'update-traffic' "no traffic moved for a bad web candidate"
expect_out "No traffic was moved" "operator told no traffic was moved"
base_env; CAND_API=503
run_release && fail "bad API candidate" || pass "bad API candidate blocks the release"
expect_no_log 'update-traffic' "no traffic moved for a bad API candidate"

echo "== no identity token for the private API candidate"
base_env; export FAKE_TOKEN=""
run_release && fail "must STOP when the API candidate cannot be tested" || pass "missing identity token STOPS the release"
expect_no_log 'update-traffic' "nothing was switched"
expect_no_log 'iam-policy-binding' "no IAM change either"
expect_out "no identity token" "reason is stated"
expect_out "CRR_IDENTITY_TOKEN_SA" "the supported fix is named"
base_env; export FAKE_TOKEN="" CRR_ALLOW_UNVERIFIED_API_CANDIDATE=true
run_release && pass "explicit override proceeds" || { fail "explicit override proceeds"; cat "$WORK/out.txt"; }
expect_out "NOT exercised" "override is logged loudly"
base_env; export FAKE_TOKEN="" DEPLOY_TARGET=web
run_release && pass "web-only release needs no API token" || fail "web-only release needs no API token"
base_env; export CRR_IDENTITY_TOKEN_SA="tokens@proj.iam"
run_release || fail "impersonation release"
expect_log 'print-identity-token --impersonate-service-account=tokens@proj.iam --audiences=https://api.example.run.app' "token is minted by impersonation for the API audience"

echo "== local-only auth bypass can never reach production"
base_env; export DISABLE_SERVERLESS_AUTH=true
run_release && fail "bypass flag in deploy env must abort" || pass "bypass flag in deploy env aborts"
expect_no_log 'run deploy' "nothing deployed when the bypass flag is set"
base_env; export FAKE_WEB_ENV_EXTRA='{"name":"DISABLE_SERVERLESS_AUTH","value":"true"}'
run_release && fail "bypass flag on the service must abort" || pass "bypass flag already on the service aborts"
expect_no_log 'update-traffic' "no traffic moved while the bypass flag is present"
expect_out "remove-env-vars=DISABLE_SERVERLESS_AUTH" "operator gets the exact fix command"

echo "== API_SECRETS must be references, never values"
base_env; export API_SECRETS="DEEPSEEK_API_KEY=sk-1234567890abcdef"
run_release && fail "a raw secret value must be rejected" || pass "a raw secret value is rejected"
grep -q "sk-1234567890abcdef" "$WORK/out.txt" "$LOG" && fail "rejected value must not be echoed anywhere" || pass "rejected value is not echoed to the log"
expect_no_log 'run deploy' "nothing deployed with an invalid secrets spec"
for bad in "lower=name:1" "A=b" "A=b:v1" "A=b:latest,C=d"; do
  base_env; export API_SECRETS="$bad"
  run_release && fail "invalid API_SECRETS '$bad' accepted" || pass "invalid API_SECRETS rejected: $bad"
done
base_env; export API_SECRETS="DEEPSEEK_API_KEY=deepseek-key:3,B_KEY=other_secret:latest" ACCESS_ALLOWED_EMAILS="a@x.com,b@y.com" QUOTA_GLOBAL_DAILY_REQUEST_LIMIT=200
run_release || { fail "release with secrets"; cat "$WORK/out.txt"; }
expect_log '--update-secrets=DEEPSEEK_API_KEY=deepseek-key:3,B_KEY=other_secret:latest' "secrets mounted by reference"
expect_log 'Access__AllowedEmailsCsv=a@x.com,b@y.com' "allow-list with commas survives the delimiter"
expect_log 'Quota__GlobalDailyRequestLimit=200' "global request cap passed"

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
run_release && pass "a consistent longer configuration is accepted" || fail "a consistent longer configuration is accepted"
expect_log 'UPSTREAM_TIMEOUT_MS=645000' "proxy cap follows the web timeout"

echo "== PREPARE is the default and can never move traffic"
for env in production staging; do
  base_env $env
  run_mode "" && pass "[$env] default mode succeeds" || { fail "[$env] default mode succeeds"; cat "$WORK/out.txt"; }
  expect_out "mode=prepare" "[$env] the default mode IS prepare"
  expect_out "PREPARED" "[$env] operator is told this is only prepared"
  expect_out "NOT business acceptance" "[$env] smoke checks are not presented as acceptance"
  expect_no_log 'update-traffic' "[$env] prepare issued no traffic command at all"
  expect_serving api api-00005 "[$env] API still serves the old revision after prepare"
  expect_serving web web-00007 "[$env] web still serves the old revision after prepare"
  expect_log 'run deploy .*--no-traffic --tag=candidate' "[$env] candidates were deployed with no traffic"
done
base_env production; echo 0 > "$FAKE_STATE/binding"
run_prepare || fail "production prepare with a missing grant"
expect_no_log 'iam-policy-binding' "production prepare changes NO IAM, even when the web grant is missing"
base_env staging; echo 0 > "$FAKE_STATE/binding"
run_prepare || fail "staging prepare with a missing grant"
expect_log 'add-iam-policy-binding .*serviceAccount:web@proj.iam' "staging prepare may add the web grant (staging only)"
for t in api web; do
  base_env production; export DEPLOY_TARGET=$t
  run_prepare || fail "prepare $t"
  expect_no_log 'update-traffic' "prepare of only $t moved no traffic"
done
base_env production; CAND_WEB=500
run_prepare && fail "bad candidate" || pass "failing smoke makes prepare fail"
expect_no_log 'update-traffic|iam-policy-binding' "failing prepare changed neither traffic nor IAM"

echo "== invalid mode or environment stops before anything happens"
base_env; export CRR_MODE=deploy
run_mode "" && fail "unknown mode must be rejected" || pass "unknown mode is rejected"
expect_out "Unknown CRR_MODE" "reason stated"
expect_log_empty "no gcloud call at all for an unknown mode"
base_env; export CRR_ENV=prod
run_mode prepare && fail "unknown env must be rejected" || pass "unknown environment is rejected"
expect_log_empty "no gcloud call at all for an unknown environment"

echo "== PROMOTE needs explicit, commit-bound confirmation (checked before any gcloud call)"
base_env; run_prepare || fail "prepare"
: > "$LOG"
run_promote && fail "promote without confirmation" || pass "promote without confirmation is refused"
expect_out "promote:production:abc123" "the exact phrase is shown to the operator"
expect_log_empty "no gcloud call at all without confirmation"
export CRR_CONFIRM_PROMOTE="promote:staging:abc123" CRR_ACCEPTANCE_REF=x
run_promote && fail "confirmation for another environment" || pass "a confirmation for ANOTHER environment is refused"
expect_log_empty "no gcloud call with a wrong-environment confirmation"
export CRR_CONFIRM_PROMOTE="promote:production:deadbee"
run_promote && fail "confirmation for another commit" || pass "a confirmation for ANOTHER commit is refused"
expect_log_empty "no gcloud call with a wrong-commit confirmation"
export CRR_CONFIRM_PROMOTE="promote:production:abc123"; unset CRR_ACCEPTANCE_REF
run_promote && fail "production promote needs an acceptance reference" || pass "production promote without an acceptance reference is refused"
expect_out "CRR_ACCEPTANCE_REF" "the missing acceptance reference is named"
expect_log_empty "no gcloud call without an acceptance reference"
expect_serving api api-00005 "nothing moved after all those refusals"
confirm
run_promote && pass "with the correct confirmation the prepared candidates are promoted" || { fail "correct confirmation promotes"; cat "$WORK/out.txt"; }
expect_serving api api-new-1 "API serves the prepared revision"
expect_serving web web-new-1 "web serves the prepared revision"
expect_out "Acceptance reference" "the acceptance reference is recorded in the log"
expect_no_log 'builds submit|run deploy' "promote built and deployed nothing"

echo "== PROMOTE only promotes what was prepared for the confirmed commit"
base_env; confirm; : > "$LOG"
run_promote && fail "nothing prepared" || pass "promote with nothing prepared is refused"
expect_out "Run prepare for this commit first" "operator is told to prepare first"
expect_untouched "nothing was changed"
base_env; run_prepare || fail "prepare"; export IMAGE_TAG=zzz999; confirm; : > "$LOG"
run_promote && fail "other commit" || pass "a candidate built from another commit is refused"
expect_untouched "nothing was changed for a commit mismatch"
base_env; run_release || fail "release"; : > "$LOG"; confirm
run_promote && pass "second promote of the same commit is a safe no-op" || fail "second promote"
expect_out "Nothing to promote" "idempotent: nothing left to promote"
expect_no_log 'update-traffic' "no traffic command on the repeated promote"
base_env; export DEPLOY_TARGET=web; run_prepare || fail "prepare web"; confirm
run_promote && pass "only the prepared service is promoted" || { fail "partial prepare promote"; cat "$WORK/out.txt"; }
expect_serving web web-new-1 "the prepared web is promoted"
expect_serving api api-00005 "the API that was never prepared is untouched"
expect_no_log 'update-traffic vertex-ai-api' "no traffic command for the unprepared API"
base_env; export DEPLOY_TARGET=web; run_prepare || fail "prepare web"; export DEPLOY_TARGET=all; confirm; : > "$LOG"
run_promote && fail "all with an unprepared API" || pass "promoting 'all' while one service was never prepared is refused"
expect_untouched "nothing changed when part of the release was never prepared"

echo "== PROMOTE rolls back (modes split)"
base_env; run_prepare || fail "prepare"; confirm; export FAKE_PROMOTE_FAILS="web"
run_promote && fail "web switch fails" || pass "web switch failure fails the promotion"
expect_serving api api-00005 "API restored after the web switch failed"
expect_serving web web-00007 "web unchanged"
base_env; run_prepare || fail "prepare"; confirm; SMOKE_WEB=500
run_promote && fail "smoke fails" || pass "post-promotion smoke failure fails the promotion"
expect_serving api api-00005 "API restored after smoke failure"
expect_serving web web-00007 "web restored after smoke failure"
echo "== the web candidate is re-tested against the NEW api before the web switch"
base_env; run_prepare || fail "prepare"; confirm
# first web candidate check (before the API switch) passes, the second (after it) fails
echo 0 > "$WORK/cfgcount"
( source "$ROOT/scripts/lib/cloud-run-release.sh"
  crr_http_status() {
    case "$1" in
      https://candidate---api*/health/ready) echo 200 ;;
      https://candidate---web*/api/workspace/config)
        c=$(( $(cat "$WORK/cfgcount") + 1 )); echo "$c" > "$WORK/cfgcount"
        [[ $c -ge 2 ]] && echo 502 || echo 200 ;;
      https://candidate---*) echo 200 ;;
      */health/live) echo 403 ;;
      *) echo 200 ;;
    esac
  }
  CRR_MODE=promote crr_release ) > "$WORK/out.txt" 2>&1 && fail "chain re-check must fail" || pass "a web candidate that fails against the new API stops the promotion"
expect_out "does not work against the newly promoted API" "reason stated"
expect_serving api api-00005 "API rolled back"
expect_serving web web-00007 "web was never switched"
expect_no_log 'update-traffic vertex-ai-web .*--to-latest' "no web traffic switch happened"

echo "== production services are never created by this tool"
base_env production; fresh_services
run_prepare && fail "production prepare must not create services" || pass "production prepare refuses to create services"
expect_out "Refusing to create a production service" "reason stated"
expect_untouched "nothing was built or deployed"
base_env production; fresh_services; confirm
run_promote && fail "production promote must not create services" || pass "production promote refuses when services do not exist"
expect_untouched "nothing was changed"

echo "== staging is isolated from production (checked before anything runs)"
base_env staging; export API_SERVICE=vertex-ai-api
run_prepare && fail "staging with a production-looking name" || pass "staging service names must end with -staging"
expect_log_empty "no gcloud call for a bad staging name"
base_env staging; export FIRESTORE_PROJECT_ID=fb
run_prepare && fail "staging with production Firestore" || pass "staging may not share the PRODUCTION Firestore project"
expect_out "own data" "reason stated"
expect_log_empty "no gcloud call"
base_env staging; export FIREBASE_PROJECT_ID=fb
run_prepare && fail "staging with production Firebase" || pass "staging may not share the PRODUCTION Firebase project (real users)"
expect_log_empty "no gcloud call"
base_env staging; export ATTACHMENT_STORAGE_BUCKET=bucket
run_prepare && fail "staging with production bucket" || pass "staging may not use the production bucket"
base_env staging; export ATTACHMENT_STORAGE_BUCKET=other-bucket
run_prepare && fail "staging bucket name" || pass "staging bucket name must contain 'staging'"
base_env production; export WEB_SERVICE=vertex-ai-web-staging
run_prepare && fail "production pointed at staging" || pass "a production run pointed at a staging resource is refused"
expect_log_empty "no gcloud call"
base_env staging
run_prepare && pass "a properly isolated staging prepare works" || { fail "isolated staging prepare"; cat "$WORK/out.txt"; }

echo "== open-access warning"
base_env
run_prepare; grep -q "registration is OPEN" "$WORK/out.txt" && pass "warns when no access policy is configured" || fail "warns when no access policy is configured"
base_env; export ACCESS_ALLOWED_EMAILS="me@example.com"
run_prepare; grep -q "registration is OPEN" "$WORK/out.txt" && fail "no warning when a policy is set" || pass "no warning when a policy is set"

echo
if (( failures > 0 )); then echo "$failures check(s) failed"; exit 1; fi
echo "all release checks passed"
