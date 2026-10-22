"""Offline deployment contract tests; no Google Cloud resources are changed."""
import json
import os
from pathlib import Path
import subprocess
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("deploy-cloud-run.sh")
FAKE = """#!/usr/bin/env python3
import json, os, sys
args = sys.argv[1:]
with open(os.environ["CALL_LOG"], "a") as log:
    log.write(json.dumps([os.path.basename(sys.argv[0]), *args]) + "\\n")
if args[:3] == ["run", "services", "list"]:
    print(os.environ.get("SERVICES", ""))
    sys.exit(int(os.environ.get("LOOKUP_EXIT", "0")))
if args[:3] == ["run", "services", "describe"]:
    print("https://example.run.app")
if args[:2] == ["builds", "submit"]:
    sys.exit(int(os.environ.get("BUILD_EXIT", "0")))
"""


class DeployTests(unittest.TestCase):
    def run_script(self, services="", options=(), **settings):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for command in ("gcloud", "node"):
                executable = root / command
                executable.write_text(FAKE)
                executable.chmod(0o755)
            log = root / "calls"
            env = {key: value for key, value in os.environ.items()
                   if key in ("PATH", "HOME", "LANG")}
            env.update(PATH=f"{root}:{env['PATH']}", CALL_LOG=str(log),
                       PROJECT_ID="test-project", IMAGE_TAG="test",
                       SERVICES=services, **settings)
            result = subprocess.run(["bash", str(SCRIPT), *options], cwd=tmp,
                                    env=env, capture_output=True, text=True)
            calls = [json.loads(line) for line in log.read_text().splitlines()] if log.exists() else []
            return result, calls

    def test_existing_services_only_update_images(self):
        result, calls = self.run_script("vertex-ai-api\nvertex-ai-web")
        self.assertEqual(result.returncode, 0, result.stderr)
        updates = [call for call in calls if call[1:4] == ["run", "services", "update"]]
        self.assertEqual(len(updates), 2)
        for call in updates:
            self.assertEqual(len(call), 8)
            self.assertTrue(call[-1].startswith("--image="))
        self.assertFalse(any(call[0] == "node" for call in calls))
        self.assertFalse(any("--set-env-vars" in arg for call in calls for arg in call))

    def test_first_deployment_requires_configuration(self):
        result, calls = self.run_script()
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("SERVICE_ACCOUNT is required", result.stdout)
        self.assertEqual(len(calls), 1)

    def test_initial_and_explicit_configuration(self):
        for services, options in (("", ()), ("vertex-ai-api\nvertex-ai-web", ("--update-config",))):
            with self.subTest(services=services):
                result, calls = self.run_script(
                    services, options, SERVICE_ACCOUNT="api@example.test",
                    FIREBASE_API_KEY="public-web-config", FIREBASE_APP_ID="web-app")
                self.assertEqual(result.returncode, 0, result.stderr)
                deploys = [call for call in calls if call[1:3] == ["run", "deploy"]]
                self.assertEqual(len(deploys), 2)
                self.assertTrue(all(any(arg.startswith("--update-env-vars=") for arg in call)
                                    for call in deploys))
                self.assertFalse(any(arg.startswith("--set-env-vars=")
                                     for call in calls for arg in call))

    def test_api_receives_the_generation_time_limit(self):
        result, calls = self.run_script(
            "", (), SERVICE_ACCOUNT="api@example.test",
            FIREBASE_API_KEY="public-web-config", FIREBASE_APP_ID="web-app")
        self.assertEqual(result.returncode, 0, result.stderr)
        api = next(call for call in calls if call[1:3] == ["run", "deploy"] and "vertex-ai-api" in call)
        env = next(arg for arg in api if arg.startswith("--update-env-vars="))
        self.assertIn("Runs__MaxDurationSeconds=270", env)
        web = next(call for call in calls if call[1:3] == ["run", "deploy"] and "vertex-ai-web" in call)
        self.assertIn("--timeout=300s", web)

    def test_v2_routes_are_off_by_default_and_can_be_chosen(self):
        initial = dict(SERVICE_ACCOUNT="api@example.test", FIREBASE_API_KEY="public-web-config", FIREBASE_APP_ID="web-app")
        for extra, runs in (({}, "false"), ({"RUNS_ENABLED": "true"}, "true")):
            with self.subTest(extra=extra):
                result, calls = self.run_script("", (), **initial, **extra)
                self.assertEqual(result.returncode, 0, result.stderr)
                api = next(c for c in calls if c[1:3] == ["run", "deploy"] and "vertex-ai-api" in c)
                web = next(c for c in calls if c[1:3] == ["run", "deploy"] and "vertex-ai-web" in c)
                self.assertIn(f"Runs__Enabled={runs}", next(a for a in api if a.startswith("--update-env-vars=")))
                self.assertNotIn("WEB_DEFAULT_UI", next(a for a in web if a.startswith("--update-env-vars=")))

    def test_invalid_switches_are_rejected_before_any_mutation(self):
        for bad in ({"RUNS_ENABLED": "yes"},):
            with self.subTest(bad=bad):
                result, calls = self.run_script("", (), SERVICE_ACCOUNT="api@example.test",
                                                FIREBASE_API_KEY="k", FIREBASE_APP_ID="a", **bad)
                self.assertNotEqual(result.returncode, 0)
                self.assertEqual(calls, [])

    def test_short_web_timeout_is_rejected_before_any_mutation(self):
        result, calls = self.run_script(
            "", (), SERVICE_ACCOUNT="api@example.test", WEB_TIMEOUT="60s",
            FIREBASE_API_KEY="public-web-config", FIREBASE_APP_ID="web-app")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("leave only 30s", result.stderr)
        self.assertEqual(len(calls), 1)  # only the service lookup; nothing built, created or deployed

    def test_unparseable_timeout_is_rejected(self):
        result, calls = self.run_script(
            "", (), SERVICE_ACCOUNT="api@example.test", API_TIMEOUT="5m",
            FIREBASE_API_KEY="public-web-config", FIREBASE_APP_ID="web-app")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("whole seconds", result.stderr)
        self.assertEqual(len(calls), 1)

    def test_image_only_deployment_does_not_validate_or_change_live_timeouts(self):
        # Live settings are preserved; the script defaults must not be able to block an image update.
        result, calls = self.run_script("vertex-ai-api\nvertex-ai-web", WEB_TIMEOUT="60s")
        self.assertEqual(result.returncode, 0, result.stderr)
        self.assertFalse(any(arg.startswith("--timeout") for call in calls for arg in call))

    def test_lookup_failure_stops_before_mutations(self):
        result, calls = self.run_script(LOOKUP_EXIT="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertEqual(len(calls), 1)

    def test_partial_deployment_stops(self):
        result, calls = self.run_script("vertex-ai-api")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("--update-config", result.stderr)
        self.assertEqual(len(calls), 1)

    def test_build_failure_stops_before_service_update(self):
        result, calls = self.run_script("vertex-ai-api\nvertex-ai-web", BUILD_EXIT="1")
        self.assertNotEqual(result.returncode, 0)
        self.assertFalse(any(call[1:4] == ["run", "services", "update"] for call in calls))


if __name__ == "__main__":
    unittest.main()
