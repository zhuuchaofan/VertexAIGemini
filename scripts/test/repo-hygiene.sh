#!/usr/bin/env bash
# Repository hygiene gate (runs in CI and locally): catches generated files, oversized files, committed
# credentials, and workflows that could deploy or write to the repository unexpectedly.
set -uo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.." || exit 1

failures=0
pass() { echo "PASS $1"; }
fail() { echo "FAIL $1"; failures=$((failures + 1)); }

MAX_BYTES=$((1024 * 1024))
files="$(git ls-files)"

echo "== generated files must not be tracked"
bad="$(grep -E '(^|/)(firestore-debug|firebase-debug|ui-debug|pubsub-debug)\.log$|\.log$|(^|/)firebase-export-|(^|/)\.firebase/' <<< "$files" || true)"
[[ -z "$bad" ]] && pass "no log files or emulator exports are tracked" || { fail "tracked generated files:"; echo "$bad" | sed 's/^/    /'; }

echo "== no oversized tracked files (> 1 MiB)"
big=""
while IFS= read -r f; do
  [[ -f "$f" ]] || continue
  size=$(stat -c %s "$f" 2>/dev/null || echo 0)
  (( size > MAX_BYTES )) && big+="    ${size} ${f}"$'\n'
done <<< "$files"
[[ -z "$big" ]] && pass "no tracked file exceeds 1 MiB" || { fail "oversized tracked files:"; printf '%s' "$big"; }

echo "== no credentials in tracked files"
# PEM private keys, service-account JSON, GitHub tokens, long sk- style keys
hits="$(git grep -nIE -e '-----BEGIN ([A-Z]+ )?PRIVATE KEY-----' -e '"private_key"[[:space:]]*:' -e 'ghp_[A-Za-z0-9]{30,}' -e 'github_pat_[A-Za-z0-9_]{30,}' -e '\bsk-[A-Za-z0-9]{32,}' -- . ':(exclude)scripts/test/repo-hygiene.sh' ':(exclude)VertexAI.Tests/*' ':(exclude)scripts/test/cloud-run-release.test.sh' ':(exclude)docs/*' || true)"
[[ -z "$hits" ]] && pass "no private keys or access tokens found" || { fail "possible credentials:"; echo "$hits" | cut -c1-120 | sed 's/^/    /'; }

echo "== .gitignore keeps emulator output out"
for f in VertexAI.Tests/firestore-debug.log firestore-debug.log firebase-debug.log ui-debug.log; do
  git check-ignore -q "$f" && pass "$f is ignored" || fail "$f is NOT ignored"
done

echo "== workflows cannot deploy unexpectedly or write to the repository"
if grep -qE '^  (push|pull_request|pull_request_target|schedule|workflow_run):' .github/workflows/deploy-cloud-run.yml; then
  fail "deploy-cloud-run.yml has an automatic trigger (must be workflow_dispatch only)"
else
  pass "deploy-cloud-run.yml has no automatic trigger"
fi
grep -qE '^  workflow_dispatch:' .github/workflows/deploy-cloud-run.yml && pass "deploy-cloud-run.yml is manually dispatchable" || fail "deploy-cloud-run.yml lost workflow_dispatch"
python3 - <<'PY' && pass "deploy workflow defaults to staging + prepare and has no environment-less shortcut" || fail "deploy workflow defaults are unsafe"
import yaml
w = yaml.safe_load(open(".github/workflows/deploy-cloud-run.yml"))
i = w[True]["workflow_dispatch"]["inputs"]
assert i["target_env"]["default"] == "staging", "target_env must default to staging"
assert i["mode"]["default"] == "prepare", "mode must default to prepare"
assert i["bootstrap_infrastructure"]["default"] is False, "bootstrap must default to false"
PY
if grep -qE '^  (push|pull_request|pull_request_target|schedule|workflow_run):' .github/workflows/staging-acceptance.yml; then fail "staging-acceptance.yml has an automatic trigger"; else pass "staging-acceptance.yml is manual only"; fi
grep -qE 'target_env|CRR_ENV|vars.GCP_PROJECT_ID|vars.WIF_PROVIDER' .github/workflows/staging-acceptance.yml && fail "staging-acceptance.yml must not reference production" || pass "staging-acceptance.yml cannot target production"
grep -q 'inputs.mode == .prepare. && inputs.bootstrap_infrastructure' .github/workflows/deploy-cloud-run.yml && pass "bootstrap steps run only in prepare mode" || fail "bootstrap steps can run outside prepare mode"
writers="$(grep -nE 'git (push|commit|add)|stefanzweifel/git-auto-commit|EndBug/add-and-commit|peter-evans/create-pull-request' .github/workflows/*.yml || true)"
[[ -z "$writers" ]] && pass "no workflow commits or pushes to the repository" || { fail "workflow writes to the repo:"; echo "$writers" | sed 's/^/    /'; }
grep -qE '^  contents: read$' .github/workflows/ci.yml && pass "ci.yml is read-only" || fail "ci.yml must declare 'contents: read'"
if grep -nE 'google-github-actions/auth|workload_identity_provider|id-token' .github/workflows/ci.yml; then fail "ci.yml must not authenticate to Google Cloud"; else pass "ci.yml has no cloud authentication"; fi

echo
if (( failures > 0 )); then echo "$failures hygiene check(s) failed"; exit 1; fi
echo "repository hygiene OK"
