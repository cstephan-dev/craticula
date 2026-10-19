#!/usr/bin/env python3
"""Pins the supply-chain rules of every GitHub workflow offline (story #2467, NFR-02): each `uses:` of a third-party
or local-looking action is pinned to a 40-character commit SHA, every workflow declares a top-level `permissions:`
block, and the public CI (`ci.yml`) references no secret. Line checks only, stdlib only, no network. A reusable-workflow
call to this repository (`./.github/workflows/...`) needs no pin. A mapping line with no space after its colon (`run:bash x`,
story #1662 FR-02) is invalid YAML that no job could survive; `key_without_space` catches that class without a YAML library,
and every workflow is also parsed with PyYAML when it is installed. Run: python3 scripts/test_workflows.py
(also run by Craticula.Conformance.Tests/WorkflowLintTests.cs on every `dotnet test`).
"""
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
WORKFLOWS = ROOT / ".github" / "workflows"
USES = re.compile(r"^\s*(?:-\s+)?uses:\s*(?P<ref>\S+)")
SHA_PIN = re.compile(r"^[^@\s]+@[0-9a-f]{40}$")
PERMISSIONS = re.compile(r"^permissions:", re.MULTILINE)
SECRET = re.compile(r"\bsecrets\b", re.IGNORECASE)
READ_ONLY_PERMISSIONS = re.compile(r"^permissions:\n  contents: read\n(?!  )", re.MULTILINE)
JOB_PERMISSIONS = re.compile(r"^    permissions:", re.MULTILINE)
PIPED_RUN = re.compile(r"^\s*run:\s.*\|", re.MULTILINE)
EXPLICIT_BASH = re.compile(r"^\s*shell:\s*bash\s*$", re.MULTILINE)
KEY_WITHOUT_SPACE = re.compile(r"^(?P<key>[A-Za-z_][\w.-]*):(?P<rest>[^\s/].*)$")
BLOCK_SCALAR = re.compile(r":\s+[|>][+-]?\d*\s*(?:#.*)?$")


def unpinned_uses(text: str) -> list[str]:
    """The `uses:` values that are neither a local path nor `owner/action@<40 hex>`."""
    bad = []
    for line in text.splitlines():
        match = USES.match(line)
        if not match:
            continue
        ref = match.group("ref").strip("'\"")
        if ref.startswith("./"):
            continue
        if not SHA_PIN.match(ref):
            bad.append(ref)
    return bad


def has_top_level_permissions(text: str) -> bool:
    return PERMISSIONS.search(text) is not None


def is_read_only(text: str) -> bool:
    """Top-level permissions are exactly `contents: read` and no job raises them."""
    return READ_ONLY_PERMISSIONS.search(text) is not None and JOB_PERMISSIONS.search(text) is None


def pipe_hides_failure(text: str) -> bool:
    """A single-line `run:` with a pipe and no explicit `shell: bash` (default `bash -e` has no pipefail)."""
    return PIPED_RUN.search(text) is not None and EXPLICIT_BASH.search(text) is None


def key_without_space(text: str) -> list[str]:
    """Mapping lines written `key:value` with no space after the colon (FR-02: `run:bash x` is not a mapping entry, so the
    whole file fails to load). Comments, blank lines, the bodies of block scalars (`run: |`) and list items (`- a:b` is a plain
    string) are skipped. Stdlib only, a line scan: it does not replace a parser, it catches the one class a regex lint missed."""
    bad = []
    block_column = None
    for line in text.splitlines():
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        indent = len(line) - len(line.lstrip(" "))
        if block_column is not None:
            if indent > block_column:
                continue
            block_column = None
        content = line.lstrip(" ")
        key_column = indent
        while content.startswith("- "):
            content = content[2:].lstrip(" ")
            key_column = len(line) - len(content)
        if BLOCK_SCALAR.search(line):
            block_column = key_column
        elif not stripped.startswith("- ") and KEY_WITHOUT_SPACE.match(content):
            bad.append(stripped)
    return bad


def secret_references(text: str) -> list[str]:
    """Lines that mention `secrets` outside a comment."""
    return [line.strip() for line in text.splitlines() if SECRET.search(line) and not line.lstrip().startswith("#")]


