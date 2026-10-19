#!/usr/bin/env python3
"""Pins the governance files of the public repository (story #1662): CODEOWNERS (FR-03, AC-3), SECURITY.md (FR-04, AC-5, SC-02),
the DCO workflow's presence (FR-02), the rule that these public files name no private path (NFR-01), and, where the private
documents exist, that the gate list of the repository's flow rule and its two copies agree and name the governance files.
Line checks only, stdlib only, no network. The private documents (`.claude/`, `docs/`) are absent from the public mirror: the
gate-list tests then skip. Run: python3 scripts/test_governance_files.py
(also run by Craticula.Conformance.Tests/GovernanceFilesTests.cs on every `dotnet test`).
"""
import os
import re
import unittest
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
CODEOWNERS = ROOT / ".github" / "CODEOWNERS"
SECURITY = ROOT / "SECURITY.md"
PRIVATE_PREFIXES = ("docs/", ".claude", "CLAUDE.md", ".mcp.json", ".githooks", ".azuredevops", ".sonarlint", ".vscode")
OWNER = re.compile(r"^(?:@[A-Za-z0-9][A-Za-z0-9-]*(?:/[A-Za-z0-9._-]+)?|[^@\s]+@[^@\s]+\.[A-Za-z]+)$")
GATE_COPIES = (".claude/rules/git-branch-naming.md", "docs/compliance/isms-change-pr-pipeline.md", "docs/compliance/pipelines-craticula.md")
# What the governance stories add to the gate: a pull request touching these validates its own copy of the checks or changes
# what the publish accepts, so the agent never completes it (the maker does).
GATE_GOVERNANCE = (".github/CODEOWNERS", "scripts/check-dco*", "scripts/test_check_dco*", "scripts/apply-public-change*",
                   "scripts/test_apply_public_change*", "scripts/test_governance_files*", ".githooks/", ".github/workflows/")


def codeowners_rules(text: str) -> list[tuple[str, list[str]]]:
    """(pattern, owners) per rule line; comments and blank lines are skipped."""
    rules = []
    for line in text.splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        parts = line.split()
        rules.append((parts[0], parts[1:]))
    return rules


def codeowners_problems(text: str, root: Path) -> list[str]:
    """What is wrong with a CODEOWNERS file: a rule without an owner, an owner that is not `@user`, `@org/team` or an e-mail,
    a pattern that matches nothing in the tree, a catch-all that is not the first rule (the last matching pattern wins, so a
    catch-all placed later would override every specific rule), a missing `csharp/src/` rule."""
    problems = []
    rules = codeowners_rules(text)
    if not rules:
        return ["no rule"]
    if rules[0][0] != "*":
        problems.append("the catch-all `*` must be the first rule (the last matching pattern wins)")
    if not any(pattern.strip("/") == "csharp/src" for pattern, _ in rules):
        problems.append("no rule for /csharp/src/")
    for pattern, owners in rules:
        if not owners:
            problems.append(f"{pattern}: no owner")
        for owner in owners:
            if not OWNER.match(owner):
                problems.append(f"{pattern}: '{owner}' is not an @user, @org/team or e-mail owner")
        if pattern != "*":
            target = pattern.lstrip("/").rstrip("/")
            exists = any(root.glob(target)) if any(c in target for c in "*?[") else (root / target).exists()
            if not exists:
                problems.append(f"{pattern}: matches nothing in the tree")
    return problems


def security_problems(text: str, root: Path) -> list[str]:
    """What is wrong with SECURITY.md: no private-report path (Security tab, Report a vulnerability), no response time in days,
    no supported-versions table with a supported row, a public issue or mailbox offered instead, a link that does not resolve
    inside the repository or points at a private path, an e-mail address."""
    problems = []
    if not re.search(r"\*\*Security\*\* tab", text) or "Report a vulnerability" not in text:
        problems.append("no private-report path (the Security tab, 'Report a vulnerability')")
    if len(re.findall(r"within \*\*\d+ (?:calendar )?days\*\*", text)) < 2:
        problems.append("fewer than two response times in days (acknowledgement and first assessment)")
    if not re.search(r"\|\s*Version\s*\|\s*Supported\s*\|", text) or not re.search(r"\|[^|\n]+\|\s*Yes\b", text):
        problems.append("no supported-versions table with a supported row")
    if re.search(r"[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}", text):
        problems.append("an e-mail address (the policy names the Security tab only)")
    for target in re.findall(r"\]\(([^)\s]+)\)", text):
        if re.match(r"^[a-z][a-z0-9+.-]*:", target):
            if "dev.azure.com" in target or "visualstudio.com" in target:
                problems.append(f"link to a private host: {target}")
            continue
        path = target.split("#")[0]
        if path.startswith(PRIVATE_PREFIXES):
            problems.append(f"link into a private path: {target}")
        elif path and not (root / path).exists():
            problems.append(f"link that does not resolve: {target}")
    return problems


