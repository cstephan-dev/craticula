#!/usr/bin/env python3
"""Pins scripts/check-dco.sh (story #1662, FR-02, AC-2): the DCO check of the public pull request workflow.

Scratch repositories only, no network. Needs bash and git; skips where one is missing. Each case builds a base commit and a few
pull request commits with scripted authors, committers and messages, then runs the script on `<base>..<head>`.
Run: python3 scripts/test_check_dco.py (also run by Craticula.Conformance.Tests on every `dotnet test`).
"""
import os
import shutil
import subprocess
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "check-dco.sh"
NEED_TOOLS = unittest.skipUnless(all(shutil.which(t) for t in ("bash", "git")) and SCRIPT.exists(), "needs bash, git and the script")
ADA = ("Ada Lovelace", "ada@example.org")
GRACE = ("Grace Hopper", "grace@example.org")
BOT = ("github-actions[bot]", "41898282+github-actions[bot]@users.noreply.github.com")


def signed(person):
    return f"Signed-off-by: {person[0]} <{person[1]}>"


@NEED_TOOLS
class CheckDcoTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        config = self.tmp / "gitconfig"
        config.write_text("[user]\n\tname = Scratch\n\temail = scratch@example.invalid\n[init]\n\tdefaultBranch = main\n")
        self.env = {**os.environ, "GIT_CONFIG_GLOBAL": str(config), "GIT_CONFIG_NOSYSTEM": "1", "GIT_TERMINAL_PROMPT": "0"}
        self.repo = self.tmp / "repo"
        self.git("init", "-q", str(self.repo), cwd=self.tmp)
        self.counter = 0
        self.base = self.commit("the base, not signed off", ADA)

    def git(self, *args, cwd=None, check=True, env=None):
        done = subprocess.run(["git", *args], cwd=cwd or self.repo, env=env or self.env, capture_output=True, text=True)
        if check and done.returncode != 0:
            raise AssertionError(f"git {' '.join(args)} failed: {done.stdout}{done.stderr}")
        return done

    def commit(self, message, author=ADA, committer=None):
        self.counter += 1
        (self.repo / f"f{self.counter}.txt").write_text(f"{self.counter}\n")
        committer = committer or author
        env = {**self.env, "GIT_AUTHOR_NAME": author[0], "GIT_AUTHOR_EMAIL": author[1],
               "GIT_COMMITTER_NAME": committer[0], "GIT_COMMITTER_EMAIL": committer[1]}
        self.git("add", "-A", env=env)
        self.git("commit", "-q", "-m", message, env=env)
        return self.git("rev-parse", "HEAD").stdout.strip()

    def check(self, head=None, base=None, env_extra=None, summary=None):
        env = {**self.env, "DCO_REPO": str(self.repo), **(env_extra or {})}
        if summary:
            env["GITHUB_STEP_SUMMARY"] = str(summary)
        args = ["bash", str(SCRIPT), base or self.base, head or self.git("rev-parse", "HEAD").stdout.strip()]
        return subprocess.run(args, env=env, capture_output=True, text=True)

    def test_every_commit_signed_passes(self):
        self.commit(f"feat: one\n\n{signed(ADA)}")
        self.commit(f"fix: two\n\n{signed(ADA)}")
        done = self.check()
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertIn("2 commit(s) signed off", done.stdout)

    def test_one_unsigned_commit_fails_and_only_it_is_named(self):
        self.commit(f"feat: good one\n\n{signed(ADA)}")
        bad = self.commit("fix: forgot the sign-off")
        self.commit(f"feat: good two\n\n{signed(ADA)}")
        done = self.check()
        self.assertEqual(1, done.returncode)
        self.assertIn("1 of 3 commit(s)", done.stdout)
        self.assertIn(f"{bad[:7]} fix: forgot the sign-off", done.stdout)
        self.assertNotIn("good one", done.stdout)
        self.assertNotIn("good two", done.stdout)
        self.assertIn("git commit --amend -s", done.stdout)
        self.assertIn(f"git rebase --signoff {self.base}", done.stdout)

    def test_every_unsigned_commit_is_listed(self):
        a = self.commit("fix: first bad")
        b = self.commit("fix: second bad")
        done = self.check()
        self.assertEqual(1, done.returncode)
        self.assertIn(a[:7], done.stdout)
        self.assertIn(b[:7], done.stdout)
        self.assertIn("2 of 2", done.stdout)

    def test_someone_elses_signoff_fails(self):
        self.commit(f"fix: x\n\n{signed(GRACE)}", ADA)
        done = self.check()
        self.assertEqual(1, done.returncode)
        self.assertIn("ada@example.org", done.stdout)

    def test_a_signoff_in_prose_does_not_count(self):
        self.commit(f"fix: x\n\n{signed(ADA)}\n\nand some prose after it, so it is not a trailer\n")
        self.assertEqual(1, self.check().returncode)

    def test_a_malformed_signoff_does_not_count(self):
        self.commit("fix: x\n\nSigned-off-by: Ada Lovelace")
        self.assertEqual(1, self.check().returncode)
        self.commit("fix: y\n\nSigned-off-by: <ada@example.org>")
        self.assertEqual(1, self.check().returncode)

    def test_the_email_is_compared_without_case(self):
        self.commit("fix: x\n\nSigned-off-by: Ada <ADA@Example.ORG>")
        self.assertEqual(0, self.check().returncode)

    def test_the_committers_signoff_is_accepted(self):
        """A commit rebased or cherry-picked by somebody else: the committer signs."""
        self.commit(f"fix: x\n\n{signed(GRACE)}", author=ADA, committer=GRACE)
        self.assertEqual(0, self.check().returncode)

    def test_a_co_signed_squash_message_passes_when_the_author_signed(self):
        self.commit(f"fix: x\n\n{signed(ADA)}\n{signed(GRACE)}")
        self.assertEqual(0, self.check().returncode)

    def test_crlf_messages_are_read(self):
        self.commit(f"fix: x\r\n\r\n{signed(ADA)}\r\n")
        self.assertEqual(0, self.check().returncode)

    def test_a_horizontal_rule_in_the_message_does_not_hide_the_signoff(self):
        self.commit(f"fix: x\n\nabove\n\n---\n\nbelow\n\n{signed(ADA)}")
        self.assertEqual(0, self.check().returncode)

    def test_merge_commits_are_exempt(self):
        main = self.git("rev-parse", "HEAD").stdout.strip()
        self.git("switch", "-q", "-c", "side")
        self.commit(f"fix: side\n\n{signed(ADA)}")
        self.git("switch", "-q", "main")
        self.commit(f"fix: main\n\n{signed(ADA)}")
        self.git("merge", "-q", "--no-ff", "-m", "Merge side into main", "side")
        done = self.check(base=main)
        self.assertEqual(0, done.returncode, done.stdout + done.stderr)
        self.assertIn("2 commit(s)", done.stdout)

    def test_commits_already_in_the_base_are_ignored(self):
        self.commit("old: unsigned but already merged")
        base = self.git("rev-parse", "HEAD").stdout.strip()
        self.commit(f"fix: new\n\n{signed(ADA)}")
        self.assertEqual(0, self.check(base=base).returncode)

    def test_an_empty_range_passes(self):
        done = self.check(head=self.base)
        self.assertEqual(0, done.returncode)
        self.assertIn("0 commit(s)", done.stdout)

    def test_a_bot_author_email_is_not_an_exemption(self):
        """Anyone can write that e-mail into a commit."""
        self.commit("fix: pretends to be a bot", BOT)
        done = self.check()
        self.assertEqual(1, done.returncode)
        self.assertIn("pretends to be a bot", done.stdout)

    def test_the_exemption_comes_only_from_the_environment(self):
        self.commit("chore: release 1.2.3", BOT)
        done = self.check(env_extra={"DCO_EXEMPT": "pull request opened by the Release Please action (github-actions[bot])"})
        self.assertEqual(0, done.returncode)
        self.assertIn("DCO check skipped: pull request opened by the Release Please action", done.stdout)
        self.assertEqual(1, self.check(env_extra={"DCO_EXEMPT": ""}).returncode)

    def test_the_report_goes_to_the_job_summary_too(self):
        self.commit("fix: no sign-off")
        summary = self.tmp / "summary.md"
        self.assertEqual(1, self.check(summary=summary).returncode)
        self.assertIn("DCO check failed", summary.read_text())

    def test_bad_arguments_are_a_usage_error(self):
        env = {**self.env, "DCO_REPO": str(self.repo)}
        for args in ([], ["a" * 40], ["main", "HEAD"], ["--help", "x"], ["a" * 40, "b" * 40, "c" * 40], ["a" * 39, "b" * 40]):
            with self.subTest(args=args):
                done = subprocess.run(["bash", str(SCRIPT), *args], env=env, capture_output=True, text=True)
                self.assertEqual(2, done.returncode, done.stdout + done.stderr)

    def test_a_commit_missing_from_the_clone_is_a_clear_error(self):
        done = self.check(head="b" * 40)
        self.assertEqual(2, done.returncode)
        self.assertIn("not in this clone", done.stderr)


