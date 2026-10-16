#!/usr/bin/env python3
"""Pins the supply-chain rules of every GitHub workflow offline (story #2467, NFR-02): each `uses:` of a third-party
or local-looking action is pinned to a 40-character commit SHA, every workflow declares a top-level `permissions:`
block, and the public CI (`ci.yml`) references no secret. Line checks only, stdlib only, no network. A reusable-workflow
call to this repository (`./.github/workflows/...`) needs no pin. Run: python3 scripts/test_workflows.py
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
SECRET = re.compile(r"\bsecrets\.", re.IGNORECASE)


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


def secret_references(text: str) -> list[str]:
    """Lines that mention `secrets.` outside a comment."""
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

    def test_every_workflow_declares_permissions(self):
        for path in self.workflows():
            with self.subTest(workflow=path.name):
                self.assertTrue(has_top_level_permissions(path.read_text(encoding="utf-8")))

    def test_public_ci_uses_no_secret(self):
        ci = WORKFLOWS / "ci.yml"
        self.assertTrue(ci.exists(), "ci.yml is missing")
        self.assertEqual([], secret_references(ci.read_text(encoding="utf-8")))

    def test_public_ci_has_the_two_required_checks(self):
        text = (WORKFLOWS / "ci.yml").read_text(encoding="utf-8")
        for job in ("build", "test"):
            with self.subTest(job=job):
                self.assertRegex(text, rf"(?m)^  {job}:$")


if __name__ == "__main__":
    unittest.main()