class RuleFunctionTests(unittest.TestCase):
    """Each rule has a case that must fail, so a rule that stops checking is noticed."""

    def test_sha_pin_is_accepted(self):
        self.assertEqual([], unpinned_uses("      - uses: actions/checkout@" + "a" * 40 + " # v7.0.1\n"))

    def test_tag_pin_fails(self):
        self.assertEqual(["actions/checkout@v4"], unpinned_uses("      - uses: actions/checkout@v4\n"))

    def test_branch_pin_fails(self):
        self.assertEqual(["owner/action@main"], unpinned_uses("    uses: owner/action@main\n"))

    def test_short_sha_fails(self):
        self.assertEqual(["owner/action@" + "a" * 39], unpinned_uses("      - uses: owner/action@" + "a" * 39 + "\n"))

    def test_local_reusable_workflow_needs_no_pin(self):
        self.assertEqual([], unpinned_uses("    uses: ./.github/workflows/release.yml\n"))

    def test_missing_permissions_fails(self):
        self.assertFalse(has_top_level_permissions("name: x\non: push\njobs: {}\n"))

    def test_nested_permissions_do_not_count(self):
        self.assertFalse(has_top_level_permissions("name: x\njobs:\n  a:\n    permissions:\n      contents: read\n"))

    def test_top_level_permissions_pass(self):
        self.assertTrue(has_top_level_permissions("name: x\npermissions:\n  contents: read\n"))

    def test_secret_reference_fails(self):
        self.assertEqual(["token: ${{ secrets.TOKEN }}"], secret_references("        token: ${{ secrets.TOKEN }}\n"))

    def test_bracket_and_json_secret_forms_fail(self):
        self.assertEqual(2, len(secret_references("a: ${{ secrets['X'] }}\nb: ${{ toJSON(secrets) }}\n")))

    def test_read_only_permissions_pass(self):
        self.assertTrue(is_read_only("name: x\npermissions:\n  contents: read\n\njobs: {}\n"))

    def test_write_permissions_fail(self):
        self.assertFalse(is_read_only("name: x\npermissions: write-all\n"))
        self.assertFalse(is_read_only("name: x\npermissions:\n  contents: write\n"))

    def test_job_level_permissions_fail(self):
        self.assertFalse(is_read_only("permissions:\n  contents: read\njobs:\n  a:\n    permissions:\n      contents: write\n"))

    def test_pipe_without_shell_fails(self):
        self.assertTrue(pipe_hides_failure("        run: bash x.sh 2>&1 | tee log\n"))

    def test_pipe_with_explicit_bash_passes(self):
        self.assertFalse(pipe_hides_failure("        shell: bash\n        run: bash x.sh | tee log\n"))

    def test_key_without_space_fails_on_the_broken_ci_line(self):
        """FR-02: the exact line commit 78a7c9c wrote into ci.yml."""
        line = '        run:bash scripts/coverage.sh 2>&1 | tee "$RUNNER_TEMP/coverage.log"\n'
        self.assertEqual(['run:bash scripts/coverage.sh 2>&1 | tee "$RUNNER_TEMP/coverage.log"'], key_without_space(line))

    def test_key_without_space_fails_on_other_keys(self):
        self.assertEqual(["name:Build", "uses:actions/checkout@v4"], key_without_space("name:Build\n    uses:actions/checkout@v4\n"))

    def test_key_with_space_passes(self):
        self.assertEqual([], key_without_space("      - name: Test\n        run: bash x.sh\n    needs:\n      - build\n"))

    def test_block_scalar_bodies_are_not_scanned(self):
        text = "      - run: |\n          echo a:b\n          url:x\n      - name: next\n"
        self.assertEqual([], key_without_space(text))

    def test_key_without_space_resumes_after_a_block_scalar(self):
        text = "        run: |\n          echo a:b\n        shell:bash\n"
        self.assertEqual(["shell:bash"], key_without_space(text))

    def test_urls_comments_and_expressions_are_not_flagged(self):
        text = "# note a:b\n  https://example.org/x\n  if: ${{ github.event_name == 'push' }}\n  cron: '0 3 * * 1'\n"
        self.assertEqual([], key_without_space(text))

    def test_secret_in_comment_is_ignored(self):
        self.assertEqual([], secret_references("# No secret: nothing reads secrets.X here\n"))


class RealWorkflowTests(unittest.TestCase):
    def workflows(self):
        files = sorted(WORKFLOWS.glob("*.yml")) + sorted(WORKFLOWS.glob("*.yaml"))
        self.assertTrue(files, "no workflow found under .github/workflows")
        return files

    def test_every_action_is_pinned_to_a_sha(self):
        for path in self.workflows():
            with self.subTest(workflow=path.name):
                self.assertEqual([], unpinned_uses(path.read_text(encoding="utf-8")))

    def test_every_workflow_has_a_space_after_each_key_colon(self):
        """FR-02: stdlib check, always runs."""
        for path in self.workflows():
            with self.subTest(workflow=path.name):
                self.assertEqual([], key_without_space(path.read_text(encoding="utf-8")))

    def test_every_workflow_parses_as_yaml(self):
        """FR-02: a full parse when PyYAML is installed (it is not a dependency of the repository)."""
        try:
            import yaml
        except ImportError:
            self.skipTest("PyYAML is not installed; test_every_workflow_has_a_space_after_each_key_colon still runs")
        for path in self.workflows():
            with self.subTest(workflow=path.name):
                self.assertIsInstance(yaml.safe_load(path.read_text(encoding="utf-8")), dict)

    def test_every_workflow_declares_permissions(self):
        for path in self.workflows():
            with self.subTest(workflow=path.name):
                self.assertTrue(has_top_level_permissions(path.read_text(encoding="utf-8")))

    def test_public_ci_uses_no_secret(self):
        ci = WORKFLOWS / "ci.yml"
        self.assertTrue(ci.exists(), "ci.yml is missing")
        self.assertEqual([], secret_references(ci.read_text(encoding="utf-8")))

    def test_public_ci_is_read_only(self):
        self.assertTrue(is_read_only((WORKFLOWS / "ci.yml").read_text(encoding="utf-8")))

    def test_public_ci_pipes_run_under_explicit_bash(self):
        self.assertFalse(pipe_hides_failure((WORKFLOWS / "ci.yml").read_text(encoding="utf-8")))

    def test_public_ci_has_the_two_required_checks(self):
        text = (WORKFLOWS / "ci.yml").read_text(encoding="utf-8")
        for job in ("build", "test"):
            with self.subTest(job=job):
                self.assertRegex(text, rf"(?m)^  {job}:$")


if __name__ == "__main__":
    unittest.main()
