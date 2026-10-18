"""Offline contract tests for the Cloud Shell release script; no Google Cloud resources are touched."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("release-cloud-shell.sh")

FAKE_GCLOUD = r'''#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
with open(os.environ["CALL_LOG"], "a") as log:
    log.write(json.dumps(args) + "\n")

def service(name):
    api = name.endswith("api")
    env = [{"name": "FIREBASE_PROJECT_ID", "value": "fb-project"}, {"name": "Firebase__ApiKey", "value": "k"},
           {"name": "Firestore__X", "valueFrom": {"secretKeyRef": {"name": "s", "key": "latest"}}}]
    if api:
        env += [{"name": "FIRESTORE_PROJECT_ID", "value": "fs-project"},
                {"name": "Persistence__AttachmentStorageBucket", "value": "att-bucket"}]
        if os.environ.get("RUNS_ENABLED"):
            env.append({"name": "Runs__Enabled", "value": os.environ["RUNS_ENABLED"]})
    else:
        if os.environ.get("UI"):
            env.append({"name": "WEB_DEFAULT_UI", "value": os.environ["UI"]})
    traffic = [{"revisionName": os.environ.get("SERVING", "svc-00001"), "percent": 100}]
    latest = os.environ.get("LATEST", "svc-00001")
    if not api and os.environ.get("TAGGED"):
        traffic.append({"revisionName": os.environ["TAGGED"], "tag": "next", "url": "https://next---web.example.run.app"})
    return {"spec": {"template": {"spec": {
                "timeoutSeconds": int(os.environ.get("API_TIMEOUT" if api else "WEB_TIMEOUT_NOW", "300" if api else "60")),
                "containers": [{"image": "asia-east1-docker.pkg.dev/proj/vertex-ai/vertex-ai-%s:old" % ("api" if api else "web"),
                                "env": env}]}}},
            "status": {"url": "https://%s.example.run.app" % name, "traffic": traffic, "latestReadyRevisionName": latest}}

if args[:2] == ["config", "get-value"]:
    print("test-project")
elif args[:3] == ["run", "services", "describe"]:
    print(json.dumps(service(args[3])))
elif args[:3] == ["artifacts", "docker", "images"]:
    sys.exit(0 if os.environ.get("IMAGES", "1") == "1" else 1)
elif args[:3] == ["storage", "buckets", "describe"]:
    sys.exit(0 if os.environ.get("BUCKET_EXISTS") == "1" else 1)
elif args[:2] == ["storage", "ls"]:
    print("gs://b/pre-v2/pre-v2.overall_export_metadata")
elif args[:2] == ["firestore", "databases"]:
    print("asia-northeast1")
elif args[:3] == ["firestore", "indexes", "composite"]:
    print("projects/p/databases/(default)/collectionGroups/runs/indexes/x\tREADY")
elif args[:2] == ["auth", "print-identity-token"]:
    print("token")
'''

FAKE_CURL = r'''#!/usr/bin/env python3
import json, os, sys
url = [a for a in sys.argv[1:] if a.startswith("http")][0]
with open(os.environ["CALL_LOG"], "a") as log:
    log.write(json.dumps(["curl", url]) + "\n")
codes = json.loads(os.environ.get("CURL_CODES", "{}"))
code = "200"
for suffix, value in codes.items():
    if url.endswith(suffix):
        code = value
print(code, end="")
'''

READ_ONLY = {("run", "services", "describe"), ("config", "get-value"), ("artifacts", "docker", "images"),
             ("storage", "buckets", "describe"), ("storage", "ls"), ("firestore", "databases", "describe"),
             ("firestore", "indexes", "composite"), ("auth", "print-identity-token")}


class ReleaseTests(unittest.TestCase):
    def run_script(self, *args, state="", **settings):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for name, body in (("gcloud", FAKE_GCLOUD), ("curl", FAKE_CURL)):
                executable = root / name
                executable.write_text(body)
                executable.chmod(0o755)
            node = root / "node"
            node.write_text("#!/bin/sh\necho node \"$@\" >> \"$CALL_LOG\"\n")
            node.chmod(0o755)
            (root / "state").mkdir()
            if state:
                (root / "state" / "state.env").write_text(state)
            log = root / "calls"
            env = {k: v for k, v in os.environ.items() if k in ("PATH", "HOME", "LANG")}
            env.update(PATH=f"{root}:{env['PATH']}", CALL_LOG=str(log), RELEASE_STATE_DIR=str(root / "state"),
                       ALLOW_DIRTY="1", **settings)
            result = subprocess.run(["bash", str(SCRIPT), *args], env=env, capture_output=True, text=True)
            calls = [json.loads(line) for line in log.read_text().splitlines() if line.startswith("[")] if log.exists() else []
            saved = (root / "state" / "state.env").read_text() if (root / "state" / "state.env").exists() else ""
            return result, calls, saved

    @staticmethod
    def mutating(calls):
        return [c for c in calls if c and c[0] != "curl" and tuple(c[:3]) not in READ_ONLY
                and tuple(c[:2]) not in READ_ONLY]

    def test_plan_changes_nothing_and_says_what_will_change(self):
        result, calls, _ = self.run_script("plan")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.mutating(calls), [])
        self.assertIn("60s -> 300s", result.stdout)
        self.assertIn("Runs__MaxDurationSeconds  -> 270", result.stdout)
        self.assertIn("att-bucket", result.stdout)
        self.assertIn("fs-project", result.stdout)

    def test_deploy_keeps_existing_settings_and_stages_the_web_revision(self):
        result, calls, saved = self.run_script("deploy", SERVING="svc-00007")
        self.assertEqual(result.returncode, 0, result.stderr)
        deploys = [c for c in calls if c[:2] == ["run", "deploy"]]
        self.assertEqual([c[2] for c in deploys], ["vertex-ai-api", "vertex-ai-web"])
        api, web = deploys
        self.assertIn("--update-env-vars=Runs__MaxDurationSeconds=270,Runs__Enabled=false", api)
        self.assertIn("--no-traffic", web)
        self.assertIn("--tag=next", web)
        self.assertIn("--timeout=300s", web)
        self.assertIn("--update-env-vars=WEB_DEFAULT_UI=legacy", web)
        everything = [arg for c in calls for arg in c]
        for forbidden in ("--set-env-vars", "--set-secrets", "--update-secrets", "--clear-secrets", "--service-account",
                          "--allow-unauthenticated", "--no-allow-unauthenticated", "--remove-env-vars"):
            self.assertFalse(any(arg.startswith(forbidden) for arg in everything), forbidden)
        self.assertTrue(all(any(a.startswith("--image=asia-east1-docker.pkg.dev/proj/vertex-ai/") for a in c) for c in deploys))
        self.assertIn("PREV_API_REVISION=svc-00007", saved)

    def test_deploy_keeps_a_choice_already_made(self):
        _, calls, _ = self.run_script("deploy", RUNS_ENABLED="true", UI="next")
        deploys = [c for c in calls if c[:2] == ["run", "deploy"]]
        self.assertIn("--update-env-vars=Runs__MaxDurationSeconds=270,Runs__Enabled=true", deploys[0])
        self.assertIn("--update-env-vars=WEB_DEFAULT_UI=next", deploys[1])

    def test_rerunning_deploy_keeps_the_first_recorded_revisions(self):
        _, _, saved = self.run_script("deploy", SERVING="svc-00009", state="PREV_API_REVISION=svc-00003\nPREV_WEB_REVISION=svc-00004\n")
        self.assertIn("PREV_API_REVISION=svc-00003", saved)
        self.assertNotIn("svc-00009", saved)

    def test_deploy_stops_before_changing_anything_when_images_are_missing(self):
        result, calls, _ = self.run_script("deploy", IMAGES="0")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("build", result.stderr)
        self.assertEqual(self.mutating(calls), [])

    def test_build_skips_images_that_exist_and_builds_the_rest(self):
        _, calls, _ = self.run_script("build", IMAGES="1")
        self.assertEqual([c for c in calls if c[:2] == ["builds", "submit"]], [])
        _, calls, _ = self.run_script("build", IMAGES="0")
        builds = [c for c in calls if c[:2] == ["builds", "submit"]]
        self.assertEqual([c[2] for c in builds], ["VertexAI", "apps/web"])

    def test_short_web_timeout_is_refused(self):
        result, calls, _ = self.run_script("deploy", WEB_TIMEOUT="60s")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])

    def test_expect_commit_must_match(self):
        result, calls, _ = self.run_script("deploy", EXPECT_COMMIT="deadbeef")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("EXPECT_COMMIT", result.stderr)
        self.assertEqual(self.mutating(calls), [])

    def test_backup_copies_and_never_deletes_then_records(self):
        result, calls, saved = self.run_script("backup")
        self.assertEqual(result.returncode, 0, result.stderr)
        names = [" ".join(c[:2]) for c in calls]
        self.assertIn("storage buckets", names)
        export = next(c for c in calls if c[:2] == ["firestore", "export"])
        self.assertIn("--project=fs-project", export)
        rsync = next(c for c in calls if c[:2] == ["storage", "rsync"])
        self.assertIn("gs://att-bucket", rsync)
        self.assertFalse(any("delete" in arg for arg in rsync))
        self.assertIn("BACKUP_PREFIX=gs://fs-project-firestore-backups/pre-v2-", saved)

    def test_dry_run_prints_commands_and_runs_none(self):
        result, calls, saved = self.run_script("backup", DRY_RUN="1")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.mutating(calls), [])
        self.assertIn("+ gcloud firestore export", result.stdout)
        self.assertEqual(saved, "")

    def test_enable_v2_needs_a_backup_and_ready_indexes(self):
        result, calls, _ = self.run_script("enable-v2")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])
        result, calls, _ = self.run_script("enable-v2", state="BACKUP_PREFIX=gs://b/x\nINDEXES_READY=now\n")
        self.assertEqual(result.returncode, 0, result.stderr)
        update = next(c for c in calls if c[:3] == ["run", "services", "update"])
        self.assertIn("--update-env-vars=Runs__Enabled=true", update)

    def test_promote_needs_the_tagged_revision_to_be_the_latest(self):
        result, calls, _ = self.run_script("promote")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])
        result, calls, _ = self.run_script("promote", TAGGED="web-00002", LATEST="web-00001")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])
        result, calls, _ = self.run_script("promote", TAGGED="web-00002", LATEST="web-00002")
        self.assertEqual(result.returncode, 0, result.stderr)
        traffic = [c for c in calls if c[:3] == ["run", "services", "update-traffic"]]
        self.assertIn("--to-latest", traffic[0])
        self.assertIn("--remove-tags=next", traffic[1])

    def test_ui_switch_only_touches_the_web_setting(self):
        result, calls, _ = self.run_script("ui", "next")
        self.assertEqual(result.returncode, 0, result.stderr)
        update = next(c for c in calls if c[:3] == ["run", "services", "update"])
        self.assertEqual(update[3], "vertex-ai-web")
        self.assertIn("--update-env-vars=WEB_DEFAULT_UI=next", update)
        result, _, _ = self.run_script("ui", "fancy")
        self.assertNotEqual(result.returncode, 0)

    def test_rollback_returns_both_services_to_the_recorded_revisions(self):
        result, calls, _ = self.run_script("rollback", state="PREV_API_REVISION=api-1\nPREV_WEB_REVISION=web-1\n")
        self.assertEqual(result.returncode, 0, result.stderr)
        traffic = [c for c in calls if c[:3] == ["run", "services", "update-traffic"]]
        self.assertIn("--to-revisions=web-1=100", traffic[0])
        self.assertIn("--to-revisions=api-1=100", traffic[1])
        result, calls, _ = self.run_script("rollback")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])

    def test_verify_reports_a_failed_check(self):
        result, _, _ = self.run_script("verify", WEB_TIMEOUT_NOW="300", CURL_CODES=json.dumps({"/health/live": "403", "/api/conversations": "401", "/api/v2/me/settings": "404"}))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        result, _, _ = self.run_script("verify", WEB_TIMEOUT_NOW="60", CURL_CODES=json.dumps({"/health/live": "403", "/api/conversations": "401", "/api/v2/me/settings": "404"}))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("FAIL  web request timeout", result.stdout)


if __name__ == "__main__":
    unittest.main()