WORKFLOW = HERE.parent / ".github" / "workflows" / "dco.yml"


def workflow_script():
    """The shell text of the workflow's `run: |` step (stdlib line scan; the workflow has exactly one)."""
    lines = WORKFLOW.read_text(encoding="utf-8").splitlines()
    start = next(n for n, line in enumerate(lines) if line.strip() == "run: |")
    indent = len(lines[start]) - len(lines[start].lstrip(" "))
    body = []
    for line in lines[start + 1:]:
        if line.strip() and len(line) - len(line.lstrip(" ")) <= indent:
            break
        body.append(line[indent + 2:] if line.strip() else "")
    return "\n".join(body) + "\n"


@unittest.skipUnless(shutil.which("bash") and WORKFLOW.exists(), "needs bash and the workflow")
class WorkflowExemptionTests(unittest.TestCase):
    """The exemption of dco.yml: the pull request author's login, same repository only."""

    def exempt(self, author, fork):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "scripts").mkdir()
            (root / "scripts" / "check-dco.sh").write_text('echo "EXEMPT=[${DCO_EXEMPT:-}] ARGS=[$1 $2]"\n')
            (root / "run.sh").write_text(workflow_script())
            env = {**os.environ, "BASE_SHA": "a" * 40, "HEAD_SHA": "b" * 40, "PR_AUTHOR": author, "PR_FROM_FORK": fork}
            done = subprocess.run(["bash", "run.sh"], cwd=root, env=env, capture_output=True, text=True)
            self.assertEqual(0, done.returncode, done.stdout + done.stderr)
            return done.stdout

    def test_the_release_please_and_dependabot_pull_requests_are_exempt(self):
        self.assertIn("EXEMPT=[pull request opened by the Release Please action (github-actions[bot])]", self.exempt("github-actions[bot]", "false"))
        self.assertIn("EXEMPT=[pull request opened by Dependabot (dependabot[bot])]", self.exempt("dependabot[bot]", "false"))

    def test_the_shas_reach_the_script(self):
        self.assertIn(f"ARGS=[{'a' * 40} {'b' * 40}]", self.exempt("someone", "false"))

    def test_nobody_else_is_exempt(self):
        for author in ("someone", "github-actions[bot]x", "xgithub-actions[bot]", "github-actions", "dependabot", "Github-Actions[bot]", ""):
            with self.subTest(author=author):
                self.assertIn("EXEMPT=[]", self.exempt(author, "false"))

    def test_a_pull_request_from_a_fork_is_never_exempt(self):
        for fork in ("true", "", "null"):
            with self.subTest(fork=fork):
                self.assertIn("EXEMPT=[]", self.exempt("github-actions[bot]", fork))
                self.assertIn("EXEMPT=[]", self.exempt("dependabot[bot]", fork))


if __name__ == "__main__":
    unittest.main()