def private_references(text: str) -> list[str]:
    """Lines of a public file that name a private path (`docs/`, `.claude`, `CLAUDE.md`, `.mcp.json`, `.githooks`, ...): the
    mirror strips them, so an outsider could not follow the reference (NFR-01)."""
    found = []
    for line in text.splitlines():
        if any(re.search(rf"(?<![\w./-]){re.escape(prefix)}", line) for prefix in PRIVATE_PREFIXES):
            found.append(line.strip())
    return found


def gate_paths(text: str) -> set[str]:
    """The backticked paths of the gate list in `text`: from the `.azuredevops/` entry nearest before `csharp/**/Directory.*.props`
    up to that entry (the list ends there in the flow rule and in both copies; an earlier mention of `.azuredevops/` in
    the surrounding prose is not part of it)."""
    for end in re.finditer(r"Directory\.\*\.props", text):
        start = text.rfind("`.azuredevops/`", 0, end.start())
        if start != -1 and end.start() - start < 2500:
            return set(re.findall(r"`([^`]+)`", text[start:end.end() + 1]))
    return set()


class CodeownersRuleTests(unittest.TestCase):
    """Each rule has a case that must fail, so a rule that stops checking is noticed."""

    def setUp(self):
        self.root = ROOT

    def test_fr03_a_good_file_has_no_problem(self):
        self.assertEqual([], codeowners_problems("*  @pluries\n/csharp/src/  @pluries\n/spec/  @pluries\n", self.root))

    def test_fr03_no_owner_fails(self):
        self.assertIn("/spec/: no owner", codeowners_problems("*  @pluries\n/csharp/src/  @pluries\n/spec/\n", self.root))

    def test_fr03_a_bare_word_owner_fails(self):
        self.assertTrue(any("not an @user" in p for p in codeowners_problems("*  pluries\n/csharp/src/  @pluries\n", self.root)))

    def test_fr03_an_org_team_and_an_email_are_valid_owners(self):
        self.assertEqual([], codeowners_problems("*  @org/team a@example.org\n/csharp/src/  @org/team\n", self.root))

    def test_fr03_a_pattern_that_matches_nothing_fails(self):
        self.assertIn("/nosuchdir/: matches nothing in the tree",
                      codeowners_problems("*  @pluries\n/csharp/src/  @pluries\n/nosuchdir/  @pluries\n", self.root))

    def test_fr03_a_glob_pattern_is_resolved(self):
        self.assertEqual([], codeowners_problems("*  @pluries\n/csharp/src/  @pluries\n/scripts/test_*.py  @pluries\n", self.root))
        self.assertIn("/scripts/zzz_*.py: matches nothing in the tree",
                      codeowners_problems("*  @pluries\n/csharp/src/  @pluries\n/scripts/zzz_*.py  @pluries\n", self.root))

    def test_fr03_a_catch_all_after_a_specific_rule_fails(self):
        self.assertTrue(any("catch-all" in p for p in codeowners_problems("/csharp/src/  @pluries\n*  @pluries\n", self.root)))

    def test_fr03_a_missing_csharp_src_rule_fails(self):
        self.assertIn("no rule for /csharp/src/", codeowners_problems("*  @pluries\n/spec/  @pluries\n", self.root))

    def test_fr03_an_empty_file_fails(self):
        self.assertEqual(["no rule"], codeowners_problems("# only a comment\n\n", self.root))


class SecurityRuleTests(unittest.TestCase):
    GOOD = ("# Security policy\n\n1. Open the **Security** tab of this repository.\n2. Choose **Report a vulnerability**.\n\n"
            "We acknowledge within **7 calendar days** and assess within **30 days**.\n\n"
            "| Version | Supported |\n|---|---|\n| The latest release | Yes |\n\nSee [VERSIONING.md](VERSIONING.md).\n")

    def test_fr04_a_good_file_has_no_problem(self):
        self.assertEqual([], security_problems(self.GOOD, ROOT))

    def test_fr04_no_private_report_path_fails(self):
        self.assertTrue(security_problems(self.GOOD.replace("**Report a vulnerability**", "write to us"), ROOT))

    def test_fr04_a_single_response_time_fails(self):
        self.assertTrue(security_problems(self.GOOD.replace(" and assess within **30 days**", ""), ROOT))

    def test_fr04_no_supported_versions_row_fails(self):
        self.assertTrue(security_problems(self.GOOD.replace("| The latest release | Yes |", "| The latest release | No |"), ROOT))
        self.assertTrue(security_problems(self.GOOD.replace("| Version | Supported |", "| Release | Status |"), ROOT))

    def test_fr04_a_mailbox_fails(self):
        self.assertIn("an e-mail address (the policy names the Security tab only)",
                      security_problems(self.GOOD + "\nOr write to security@example.org.\n", ROOT))

    def test_nfr01_a_link_into_a_private_path_fails(self):
        for target in ("docs/adr/0001-x.md", ".claude/rules/x.md", "CLAUDE.md"):
            with self.subTest(target=target):
                self.assertTrue(any("private path" in p for p in security_problems(self.GOOD + f"\n[x]({target})\n", ROOT)))

    def test_nfr01_a_private_host_link_fails(self):
        self.assertTrue(any("private host" in p for p in security_problems(self.GOOD + "\n[x](https://dev.azure.com/o/p)\n", ROOT)))

    def test_fr04_a_dead_relative_link_fails(self):
        self.assertTrue(any("does not resolve" in p for p in security_problems(self.GOOD + "\n[x](NOSUCHFILE.md)\n", ROOT)))

    def test_fr04_an_external_https_link_is_not_resolved_against_the_tree(self):
        self.assertEqual([], security_problems(self.GOOD + "\n[x](https://developercertificate.org)\n", ROOT))


