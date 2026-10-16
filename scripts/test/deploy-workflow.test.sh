#!/usr/bin/env bash
# Executes the workflow's own shell steps ("Select environment", "Derive defaults", "Validate ...") with fake
# GitHub variables, to prove staging and production never share or fall back to each other's values and that the
# dangerous combinations are refused before any cloud access. No network, no gcloud.
set -uo pipefail
cd "$(dirname "$0")/../.."
WF=.github/workflows/deploy-cloud-run.yml
pass=0; fail=0
ok()  { echo "  PASS  $1"; pass=$((pass+1)); }
bad() { echo "  FAIL  $1"; fail=$((fail+1)); }

WORK="$(mktemp -d)"; trap 'rm -rf "${WORK:?}"' EXIT
python3 - "$WF" "$WORK" <<'PY'
import sys, yaml
wf = yaml.safe_load(open(sys.argv[1]))
steps = {s["name"].split(" (")[0]: s for s in wf["jobs"]["release"]["steps"]}
want = {"select": "Select environment", "derive": "Derive defaults that depend on the selected project",
        "validate": "Validate deployment configuration and guards"}
for key, prefix in want.items():
    step = next(s for n, s in steps.items() if n.startswith(prefix))
    open(f"{sys.argv[2]}/{key}.sh", "w").write(step["run"])
    open(f"{sys.argv[2]}/{key}.env", "w").write("\n".join(step.get("env", {}).keys()))
PY

# run <env> <mode> [VAR=value ...] : runs select -> derive -> validate; prints the resulting environment via GITHUB_ENV
run_steps() {
  local target="$1" mode="$2"; shift 2
  local ghenv="$WORK/ghenv"; : > "$ghenv"
  (
    export GITHUB_ENV="$ghenv" CRR_ENV="$target" CRR_MODE="$mode" IMAGE_TAG=abc123 BOOTSTRAP=false CRR_CONFIRM_PROMOTE="${CONFIRM:-}" CRR_ACCEPTANCE_REF="${ACCEPT:-}"
    # every P_/S_/X_ variable the step declares defaults to empty, then the test's values apply
    for f in select; do while read -r name; do export "$name="; done < <(tr -s '\n' ' ' < "$WORK/$f.env" | tr ' ' '\n' | grep .); done
    for kv in "$@"; do export "$kv"; done
    bash "$WORK/select.sh" >"$WORK/select.out" 2>&1 || { cat "$WORK/select.out" >&2; exit 11; }
    set -a; source "$ghenv"; set +a
    bash "$WORK/derive.sh" >"$WORK/derive.out" 2>&1 || { cat "$WORK/derive.out" >&2; exit 12; }
    set -a; source "$ghenv"; set +a
    bash "$WORK/validate.sh" >"$WORK/validate.out" 2>&1 || { cat "$WORK/validate.out" >&2; exit 13; }
  )
}
envval() { grep -E "^$1=" "$WORK/ghenv" | tail -1 | cut -d= -f2-; }

PROD=(P_PROJECT_ID=prod-proj P_WIF_PROVIDER=prod-wif P_DEPLOY_SERVICE_ACCOUNT=prod-deployer P_RUNTIME_SERVICE_ACCOUNT=prod-run
      P_FIREBASE_PROJECT_ID=prod-fb P_FIRESTORE_PROJECT_ID=prod-fs P_FIREBASE_AUTH_DOMAIN=prod.firebaseapp.com
      P_FIREBASE_API_KEY=prod-key P_FIREBASE_APP_ID=prod-app P_ATTACHMENT_STORAGE_BUCKET=prod-bucket)
STG=(S_PROJECT_ID=stg-proj S_WIF_PROVIDER=stg-wif S_DEPLOY_SERVICE_ACCOUNT=stg-deployer S_RUNTIME_SERVICE_ACCOUNT=stg-run
     S_FIREBASE_PROJECT_ID=stg-fb S_FIRESTORE_PROJECT_ID=stg-fs S_FIREBASE_AUTH_DOMAIN=stg.firebaseapp.com
     S_FIREBASE_API_KEY=stg-key S_FIREBASE_APP_ID=stg-app S_ATTACHMENT_STORAGE_BUCKET=stg-staging-bucket
     S_ACCESS_ALLOWED_EMAILS=tester@example.com)

echo "== staging uses ONLY staging values"
if run_steps staging prepare "${PROD[@]}" "${STG[@]}"; then
  [[ "$(envval PROJECT_ID)" == stg-proj && "$(envval WIF_PROVIDER)" == stg-wif && "$(envval FIREBASE_API_KEY)" == stg-key ]] && ok "staging selects STAGING project/WIF/key" || bad "staging picked wrong values"
  [[ "$(envval API_SERVICE)" == vertex-ai-api-staging && "$(envval WEB_SERVICE)" == vertex-ai-web-staging ]] && ok "staging default service names end with -staging" || bad "staging service names"
  [[ "$(envval PRODUCTION_FIRESTORE_PROJECT_ID)" == prod-fs && "$(envval PRODUCTION_ATTACHMENT_STORAGE_BUCKET)" == prod-bucket ]] && ok "production guard values come from the production set" || bad "guard values"
