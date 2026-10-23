#!/usr/bin/env python3
"""Pins the Release Please setup offline (story #1659, AC-2 and AC-3): config, manifest, the <Version> xpath and the
workflow's pins and permissions. No network, no GitHub. PyYAML is optional: without it the YAML checks are skipped with
a message and only the line checks (SHA pins, secrets) run. Run: python3 scripts/test_release_please_config.py
(also run by Craticula.Conformance.Tests/ReleasePleaseConfigTests.cs on every `dotnet test`).
"""
import json
import re
import unittest
import xml.etree.ElementTree as ET
from pathlib import Path

try:
    import yaml
except ImportError:  # pragma: no cover
    yaml = None

ROOT = Path(__file__).resolve().parent.parent
CONFIG = ROOT / "release-please-config.json"
MANIFEST = ROOT / ".release-please-manifest.json"
PROPS = ROOT / "csharp/src/Directory.Build.props"
WORKFLOW = ROOT / ".github/workflows/release-please.yml"
RELEASE_YML = "./.github/workflows/release.yml"
RELEASE_FILE = ROOT / ".github/workflows/release.yml"
XPATH = "//Project/PropertyGroup/Version"
README = ROOT / "README.md"
MARKER = "x-release-please-version"
SEMVER = re.compile(r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?")


def props_version() -> str:
    versions = ET.parse(PROPS).getroot().findall("./PropertyGroup/Version")
    return versions[0].text.strip()


class ConfigTests(unittest.TestCase):
    def setUp(self):
        self.config = json.loads(CONFIG.read_text(encoding="utf-8"))
        self.package = self.config["packages"]["."]

    def test_manifest_equals_props_version(self):
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
        self.assertEqual(
            {".": props_version()}, manifest,
            ".release-please-manifest.json and <Version> in csharp/src/Directory.Build.props must move together "
            "(Release Please moves both in its release pull request; a hand edit of one is a mistake).")

    def test_version_xpath_matches_exactly_one_element(self):
        entries = [f for f in self.package["extra-files"] if f.get("path") == "csharp/src/Directory.Build.props"]
        self.assertEqual(1, len(entries))
        self.assertEqual("xml", entries[0]["type"])
        self.assertEqual(XPATH, entries[0]["xpath"])
        root = ET.parse(PROPS).getroot()
        self.assertEqual("Project", root.tag)
        self.assertEqual(1, len(root.findall("./PropertyGroup/Version")),
                         "exactly one <Version> under Project/PropertyGroup, or the xml updater edits the wrong one")
        self.assertEqual(1, len(root.findall(".//Version")), "no other <Version> element anywhere in the file")

    def test_extra_files_are_the_props_xml_and_the_readme_generic(self):
        entries = {f["path"]: f for f in self.package["extra-files"]}
        self.assertEqual({"csharp/src/Directory.Build.props", "README.md"}, set(entries), "extra-files entries")
        self.assertEqual(2, len(self.package["extra-files"]))
        self.assertEqual("xml", entries["csharp/src/Directory.Build.props"]["type"])
        self.assertEqual("generic", entries["README.md"]["type"])

    def test_readme_marker_is_one_line_with_one_semver_equal_to_the_manifest(self):
        lines = [ln for ln in README.read_text(encoding="utf-8").splitlines() if MARKER in ln]
        self.assertEqual(1, len(lines), f"README.md: the {MARKER} marker must be on exactly one line, found {len(lines)}")
        line = lines[0]
        self.assertTrue(line.startswith("Latest release: "), f"README.md: marked line is {line!r}")
        self.assertFalse(line.lstrip().startswith("|"), "README.md: the marked line must not be a table row")
        found = SEMVER.findall(line.split("<!--")[0])
        self.assertEqual(1, len(found), f"README.md: the marked line must hold exactly one semver, found {found}")
        self.assertEqual(1, len(SEMVER.findall(line)), "README.md: the comment part must not hold a version either")
        manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))["."]
        self.assertEqual(manifest, found[0], "README.md: the 'Latest release' version must equal .release-please-manifest.json")

    def test_tag_format_is_dotnet_v_semver(self):
        merged = {**self.config, **self.package}
        self.assertTrue(merged["include-component-in-tag"])
        self.assertTrue(merged["include-v-in-tag"])
        self.assertEqual("dotnet", merged["component"])
        tag = f"{merged['component']}{merged['tag-separator']}v{props_version()}"
        self.assertRegex(tag, r"^dotnet-v\d+\.\d+\.\d+$")

    def test_release_type_changelog_and_no_pre_major(self):
        self.assertEqual("simple", self.package["release-type"])
        self.assertEqual("CHANGELOG.md", self.config["changelog-path"])
        for key in ("bump-minor-pre-major", "bump-patch-for-minor-pre-major"):
            self.assertNotIn(key, self.config)
            self.assertNotIn(key, self.package)
        sections = {s["type"]: s.get("hidden", False) for s in self.config["changelog-sections"]}
        self.assertFalse(sections["feat"])
        self.assertFalse(sections["fix"])
        self.assertTrue(sections["chore"], "chore stays hidden: every private TECHNICAL commit maps to chore")


