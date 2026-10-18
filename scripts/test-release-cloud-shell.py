"""Offline contract tests for the Cloud Shell release script; no Google Cloud resources are touched."""
import json
import os
from pathlib import Path
import re
import subprocess
import tempfile
import time
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
    serving = os.environ.get("SERVING", "svc-00001") if api else os.environ.get("WEB_SERVING", os.environ.get("SERVING", "svc-00001"))
    traffic = [{"revisionName": serving, "percent": 100}]
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
    if os.environ.get("IMAGES", "1") != "1":
        sys.exit(1)
    if args[3] == "describe":
        print(json.loads(os.environ.get("IMAGE_DIGESTS", "{}")).get(args[4], ""))
elif args[:3] == ["storage", "buckets", "describe"]:
    sys.exit(0 if os.environ.get("BUCKET_EXISTS") == "1" else 1)
elif args[:2] == ["storage", "ls"]:
    if os.environ.get("EXPORT_MISSING") != "1":
        print("gs://b/pre-v2/firestore/pre-v2.overall_export_metadata")
elif args[:2] == ["firestore", "databases"]:
    print("asia-northeast1")
elif args[:3] == ["firestore", "indexes", "composite"]:
    print(os.environ.get("INDEXES_JSON", "[]"))
elif args[:2] == ["auth", "print-identity-token"]:
    print("token")
elif args[:2] == ["auth", "print-access-token"]:
    print("access-token")
elif args[:3] == ["run", "revisions", "describe"]:
    images = json.loads(os.environ.get("REV_IMAGES", "{}"))
    if any("timeoutSeconds" in a for a in args):
        print(os.environ.get("CANDIDATE_TIMEOUT", "300"))
    else:
        print(images.get(args[3], ""))
'''

FAKE_CURL = r'''#!/usr/bin/env python3
import json, os, sys
url = [a for a in sys.argv[1:] if a.startswith("http")][0]
with open(os.environ["CALL_LOG"], "a") as log:
    log.write(json.dumps(["curl", url] + (["PATCH", sys.argv[sys.argv.index("-d") + 1]] if "-X" in sys.argv else [])) + "\n")
if "identitytoolkit" in url:
    if "-X" not in sys.argv:
        print(os.environ.get("AUTH_CONFIG", '{"authorizedDomains": ["localhost"]}'))
    sys.exit(0)
codes = json.loads(os.environ.get("CURL_CODES", "{}"))
code = "200"
for suffix, value in codes.items():
    if url.endswith(suffix):
        code = value