class PrivateReferenceRuleTests(unittest.TestCase):
    def test_nfr01_private_names_are_found(self):
        for line in ("see docs/adr/0001.md", "run .claude/hooks/x.js", "read CLAUDE.md", "the .mcp.json file", "in .githooks/commit-msg",
                     "the .azuredevops/pipeline-pr.yml pipeline"):
            with self.subTest(line=line):
                self.assertEqual([line], private_references(line))

    def test_nfr01_public_names_are_not_flagged(self):
        for line in ("see README.md and spec/semantics.md", "scripts/check-dco.sh", "csharp/src/Core", "the .github/workflows folder",
                     "mydocs/readme", "subdocs/x"):
            with self.subTest(line=line):
                self.assertEqual([], private_references(line))


class GatePathRuleTests(unittest.TestCase):
    ROW = ("**A PR that touches the gate** (`.azuredevops/`, `scripts/pack*`, `scripts/check-dco*`, `.github/workflows/`, "
           "`csharp/src/.editorconfig`, any `csharp/**/Directory.*.props`) validates its own copy `not/this`.")

    def test_the_list_is_read_up_to_the_props_entry(self):
        self.assertEqual({".azuredevops/", "scripts/pack*", "scripts/check-dco*", ".github/workflows/", "csharp/src/.editorconfig",
                          "csharp/**/Directory.*.props"}, gate_paths(self.ROW))

    def test_a_text_without_the_list_gives_nothing(self):
        self.assertEqual(set(), gate_paths("no gate list here, only `scripts/pack*`"))

    def test_an_earlier_mention_of_azuredevops_in_the_prose_is_not_part_of_the_list(self):
        text = "`.azuredevops/` is private; `scripts/` is published.\n\n" + self.ROW
        self.assertNotIn("scripts/", gate_paths(text))
        self.assertIn("scripts/check-dco*", gate_paths(text))

    def test_a_wrapped_list_is_read(self):
        wrapped = self.ROW.replace(", `scripts/pack*`", ",\n  `scripts/pack*`")
        self.assertIn("scripts/pack*", gate_paths(wrapped))


class RealFileTests(unittest.TestCase):
    def test_fr03_codeowners_exists_and_is_valid(self):
        self.assertTrue(CODEOWNERS.exists(), ".github/CODEOWNERS is missing")
        self.assertEqual([], codeowners_problems(CODEOWNERS.read_text(encoding="utf-8"), ROOT))

    def test_fr04_security_md_exists_and_is_valid(self):
        self.assertTrue(SECURITY.exists(), "SECURITY.md is missing")
        self.assertEqual([], security_problems(SECURITY.read_text(encoding="utf-8"), ROOT))

    def test_fr02_the_dco_workflow_and_its_checker_exist(self):
        self.assertTrue((ROOT / ".github" / "workflows" / "dco.yml").exists(), "dco.yml is missing")
        checker = ROOT / "scripts" / "check-dco.sh"
        self.assertTrue(checker.exists(), "scripts/check-dco.sh is missing")
        self.assertTrue(os.access(checker, os.X_OK) or os.name == "nt", "scripts/check-dco.sh is not executable")

    def test_nfr01_the_public_governance_files_name_no_private_path(self):
        for path in (SECURITY, CODEOWNERS, ROOT / ".github" / "workflows" / "dco.yml"):
            with self.subTest(file=path.name):
                self.assertEqual([], private_references(path.read_text(encoding="utf-8")))

    def test_the_gate_lists_name_the_governance_files_and_agree(self):
        present = {name: ROOT / name for name in GATE_COPIES if (ROOT / name).exists()}
        if not present:
            self.skipTest("the private documents are absent (the public mirror)")
        reference = None
        for name, path in present.items():
            with self.subTest(file=name):
                paths = gate_paths(path.read_text(encoding="utf-8"))
                self.assertTrue(paths, "no gate list found")
                self.assertEqual([], sorted(set(GATE_GOVERNANCE) - paths), "governance entries missing from the gate list")
                if reference is None:
                    reference = paths
                self.assertEqual(reference, paths, "the gate lists of the copies differ")


if __name__ == "__main__":
    unittest.main()