class WorkflowTests(unittest.TestCase):
    def setUp(self):
        self.text = WORKFLOW.read_text(encoding="utf-8")

    def test_every_uses_is_a_full_sha_or_local(self):
        uses = re.findall(r"^\s*(?:-\s*)?uses:\s*(\S+)", self.text, re.M)
        self.assertTrue(uses)
        for ref in uses:
            self.assertTrue(
                ref.startswith("./") or re.fullmatch(r"[\w.-]+/[\w./-]+@[0-9a-f]{40}", ref),
                f"'{ref}' is neither a local path nor pinned to a 40-hex commit SHA")

    def test_no_secret_reference(self):
        self.assertNotIn("secrets.", self.text)

    def test_parsed_triggers_and_permissions(self):
        if yaml is None:
            self.skipTest("PyYAML is not installed: YAML structure checks skipped (line checks above still ran)")
        wf = yaml.safe_load(self.text)
        triggers = wf.get("on", wf.get(True))  # PyYAML reads the key `on` as boolean True
        self.assertEqual({"push": {"branches": ["main"]}}, triggers)
        self.assertEqual({"contents": "read"}, wf["permissions"])
        jobs = wf["jobs"]
        rp = jobs["release-please"]
        self.assertEqual("write", rp["permissions"]["pull-requests"])
        self.assertEqual("write", rp["permissions"]["contents"])
        callers = [name for name, job in jobs.items() if job.get("uses") == RELEASE_YML]
        self.assertEqual(["release"], callers)
        for name, job in jobs.items():
            has_id_token = job.get("permissions", {}).get("id-token") == "write"
            self.assertEqual(name in callers, has_id_token, f"id-token: write only on the job calling release.yml ({name})")
        release = jobs["release"]
        self.assertEqual("release-please", release["needs"])
        self.assertEqual("needs.release-please.outputs.release_created == 'true'", release["if"])
        self.assertNotIn("secrets", release)
        self.assertEqual("${{ needs.release-please.outputs.version }}", release["with"]["version"])
        self.assertNotEqual("release", wf["concurrency"]["group"], "same group as release.yml deadlocks the called run")
        self.assertFalse(wf["concurrency"]["cancel-in-progress"])
        self.assertEqual("write", release["permissions"]["attestations"])
        self.assertEqual("write", release["permissions"]["contents"])