print(code, end="")
'''

READ_ONLY = {("run", "services", "describe"), ("run", "revisions", "describe"), ("config", "get-value"),
             ("artifacts", "docker", "images"), ("storage", "buckets", "describe"), ("storage", "ls"),
             ("firestore", "databases", "describe"), ("firestore", "indexes", "composite"),
             ("auth", "print-identity-token"), ("auth", "print-access-token")}

ROOT = Path(__file__).resolve().parent.parent
SHORT = subprocess.run(["git", "rev-parse", "--short=12", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
COMMIT = subprocess.run(["git", "rev-parse", "HEAD"], cwd=ROOT, capture_output=True, text=True).stdout.strip()
API_IMAGE = f"asia-east1-docker.pkg.dev/proj/vertex-ai/vertex-ai-api:{SHORT}"
WEB_IMAGE = f"asia-east1-docker.pkg.dev/proj/vertex-ai/vertex-ai-web:{SHORT}"


def state_name(project="test-project", region="asia-east1", api="vertex-ai-api", web="vertex-ai-web", short=SHORT):
    return "release-" + re.sub(r"[^A-Za-z0-9._-]", "_", f"{project}_{region}_{api}_{web}_{short}") + ".env"


def header(**overrides):
    values = dict(PROJECT_ID="test-project", REGION="asia-east1", API_SERVICE="vertex-ai-api",
                  WEB_SERVICE="vertex-ai-web", RELEASE_ID=SHORT, COMMIT=COMMIT)
    values.update(overrides)
    return "".join(f"{k}={v}\n" for k, v in values.items())


def indexes_json(skip=(), state=None, flip=()):
    """The manifest as gcloud would list it: every index READY, with the trailing __name__ field."""
    manifest = json.loads((ROOT / "firestore.indexes.json").read_text())
    live = []
    for position, index in enumerate(manifest["indexes"]):
        if position in skip:
            continue
        fields = [dict(f) for f in index["fields"]]
        if position in flip:
            fields[-1]["order"] = "DESCENDING" if fields[-1]["order"] == "ASCENDING" else "ASCENDING"
        fields.append({"fieldPath": "__name__", "order": "ASCENDING"})
        live.append({"name": f"projects/p/databases/(default)/collectionGroups/{index['collectionGroup']}/indexes/i{position}",
                     "queryScope": index.get("queryScope", "COLLECTION"), "fields": fields,
                     "state": (state or {}).get(position, "READY")})
    return json.dumps(live)


FRESH_BACKUP = lambda: header() + f"BACKUP_ROOT=gs://b/pre-v2\nBACKUP_EPOCH={int(time.time())}\n"


class ReleaseTests(unittest.TestCase):
    def run_script(self, *args, state=None, state_file=None, **settings):
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
            if state is not None:
                (root / "state" / (state_file or state_name())).write_text(state)
            log = root / "calls"
            env = {k: v for k, v in os.environ.items() if k in ("PATH", "HOME", "LANG")}
            env.update(PATH=f"{root}:{env['PATH']}", CALL_LOG=str(log), RELEASE_STATE_DIR=str(root / "state"),
                       ALLOW_DIRTY="1", INDEXES_JSON=indexes_json(), INDEX_WAIT_ROUNDS="1", INDEX_WAIT_SECONDS="0")
            env.update(settings)
            result = subprocess.run(["bash", str(SCRIPT), *args], env=env, capture_output=True, text=True, cwd=ROOT)
            calls = [json.loads(line) for line in log.read_text().splitlines() if line.startswith("[")] if log.exists() else []
            saved = {f.name: f.read_text() for f in (root / "state").iterdir()}
            return result, calls, "".join(saved.values()), saved

    def go(self, *args, **kwargs):
        result, calls, saved, _ = self.run_script(*args, **kwargs)
        return result, calls, saved

    @staticmethod
    def mutating(calls):
        return [c for c in calls if c and c[0] != "curl" and tuple(c[:3]) not in READ_ONLY
                and tuple(c[:2]) not in READ_ONLY]

    # ---- plan / build / deploy

    def test_plan_changes_nothing_and_states_the_production_traffic_impact(self):
        result, calls, _ = self.go("plan")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.mutating(calls), [])
        self.assertIn("60s -> 300s", result.stdout)
        self.assertIn("Runs__MaxDurationSeconds  -> 270", result.stdout)
        self.assertIn("att-bucket", result.stdout)
        self.assertIn("ALL production traffic immediately", result.stdout)
        self.assertIn("rollback api", result.stdout)

    def test_deploy_keeps_existing_settings_and_says_the_api_is_not_a_no_traffic_release(self):
        result, calls, saved = self.go("deploy", SERVING="svc-00007", LATEST="svc-00008", TAGGED="web-00002",
                                       REV_IMAGES=json.dumps({"svc-00008": API_IMAGE, "web-00002": WEB_IMAGE}))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("NOT a no-traffic release", result.stderr)
        deploys = [c for c in calls if c[:2] == ["run", "deploy"]]
        self.assertEqual([c[2] for c in deploys], ["vertex-ai-api", "vertex-ai-web"])
        api, web = deploys
        self.assertIn("--update-env-vars=Runs__MaxDurationSeconds=270,Runs__Enabled=false", api)
        self.assertNotIn("--no-traffic", api)
        for flag in ("--no-traffic", "--tag=next", "--timeout=300s", "--update-env-vars=WEB_DEFAULT_UI=legacy"):
            self.assertIn(flag, web)
        everything = [arg for c in calls for arg in c]
        for forbidden in ("--set-env-vars", "--set-secrets", "--update-secrets", "--clear-secrets", "--service-account",
                          "--allow-unauthenticated", "--no-allow-unauthenticated", "--remove-env-vars"):
            self.assertFalse(any(arg.startswith(forbidden) for arg in everything), forbidden)
        self.assertIn("PREV_API_REVISION=svc-00007", saved)
        self.assertIn("CANDIDATE_API_REVISION=svc-00008", saved)
        self.assertIn("CANDIDATE_WEB_REVISION=web-00002", saved)

    def test_deploy_stops_if_a_candidate_does_not_run_this_commits_image(self):
        result, _, saved = self.go("deploy", LATEST="svc-00008", TAGGED="web-00002",
                                   REV_IMAGES=json.dumps({"svc-00008": API_IMAGE, "web-00002": "x/vertex-ai-web:old"}))
        self.assertNotEqual(result.returncode, 0)
        self.assertNotIn("CANDIDATE_WEB_REVISION", saved)

    def test_deploy_keeps_a_choice_already_made(self):
        _, calls, _ = self.go("deploy", RUNS_ENABLED="true", UI="next")
        deploys = [c for c in calls if c[:2] == ["run", "deploy"]]
        self.assertIn("--update-env-vars=Runs__MaxDurationSeconds=270,Runs__Enabled=true", deploys[0])
        self.assertIn("--update-env-vars=WEB_DEFAULT_UI=next", deploys[1])

    def test_rerunning_deploy_keeps_the_first_recorded_revisions(self):
        _, _, saved = self.go("deploy", SERVING="svc-00009",
                              state=header() + "PREV_API_REVISION=svc-00003\nPREV_WEB_REVISION=svc-00004\n")
        self.assertIn("PREV_API_REVISION=svc-00003", saved)
        self.assertNotIn("PREV_API_REVISION=svc-00009", saved)

    def test_deploy_stops_before_changing_anything_when_images_are_missing(self):
        result, calls, _ = self.go("deploy", IMAGES="0")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("build", result.stderr)
        self.assertEqual(self.mutating(calls), [])

    def test_build_skips_images_that_exist_and_builds_the_rest(self):
        _, calls, _ = self.go("build", IMAGES="1")
        self.assertEqual([c for c in calls if c[:2] == ["builds", "submit"]], [])
        _, calls, _ = self.go("build", IMAGES="0")
        self.assertEqual([c[2] for c in calls if c[:2] == ["builds", "submit"]], ["VertexAI", "apps/web"])

    def test_short_web_timeout_is_refused(self):
        result, calls, _ = self.go("deploy", WEB_TIMEOUT="60s")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])

    def test_expect_commit_must_match(self):
        result, calls, _ = self.go("deploy", EXPECT_COMMIT="deadbeef")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("EXPECT_COMMIT", result.stderr)
        self.assertEqual(self.mutating(calls), [])

    # ---- backup and the unified attachment path

    def test_backup_uses_one_root_for_the_export_and_the_attachments_and_records_it(self):
        result, calls, saved = self.go("backup")
        self.assertEqual(result.returncode, 0, result.stderr)
        export = next(c for c in calls if c[:2] == ["firestore", "export"])
        rsync = next(c for c in calls if c[:2] == ["storage", "rsync"])
        self.assertIn("--project=fs-project", export)
        root = re.search(r"BACKUP_ROOT=(\S+)", saved).group(1)
        self.assertRegex(root, r"^gs://fs-project-firestore-backups/pre-v2-\d{8}T\d{6}Z$")
        self.assertEqual(export[2], f"{root}/firestore")
        self.assertEqual(rsync[-2:], ["gs://att-bucket", f"{root}/attachments"])
        self.assertFalse(any("delete" in arg for arg in rsync))
        self.assertIn("BACKUP_EPOCH=", saved)

    def test_manual_and_backup_doc_use_the_same_paths_as_the_script(self):
        docs = ROOT / "docs" / "refactor"
        for name in ("backup-and-restore.md", "cloud-shell-release.md"):
            text = (docs / name).read_text()
            self.assertNotIn("attachments-pre-v2", text, name)
            self.assertIn("/attachments", text, name)
            self.assertIn("/firestore", text, name)

    def test_dry_run_prints_commands_and_runs_none(self):
        result, calls, saved = self.go("backup", DRY_RUN="1")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertEqual(self.mutating(calls), [])
        self.assertIn("+ gcloud firestore export", result.stdout)
        self.assertNotIn("BACKUP_ROOT", saved)

    # ---- indexes: full definition, rechecked live

    def test_indexes_pass_only_when_every_definition_is_ready(self):
        result, _, _ = self.go("indexes")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertEqual(result.stdout.count("  ready "), 4)

    def test_a_missing_index_a_wrong_field_order_and_a_building_index_each_fail(self):
        for label, kwargs in (("missing", dict(skip=(3,))), ("wrong order", dict(flip=(2,))), ("building", dict(state={3: "CREATING"}))):
            with self.subTest(label):
                result, _, _ = self.go("indexes", INDEXES_JSON=indexes_json(**kwargs))
                self.assertNotEqual(result.returncode, 0)

    def test_same_collection_and_prefix_is_not_enough(self):
        # Only the ASCENDING messages index exists; the DESCENDING one with the same prefix must be reported missing.
        result, _, _ = self.go("indexes", INDEXES_JSON=indexes_json(skip=(2,)))
        self.assertNotEqual(result.returncode, 0)
        self.assertRegex(result.stdout, r"missing\s+messages \(uid ASCENDING, conversationId ASCENDING, createdAt DESCENDING\)")

    # ---- enable-v2

    def test_enable_v2_needs_a_backup_recorded_for_this_release(self):
        result, calls, _ = self.go("enable-v2")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("No backup recorded for THIS release", result.stderr)
        self.assertEqual(self.mutating(calls), [])

    def test_enable_v2_rechecks_indexes_now_whatever_was_true_earlier(self):
        result, calls, _ = self.go("enable-v2", state=FRESH_BACKUP() + "INDEXES_READY=earlier\n",
                                   INDEXES_JSON=indexes_json(skip=(3,)))
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("missing", result.stdout)
        self.assertEqual(self.mutating(calls), [])

    def test_enable_v2_refuses_a_stale_or_vanished_backup(self):
        old = header() + f"BACKUP_ROOT=gs://b/pre-v2\nBACKUP_EPOCH={int(time.time()) - 100 * 3600}\n"
        result, calls, _ = self.go("enable-v2", state=old)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("old", result.stderr)
        result, calls, _ = self.go("enable-v2", state=FRESH_BACKUP(), EXPORT_MISSING="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])

    def test_enable_v2_turns_the_routes_on_when_everything_holds(self):
        result, calls, saved = self.go("enable-v2", state=FRESH_BACKUP(), LATEST="svc-00010")
        self.assertEqual(result.returncode, 0, result.stderr)
        update = next(c for c in calls if c[:3] == ["run", "services", "update"])
        self.assertIn("--update-env-vars=Runs__Enabled=true", update)
        self.assertIn("CANDIDATE_API_REVISION=svc-00010", saved)

    # ---- state binding

    def test_a_backup_from_another_project_service_or_release_is_never_used(self):
        mine = state_name()
        cases = {
            "another project": state_name(project="other-project"),
            "another api service": state_name(api="other-api"),
            "another web service": state_name(web="other-web"),
            "another region": state_name(region="us-central1"),
            "another commit": state_name(short="0123456789ab"),
        }
        for label, other in cases.items():
            with self.subTest(label):
                self.assertNotEqual(other, mine)
                result, calls, _ = self.go("enable-v2", state=FRESH_BACKUP(), state_file=other)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("No backup recorded for THIS release", result.stderr)
                self.assertEqual(self.mutating(calls), [])

    def test_a_state_file_whose_header_names_another_release_is_refused(self):
        for field, value in (("PROJECT_ID", "other"), ("API_SERVICE", "other"), ("WEB_SERVICE", "other"), ("RELEASE_ID", "f" * 12)):
            with self.subTest(field):
                bad = header(**{field: value}) + f"BACKUP_ROOT=gs://b/x\nBACKUP_EPOCH={int(time.time())}\n"
                result, calls, _ = self.go("enable-v2", state=bad)
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("belongs to", result.stderr)
                self.assertEqual(self.mutating(calls), [])

    def test_release_id_keeps_an_earlier_releases_images_and_records(self):
        old = "0b82153dec14"
        legacy = ("PROJECT_ID=test-project\nREGION=asia-east1\nAPI_SERVICE=vertex-ai-api\nWEB_SERVICE=vertex-ai-web\n"
                  f"COMMIT={old}359069d3912e0466b1a473b6dfdc\nPREV_API_REVISION=api-3\nPREV_WEB_REVISION=web-3\n")
        result, calls, _ = self.go("rollback", "web", state=legacy, state_file=state_name(short=old), RELEASE_ID=old)
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertIn("--to-revisions=web-3=100", next(c for c in calls if c[:3] == ["run", "services", "update-traffic"]))
        # Images are named by the release id, so nothing is rebuilt for a scripts-only change.
        result, calls, _ = self.go("build", RELEASE_ID=old, IMAGES="0")
        images = [arg for c in calls if c[:2] == ["builds", "submit"] for arg in c if arg.startswith("--tag=")]
        self.assertTrue(all(arg.endswith(f":{old}") for arg in images) and len(images) == 2, images)
        # The wrong release id for that file is refused.
        result, calls, _ = self.go("rollback", "web", state=legacy, state_file=state_name(short="ffffffffffff"), RELEASE_ID="ffffffffffff")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])

    def test_rollback_points_come_only_from_this_releases_state(self):
        other = header(PROJECT_ID="other") + "PREV_API_REVISION=api-x\nPREV_WEB_REVISION=web-x\n"
        result, calls, _ = self.go("rollback", state=other, state_file=state_name(project="other"))
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])

    # ---- verify: candidate revisions only

    def candidate_state(self, **extra):
        text = header() + "CANDIDATE_API_REVISION=api-cand\nCANDIDATE_WEB_REVISION=web-cand\n"
        return text + "".join(f"{k}={v}\n" for k, v in extra.items())

    GOOD_CURL = json.dumps({"/health/live": "403", "/api/conversations": "401", "/api/v2/me/settings": "404"})

    def verify(self, **settings):
        env = dict(SERVING="api-cand", LATEST="api-cand", TAGGED="web-cand", WEB_TIMEOUT_NOW="300", CURL_CODES=self.GOOD_CURL,
                   REV_IMAGES=json.dumps({"api-cand": API_IMAGE, "web-cand": WEB_IMAGE}))
        env.update(settings)
        state = env.pop("state", self.candidate_state())
        return self.go("verify", state=state, **env)

    def test_verify_passes_for_the_recorded_candidates(self):
        result, calls, _ = self.verify()
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("Candidates for", result.stdout)
        self.assertTrue(any(c[0] == "curl" and "next---web" in c[1] for c in calls))

    def test_verify_fails_without_a_recorded_candidate(self):
        result, calls, _ = self.verify(state=header())
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("No candidate revisions recorded", result.stderr)
        self.assertFalse(any(c[0] == "curl" for c in calls))

    def test_verify_fails_when_the_tag_points_at_another_revision(self):
        result, _, _ = self.verify(TAGGED="web-other")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("not the candidate", result.stdout)

    def test_verify_fails_when_a_candidate_runs_another_version(self):
        for images in ({"api-cand": "x/vertex-ai-api:old", "web-cand": WEB_IMAGE}, {"api-cand": API_IMAGE, "web-cand": "x/vertex-ai-web:old"}):
            with self.subTest(images=images):
                result, _, _ = self.verify(REV_IMAGES=json.dumps(images))
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("expected", result.stdout)

    def test_verify_fails_when_the_api_serving_traffic_is_not_the_candidate(self):
        result, _, _ = self.verify(SERVING="api-old")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("not the candidate", result.stdout)

    def test_verify_fails_when_the_candidate_has_no_tag_and_was_never_promoted(self):
        result, _, _ = self.verify(TAGGED="")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("cannot be tested", result.stdout)

    def test_verify_after_promote_tests_the_serving_candidate(self):
        result, calls, _ = self.verify(TAGGED="", SERVING="api-cand", state=self.candidate_state(PROMOTED_REVISION="web-cand"),
                                       WEB_SERVING="web-cand")
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_verify_fails_on_a_wrong_status_or_timeout_and_accepts_digest_pinned_images(self):
        result, _, _ = self.verify(CURL_CODES=json.dumps({"/health/live": "200"}))
        self.assertNotEqual(result.returncode, 0)
        result, _, _ = self.verify(CANDIDATE_TIMEOUT="60")
        self.assertNotEqual(result.returncode, 0)
        result, _, _ = self.verify(REV_IMAGES=json.dumps({"api-cand": API_IMAGE + "@sha256:abc", "web-cand": WEB_IMAGE + "@sha256:def"}))
        self.assertEqual(result.returncode, 0, result.stdout)

    def test_digest_only_revision_images_match_by_digest(self):
        # Cloud Run records "<repo>/<name>@sha256:…" and drops the tag.
        digests = {API_IMAGE: "sha256:aaa", WEB_IMAGE: "sha256:bbb"}
        repo = API_IMAGE.rsplit(":", 1)[0]
        web_repo = WEB_IMAGE.rsplit(":", 1)[0]
        pinned = {"api-cand": f"{repo}@sha256:aaa", "web-cand": f"{web_repo}@sha256:bbb"}
        result, _, _ = self.verify(REV_IMAGES=json.dumps(pinned), IMAGE_DIGESTS=json.dumps(digests))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)

    def test_a_digest_that_is_not_this_releases_image_does_not_match(self):
        digests = {API_IMAGE: "sha256:aaa", WEB_IMAGE: "sha256:bbb"}
        repo = API_IMAGE.rsplit(":", 1)[0]
        web_repo = WEB_IMAGE.rsplit(":", 1)[0]
        for images in ({"api-cand": f"{repo}@sha256:other", "web-cand": f"{web_repo}@sha256:bbb"},
                       {"api-cand": f"{repo}@sha256:aaa", "web-cand": f"{web_repo}@sha256:other"},
                       {"api-cand": f"elsewhere/vertex-ai-api@sha256:aaa", "web-cand": f"{web_repo}@sha256:bbb"}):
            with self.subTest(images=images):
                result, _, _ = self.verify(REV_IMAGES=json.dumps(images), IMAGE_DIGESTS=json.dumps(digests))
                self.assertNotEqual(result.returncode, 0)
                self.assertIn("expected", result.stdout)

    def test_deploy_accepts_digest_only_candidates(self):
        digests = {API_IMAGE: "sha256:aaa", WEB_IMAGE: "sha256:bbb"}
        repo = API_IMAGE.rsplit(":", 1)[0]
        web_repo = WEB_IMAGE.rsplit(":", 1)[0]
        result, _, saved = self.go("deploy", LATEST="svc-00008", TAGGED="web-00002", IMAGE_DIGESTS=json.dumps(digests),
                                   REV_IMAGES=json.dumps({"svc-00008": f"{repo}@sha256:aaa", "web-00002": f"{web_repo}@sha256:bbb"}))
        self.assertEqual(result.returncode, 0, result.stdout + result.stderr)
        self.assertIn("CANDIDATE_API_REVISION=svc-00008", saved)

    # ---- promote / ui / rollback

    def test_promote_needs_this_releases_candidate_to_be_tagged_and_latest(self):
        result, calls, _ = self.go("promote", state=self.candidate_state())
        self.assertNotEqual(result.returncode, 0)
        result, calls, _ = self.go("promote", state=self.candidate_state(), TAGGED="web-other", LATEST="web-other")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])
        result, calls, _ = self.go("promote", state=self.candidate_state(), TAGGED="web-cand", LATEST="web-newer")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])
        result, calls, saved = self.go("promote", state=self.candidate_state(), TAGGED="web-cand", LATEST="web-cand")
        self.assertEqual(result.returncode, 0, result.stderr)
        traffic = [c for c in calls if c[:3] == ["run", "services", "update-traffic"]]
        self.assertIn("--to-latest", traffic[0])
        self.assertIn("--remove-tags=next", traffic[1])
        self.assertIn("PROMOTED_REVISION=web-cand", saved)

    def test_ui_switch_only_touches_the_web_setting(self):
        result, calls, _ = self.go("ui", "next")
        self.assertEqual(result.returncode, 0, result.stderr)
        update = next(c for c in calls if c[:3] == ["run", "services", "update"])
        self.assertEqual(update[3], "vertex-ai-web")
        self.assertIn("--update-env-vars=WEB_DEFAULT_UI=next", update)
        result, _, _ = self.go("ui", "fancy")
        self.assertNotEqual(result.returncode, 0)

    def test_rollback_scopes(self):
        state = header() + "PREV_API_REVISION=api-1\nPREV_WEB_REVISION=web-1\n"
        for scope, expect in (("all", ["--to-revisions=web-1=100", "--to-revisions=api-1=100"]), ("web", ["--to-revisions=web-1=100"]),
                              ("api", ["--to-revisions=api-1=100"])):
            with self.subTest(scope):
                result, calls, _ = self.go("rollback", scope, state=state)
                self.assertEqual(result.returncode, 0, result.stderr)
                traffic = [c for c in calls if c[:3] == ["run", "services", "update-traffic"]]
                self.assertEqual([arg for c in traffic for arg in c if arg.startswith("--to-revisions")], expect)
        result, _, _ = self.go("rollback", "everything", state=state)
        self.assertNotEqual(result.returncode, 0)

    def test_rolling_the_api_back_with_v2_on_needs_disable_v2_first(self):
        state = header() + "PREV_API_REVISION=api-1\nPREV_WEB_REVISION=web-1\n"
        result, calls, _ = self.go("rollback", "api", state=state, RUNS_ENABLED="true")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("disable-v2", result.stderr)
        self.assertEqual(self.mutating(calls), [])
        result, calls, _ = self.go("rollback", "web", state=state, RUNS_ENABLED="true")
        self.assertEqual(result.returncode, 0, result.stderr)
        result, calls, _ = self.go("rollback", "api", state=state, RUNS_ENABLED="true", FORCE_ROLLBACK="1")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_rollback_without_recorded_points_changes_nothing(self):
        result, calls, _ = self.go("rollback")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(self.mutating(calls), [])

    # ---- Firebase authorized domain for the test URL

    def test_auth_domain_reports_a_missing_host_and_fails(self):
        result, calls, _ = self.go("auth-domain", TAGGED="web-cand")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("next---web.example.run.app", result.stdout)
        self.assertFalse(any("PATCH" in c for c in calls))

    def test_auth_domain_add_keeps_existing_domains_and_is_idempotent(self):
        result, calls, _ = self.go("auth-domain", "add", TAGGED="web-cand",
                                   AUTH_CONFIG=json.dumps({"authorizedDomains": ["localhost", "app.example.com"]}))
        self.assertEqual(result.returncode, 0, result.stderr)
        patch = next(c for c in calls if "PATCH" in c)
        self.assertIn("updateMask=authorizedDomains", patch[1])
        self.assertIn("/projects/fb-project/", patch[1])
        self.assertEqual(json.loads(patch[-1])["authorizedDomains"], ["localhost", "app.example.com", "next---web.example.run.app"])
        result, calls, _ = self.go("auth-domain", "add", TAGGED="web-cand",
                                   AUTH_CONFIG=json.dumps({"authorizedDomains": ["localhost", "next---web.example.run.app"]}))
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(any("PATCH" in c for c in calls))

    def test_auth_domain_without_a_candidate_tag_fails(self):
        result, _, _ = self.go("auth-domain", "add")
        self.assertNotEqual(result.returncode, 0)

    # ---- the manual does not call this a no-traffic release

    def test_manual_states_the_api_traffic_impact_and_its_rollback(self):
        text = (ROOT / "docs" / "refactor" / "cloud-shell-release.md").read_text()
        self.assertIn("API 新修订立即承接生产流量", text)
        self.assertIn("rollback api", text)
        self.assertNotIn("全链路无流量", text)


if __name__ == "__main__":
    unittest.main()