else bad "valid staging run was refused"; fi

echo "== production uses ONLY production values"
if run_steps production prepare "${PROD[@]}" "${STG[@]}"; then
  [[ "$(envval PROJECT_ID)" == prod-proj && "$(envval WIF_PROVIDER)" == prod-wif && "$(envval API_SERVICE)" == vertex-ai-api ]] && ok "production selects PRODUCTION values and names" || bad "production picked wrong values"
else bad "valid production run was refused"; fi

echo "== no fallback across environments"
run_steps production prepare "${STG[@]}" >/dev/null 2>&1; [[ $? -ne 0 ]] && ok "production with only staging values set is refused" || bad "production silently used staging values"
run_steps staging prepare "${PROD[@]}" >/dev/null 2>&1; [[ $? -ne 0 ]] && ok "staging with only production values set is refused" || bad "staging silently used production values"

echo "== staging cannot point at production data"
run_steps staging prepare "${PROD[@]}" "${STG[@]}" S_FIRESTORE_PROJECT_ID=prod-fs >/dev/null 2>&1; [[ $? -eq 13 ]] && ok "staging Firestore == production Firestore is refused" || bad "shared Firestore not refused"
run_steps staging prepare "${PROD[@]}" "${STG[@]}" S_FIREBASE_PROJECT_ID=prod-fb >/dev/null 2>&1; [[ $? -eq 13 ]] && ok "staging Firebase == production Firebase is refused" || bad "shared Firebase not refused"
run_steps staging prepare "${PROD[@]}" "${STG[@]}" S_ATTACHMENT_STORAGE_BUCKET=prod-bucket >/dev/null 2>&1; [[ $? -ne 0 ]] && ok "staging bucket == production bucket is refused" || bad "shared bucket not refused"
run_steps staging prepare "${PROD[@]}" "${STG[@]}" S_API_SERVICE=vertex-ai-api >/dev/null 2>&1; [[ $? -eq 13 ]] && ok "staging service without -staging suffix is refused" || bad "unsuffixed staging service accepted"
run_steps staging prepare "${PROD[@]}" "${STG[@]}" S_ACCESS_ALLOWED_EMAILS= >/dev/null 2>&1; [[ $? -eq 13 ]] && ok "staging without an access allow-list is refused" || bad "open staging accepted"

echo "== production cannot be pointed at staging resources"
run_steps production prepare "${PROD[@]}" P_WEB_SERVICE=vertex-ai-web-staging >/dev/null 2>&1; [[ $? -eq 13 ]] && ok "production with a -staging service is refused" || bad "staging-looking production accepted"

echo "== promote needs the exact confirmation (checked before any cloud access)"
CONFIRM="" run_steps staging promote "${PROD[@]}" "${STG[@]}" >/dev/null 2>&1; [[ $? -eq 13 ]] && ok "promote without confirmation is refused" || bad "promote without confirmation accepted"
CONFIRM="promote:staging:other" run_steps staging promote "${PROD[@]}" "${STG[@]}" >/dev/null 2>&1; [[ $? -eq 13 ]] && ok "promote with another commit's confirmation is refused" || bad "wrong confirmation accepted"
CONFIRM="promote:staging:abc123" run_steps staging promote "${PROD[@]}" "${STG[@]}" >/dev/null 2>&1; [[ $? -eq 0 ]] && ok "promote with the exact confirmation passes validation" || bad "correct confirmation refused"
CONFIRM="promote:staging:abc123" run_steps production promote "${PROD[@]}" >/dev/null 2>&1; [[ $? -eq 13 ]] && ok "a staging confirmation cannot promote production" || bad "staging confirmation promoted production"
CONFIRM="promote:production:abc123" run_steps production promote "${PROD[@]}" >/dev/null 2>&1; [[ $? -eq 13 ]] && ok "production promote without an acceptance reference is refused" || bad "production promote without acceptance accepted"
CONFIRM="promote:production:abc123" ACCEPT="https://github.com/o/r/actions/runs/1" run_steps production promote "${PROD[@]}" >/dev/null 2>&1; [[ $? -eq 0 ]] && ok "production promote with confirmation + acceptance reference passes validation" || bad "complete production promote refused"

echo "== defaults"
python3 - "$WF" <<'PY' && ok "workflow inputs default to staging + prepare, bootstrap off" || bad "unsafe input defaults"
import sys, yaml
w = yaml.safe_load(open(sys.argv[1])); i = w[True]["workflow_dispatch"]["inputs"]
assert i["target_env"]["default"] == "staging"
assert i["mode"]["default"] == "prepare"
assert i["bootstrap_infrastructure"]["default"] is False
assert i["confirm_promote"]["default"] == ""
PY

echo; echo "deploy-workflow tests: ${pass} passed, ${fail} failed"
[[ $fail -eq 0 ]]