class ReleaseWorkflowTests(unittest.TestCase):
    def setUp(self):
        self.text = RELEASE_FILE.read_text(encoding="utf-8")

    def test_every_uses_is_a_full_sha_or_local(self):
        uses = re.findall(r"^\s*(?:-\s*)?uses:\s*(\S+)", self.text, re.M)
        self.assertTrue(uses)
        for ref in uses:
            self.assertTrue(ref.startswith("./") or re.fullmatch(r"[\w.-]+/[\w./-]+@[0-9a-f]{40}", ref), ref)

    def test_no_secret_reference(self):
        self.assertNotIn("secrets.", self.text)

    def test_tag_is_dotnet_v_only(self):
        self.assertIn("dotnet-v$VERSION", self.text)
        self.assertNotRegex(self.text, r'"v\$VERSION"|"v\$\{VERSION\}"|(?<![-\w])v<version>')
        self.assertNotIn("--clobber", self.text)

    def test_triggers_permissions_and_steps(self):
        if yaml is None:
            self.skipTest("PyYAML is not installed")
        wf = yaml.safe_load(self.text)
        triggers = wf.get("on", wf.get(True))
        self.assertEqual({"workflow_call", "workflow_dispatch"}, set(triggers))
        for name in triggers:
            version = triggers[name]["inputs"]["version"]
            self.assertEqual("string", version["type"])
            self.assertTrue(version["required"])
        self.assertEqual({"contents": "read"}, wf["permissions"])
        for name, job in wf["jobs"].items():
            self.assertEqual(name == "publish", job.get("permissions", {}).get("id-token") == "write", name)
        self.assertEqual("nuget", wf["jobs"]["publish"]["environment"])
        conds = {s["name"]: s.get("if") for s in wf["jobs"]["publish"]["steps"] if "name" in s}
        self.assertEqual("github.event_name == 'workflow_dispatch'", conds["GitHub release (manual dispatch)"])
        self.assertEqual("github.event_name != 'workflow_dispatch'", conds["Attach packages to the Release Please release"])

    def test_publish_job_runs_no_checkout_and_no_repository_script(self):
        if yaml is None:
            self.skipTest("PyYAML is not installed")
        wf = yaml.safe_load(self.text)
        for step in wf["jobs"]["publish"]["steps"]:
            self.assertFalse(str(step.get("uses", "")).startswith("actions/checkout@"), "publish has no checkout")
            self.assertNotIn("release_notes.py", str(step.get("run", "")))
        self.assertEqual({"id-token": "write", "attestations": "write", "contents": "write"},
                         wf["jobs"]["publish"]["permissions"])
        self.assertEqual({"contents": "read"}, wf["permissions"])

    def test_release_notes_job_is_separate_tolerant_least_privilege_and_env_only(self):
        if yaml is None:
            self.skipTest("PyYAML is not installed")
        wf = yaml.safe_load(self.text)
        job = wf["jobs"]["release-notes"]
        self.assertEqual("publish", job["needs"])
        self.assertIs(True, job["continue-on-error"])
        self.assertEqual({"contents": "write"}, job["permissions"])
        self.assertNotIn("environment", job)
        steps = job["steps"]
        checkout = steps[0]
        self.assertTrue(checkout["uses"].startswith("actions/checkout@"))
        self.assertIs(False, checkout["with"]["persist-credentials"])
        self.assertEqual(["spec", "scripts/release_notes.py"], checkout["with"]["sparse-checkout"].split())
        self.assertIs(False, checkout["with"]["sparse-checkout-cone-mode"])
        named = [s for s in steps if s.get("name") == "Add the spec version to the release notes"]
        self.assertEqual(1, len(named))
        step = named[0]
        self.assertEqual({"GH_TOKEN": "${{ github.token }}", "VERSION": "${{ inputs.version }}"}, step["env"])
        self.assertNotIn("${{", step["run"], "values reach the script through env only")
        self.assertIn("::warning::", step["run"])
        self.assertNotIn("secrets.", str(job))
        self.assertNotIn("id-token", str(job))
        for name, other in wf["jobs"].items():
            self.assertEqual(name == "publish", other.get("permissions", {}).get("id-token") == "write", name)


    def test_sbom_job_runs_after_publish_and_cannot_fail_the_release(self):
        if yaml is None:
            self.skipTest("PyYAML is not installed")
        wf = yaml.safe_load(self.text)
        job = wf["jobs"]["sbom"]
        self.assertEqual("publish", job["needs"])
        self.assertIs(True, job["continue-on-error"])
        self.assertEqual({"contents": "write"}, job["permissions"])
        self.assertNotIn("environment", job)
        self.assertNotIn("id-token", str(job))
        self.assertNotIn("secrets.", str(job))
        self.assertNotIn("--clobber", str(job))
        for step in job["steps"]:
            if "uses" in step:
                self.assertRegex(step["uses"], r"@[0-9a-f]{40}$")
        self.assertIs(False, job["steps"][0]["with"]["persist-credentials"])

    def test_sbom_job_pins_the_tool_covers_the_src_projects_and_passes_values_through_env(self):
        if yaml is None:
            self.skipTest("PyYAML is not installed")
        job = yaml.safe_load(self.text)["jobs"]["sbom"]
        install = [s for s in job["steps"] if s.get("name") == "Install the CycloneDX tool"]
        self.assertEqual(1, len(install))
        self.assertRegex(install[0]["run"], r"dotnet tool install CycloneDX --version \d+\.\d+\.\d+ ")
        step = [s for s in job["steps"] if s.get("name") == "Generate and attach one SBOM per packed project"][0]
        self.assertEqual({"GH_TOKEN": "${{ github.token }}", "VERSION": "${{ inputs.version }}"}, step["env"])
        self.assertNotIn("${{", step["run"], "values reach the script through env only")
        self.assertIn("csharp/src/*/*.csproj", step["run"])
        self.assertNotIn("examples/", step["run"])
        self.assertIn('gh release upload "dotnet-v$VERSION" sbom/*.cdx.json', step["run"])
        self.assertIn("::warning::", step["run"])


if __name__ == "__main__":
    unittest.main()
