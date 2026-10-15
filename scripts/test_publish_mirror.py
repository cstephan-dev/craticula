#!/usr/bin/env python3
"""Pins the private-to-public mirror (scripts/publish_mirror.py, publish-github.sh, sync-release-commit.sh), story #1659.

Scratch repositories and a scratch bare remote only: the real `github` remote is never contacted. The pure tests (message
mapping, Release Please's rule) always run; the script tests need bash, git, git-filter-repo and node, and the two private
scripts, which the public mirror does not carry, so they skip there. Run: python3 scripts/test_publish_mirror.py
(also run by Craticula.Conformance.Tests/PublishMirrorTests.cs on every `dotnet test`).
"""
import os
import shutil
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent
sys.path.insert(0, str(HERE))
import publish_mirror as mirror  # noqa: E402

PUBLISH = HERE / "publish-github.sh"
SYNC = HERE / "sync-release-commit.sh"
SECRETS = ROOT / ".claude" / "skills" / "cst-g-commit" / "scripts" / "secret-patterns.js"
REAL_BOUNDARY = "5cf28d5ace979797b1cd21c8c844677c58bd4dc9"   # private commit the public tip 6d1b260 was filtered from
REAL_PUBLIC_TIP = "6d1b260a1e94186c85d1d2dd6b3ce1e43322d848"
PROPS = "csharp/src/Directory.Build.props"
TOOLS = all(shutil.which(t) for t in ("bash", "git", "node")) and subprocess.run(
    ["git", "filter-repo", "--version"], capture_output=True).returncode == 0
NEED_SCRIPTS = unittest.skipUnless(
    TOOLS and PUBLISH.exists() and SYNC.exists() and SECRETS.exists(),
    "needs bash, git, git-filter-repo, node and the private scripts (absent from the public mirror)")


class MappingTests(unittest.TestCase):
    def test_subject_mapping_table(self):
        table = [
            (b"FEATURE #12 add leave filter\n", b"feat: add leave filter (#12)\n"),
            (b"FIX #7 null payroll\n", b"fix: null payroll (#7)\n"),
            (b"TECHNICAL #1650 close the session records\n", b"chore: close the session records (#1650)\n"),
            (b"Merge #1658: NuGet packaging, pack smoke\n", b"chore: merge NuGet packaging, pack smoke (#1658)\n"),
            (b"FEATURE #3 add x\n\nBody line.\n\nBREAKING CHANGE: drops y\n", b"feat: add x (#3)\n\nBody line.\n\nBREAKING CHANGE: drops y\n"),
            (b"FIX #4 a  \n", b"fix: a (#4)\n"),
            (b"FIX #4 no trailing newline", b"fix: no trailing newline (#4)"),
            # no match: unchanged
            (b"#1649: add shared envelope mapping\n", b"#1649: add shared envelope mapping\n"),
            (b"feat: already conventional\n", b"feat: already conventional\n"),
            (b"Initial commit\n", b"Initial commit\n"),
            (b"FEATURE add without id\n", b"FEATURE add without id\n"),
            (b"Merge branch 'x'\n", b"Merge branch 'x'\n"),
            (b"", b""),
        ]
        for before, after in table:
            with self.subTest(before=before):
                self.assertEqual(mirror.map_message(before), after)

    def test_frozen_commits_keep_their_message(self):
        with tempfile.TemporaryDirectory() as tmp:
            frozen = Path(tmp) / "frozen.txt"
            frozen.write_text("a" * 40 + "\n")
            self.assertEqual(mirror.map_unless_frozen(b"a" * 40, b"FIX #1 x\n", str(frozen)), b"FIX #1 x\n")
            self.assertEqual(mirror.map_unless_frozen(b"b" * 40, b"FIX #1 x\n", str(frozen)), b"fix: x (#1)\n")

    def test_release_please_rule(self):
        table = [("feat: x (#1)", "minor"), ("fix: x (#1)", "patch"), ("feat!: x", "major"), ("fix(core)!: x", "major"),
                 ("feat: x\n\nBREAKING CHANGE: y", "major"), ("chore: merge x (#3)", None), ("docs: x", None),
                 ("Merge #3: x", None), ("FIX #3 x", None), ("chore: merge the private history into the public mirror", None)]
        for message, expected in table:
            with self.subTest(message=message):
                self.assertEqual(mirror.release_type(message), expected)

    def test_mapped_merge_commits_open_no_release(self):
        for subject in (b"Merge #1658: NuGet packaging\n", b"Merge #2: FEATURE-ish text\n"):
            self.assertIsNone(mirror.release_type(mirror.map_message(subject).decode()))
        self.assertEqual(mirror.release_type(mirror.map_message(b"FEATURE #5 add a thing\n").decode()), "minor")
        self.assertEqual(mirror.release_type(mirror.map_message(b"FIX #5 repair a thing\n").decode()), "patch")
        self.assertIsNone(mirror.release_type(mirror.map_message(b"TECHNICAL #5 tidy\n").decode()))


def run(cwd, *cmd, env=None, check=True):
    result = subprocess.run(cmd, cwd=cwd, env=env, capture_output=True, text=True)
    if check and result.returncode != 0:
        raise AssertionError(f"{' '.join(map(str, cmd))} exited {result.returncode}\n{result.stdout}\n{result.stderr}")
    return result


@NEED_SCRIPTS
class ScriptTests(unittest.TestCase):
    def setUp(self):
        self.tmp = Path(tempfile.mkdtemp())
        self.addCleanup(shutil.rmtree, self.tmp, ignore_errors=True)
        config = self.tmp / "gitconfig"
        config.write_text("[user]\n\tname = Scratch Maker\n\temail = maker@example.invalid\n[init]\n\tdefaultBranch = main\n")
        self.env = {**os.environ, "GIT_CONFIG_GLOBAL": str(config), "GIT_CONFIG_NOSYSTEM": "1", "GIT_TERMINAL_PROMPT": "0"}
        self.priv = self.tmp / "priv"
        self.pub = self.tmp / "public.git"
        run(self.tmp, "git", "init", "-q", str(self.priv), env=self.env)
        run(self.tmp, "git", "init", "-q", "--bare", str(self.pub), env=self.env)
        run(self.pub, "git", "config", "receive.denyNonFastForwards", "true", env=self.env)   # a force push would fail
        self.write(".claude/rules.md", "private\n")
        self.write("docs/d.md", "private\n")
        self.write("README.md", "# scratch\n")
        self.write(PROPS, "<Project><PropertyGroup>\n<Version>1.0.0</Version>\n</PropertyGroup></Project>\n")
        self.commit("Initial commit")
        self.write("a.txt", "a\n")
        self.boundary = self.commit("TECHNICAL #1 the published boundary")

    def git(self, *args, cwd=None, check=True):
        return run(cwd or self.priv, "git", *args, env=self.env, check=check)

    def write(self, rel, text, root=None):
        path = (root or self.priv) / rel
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text)

    def commit(self, message, root=None):
        root = root or self.priv
        self.git("add", "-A", cwd=root)
        self.git("commit", "-q", "-m", message, cwd=root)
        return self.git("rev-parse", "HEAD", cwd=root).stdout.strip()

    def publish(self, check=True, dry=False):
        env = {**self.env, "SRC": str(self.priv), "PUBLIC_REMOTE": str(self.pub), "PUBLISH_BOUNDARY": self.boundary,
               "SECRET_PATTERNS_JS": str(SECRETS), "PUBLISH_DRY_RUN": "1" if dry else "0"}
        return run(self.priv, "bash", str(PUBLISH), env=env, check=check)

    def sync(self, sha, check=True):
        env = {**self.env, "SRC": str(self.priv), "PUBLIC_REMOTE": str(self.pub)}
        return run(self.priv, "bash", str(SYNC), sha, "--work-item", "9", env=env, check=check)

    def public_tip(self):
        return self.git("rev-parse", "main", cwd=self.pub).stdout.strip()

    def public_subjects(self, rev="main"):
        return self.git("log", "--topo-order", "--format=%s", rev, cwd=self.pub).stdout.splitlines()

    def release_commit(self, version, extra_path=None):
        """What Release Please does on the public repository: bump the version, write the changelog and the manifest."""
        work = self.tmp / "work"
        if work.exists():
            shutil.rmtree(work)
        self.git("clone", "-q", str(self.pub), str(work), cwd=self.tmp)
        props = (work / PROPS).read_text().replace("<Version>1.0.0</Version>", f"<Version>{version}</Version>")
        (work / PROPS).write_text(props)
        self.write("CHANGELOG.md", f"# Changelog\n\n## {version}\n", root=work)
        self.write(".release-please-manifest.json", f'{{".": "{version}"}}\n', root=work)
        if extra_path:
            self.write(extra_path, "changed on the public side\n", root=work)
        sha = self.commit(f"chore: release dotnet {version}", root=work)
        self.git("push", "-q", "origin", "HEAD:refs/heads/main", cwd=work)
        return sha

    def test_published_history_keeps_its_hashes_and_later_commits_are_mapped(self):
        first = self.publish()
        self.assertIn("published", first.stdout)
        boundary_public = self.public_tip()
        self.assertEqual(self.public_subjects(), ["TECHNICAL #1 the published boundary", "Initial commit"])
        self.assertEqual(self.git("ls-tree", "-r", "--name-only", "main", cwd=self.pub).stdout.split(),
                         ["README.md", "a.txt", PROPS])    # no .claude, no docs

        self.write("b.txt", "b\n")
        self.commit("FEATURE #2 add thing\n\nBREAKING CHANGE: changes the shape")
        self.write("c.txt", "c\n")
        self.commit("FIX #3 repair thing")
        self.write("d.txt", "d\n")
        self.commit("TECHNICAL #4 tidy")
        self.publish()                                               # the bare remote refuses anything but a fast-forward
        self.assertTrue(mirror._is_ancestor(str(self.pub), boundary_public, "main"))   # the first publish's hashes survive
        self.assertEqual(self.public_subjects()[:3], ["chore: tidy (#4)", "fix: repair thing (#3)", "feat: add thing (#2)"])
        self.assertEqual(self.public_subjects()[3:], ["TECHNICAL #1 the published boundary", "Initial commit"])
        self.assertIn("BREAKING CHANGE: changes the shape", self.git("log", "-1", "--format=%B", "main~2", cwd=self.pub).stdout)

    def test_merge_subjects_are_mapped_and_open_no_release(self):
        self.publish()
        self.git("checkout", "-q", "-b", "topic")
        self.write("t.txt", "t\n")
        self.commit("FEATURE #5 topic work")
        self.git("checkout", "-q", "main")
        self.git("merge", "--no-ff", "-q", "-m", "Merge #5: topic work", "topic")
        self.publish()
        subjects = self.public_subjects()
        self.assertEqual(subjects[:2], ["chore: merge topic work (#5)", "feat: topic work (#5)"])
        self.assertIsNone(mirror.release_type(subjects[0]))
        self.assertEqual(mirror.release_type(subjects[1]), "minor")    # the feature commit itself is what releases

    def test_sync_back_round_trip_ends_in_a_fast_forward(self):
        self.publish()
        before = self.public_tip()
        self.write("b.txt", "b\n")
        self.commit("FEATURE #2 add thing")
        release = self.release_commit("1.0.1")

        blocked = self.publish(check=False)                          # the release commit is not in private main yet
        self.assertEqual(blocked.returncode, 1)
        self.assertIn("scripts/sync-release-commit.sh " + release, blocked.stderr)
        self.assertEqual(self.public_tip(), release)                 # nothing was pushed

        synced = self.sync(release)
        self.assertIn("sync release 1.0.1", synced.stdout)
        self.assertEqual(self.git("log", "-1", "--format=%s").stdout.strip(), "TECHNICAL #9 sync release 1.0.1")
        self.assertEqual(self.sync(release).returncode, 0)           # idempotent: already in sync, no second commit
        self.assertEqual(self.git("rev-list", "--count", "HEAD").stdout.strip(), "4")
        self.write("e.txt", "e\n")
        self.commit("FIX #6 after the release")

        dry = self.publish(dry=True)
        self.assertIn("history secret scan: clean", dry.stdout)
        self.assertIn("merge commit over the public tip", dry.stdout)
        self.assertEqual(self.public_tip(), release)                 # a dry run pushes nothing

        self.publish()
        tip = self.public_tip()
        parents = self.git("rev-list", "--parents", "-n", "1", tip, cwd=self.pub).stdout.split()[1:]
        self.assertEqual(len(parents), 2)
        self.assertEqual(parents[0], release)                        # fast-forward over the public tip
        self.assertTrue(mirror._is_ancestor(str(self.pub), before, tip))
        self.assertEqual(self.git("rev-parse", f"{tip}^{{tree}}", cwd=self.pub).stdout,
                         self.git("rev-parse", f"{parents[1]}^{{tree}}", cwd=self.pub).stdout)   # the filtered head's tree
        self.assertEqual(self.git("show", f"{tip}:CHANGELOG.md", cwd=self.pub).stdout, "# Changelog\n\n## 1.0.1\n")
        self.assertIn("<Version>1.0.1</Version>", self.git("show", f"{tip}:{PROPS}", cwd=self.pub).stdout)
        self.assertEqual(self.public_subjects()[0], "chore: merge the private history into the public mirror")
        self.assertIsNone(mirror.release_type(self.public_subjects()[0]))

        # Our own merge is accepted on the next round, with new private work and with none.
        again = self.publish()
        self.assertIn("nothing to publish", again.stdout)
        self.write("f.txt", "f\n")
        self.commit("FEATURE #7 next")
        self.publish()
        self.assertIn("feat: next (#7)", self.public_subjects())
        self.assertEqual(self.public_subjects()[0], "chore: merge the private history into the public mirror")

    def test_a_foreign_public_change_is_refused(self):
        self.publish()
        self.write("b.txt", "b\n")
        self.commit("FEATURE #2 add thing")
        foreign = self.release_commit("1.0.1", extra_path="README.md")
        blocked = self.publish(check=False)
        self.assertEqual(blocked.returncode, 1)
        self.assertIn(foreign, blocked.stderr)
        self.assertIn("README.md", blocked.stderr)
        self.assertEqual(self.public_tip(), foreign)
        refused = self.sync(foreign, check=False)
        self.assertEqual(refused.returncode, 1)
        self.assertIn("README.md", refused.stderr)

    def test_sync_refuses_a_dirty_tree_and_a_private_edit_it_would_overwrite(self):
        self.publish()
        release = self.release_commit("1.0.1")
        self.write(PROPS, "<Project><PropertyGroup>\n<Version>1.0.0</Version>\n<Other/>\n</PropertyGroup></Project>\n")
        self.assertIn("not clean", self.sync(release, check=False).stderr)
        self.commit("TECHNICAL #8 edit the props in private")
        refused = self.sync(release, check=False)
        self.assertEqual(refused.returncode, 1)
        self.assertIn("merge it by hand", refused.stderr)

    @unittest.skipUnless(TOOLS and PUBLISH.exists() and SECRETS.exists(), "private scripts or tools absent")
    def test_real_boundary_filters_to_the_published_tip(self):
        probe = subprocess.run(["git", "-C", str(ROOT), "cat-file", "-e", f"{REAL_BOUNDARY}^{{commit}}"], capture_output=True)
        if probe.returncode != 0 or subprocess.run(["git", "-C", str(ROOT), "cat-file", "-e", REAL_PUBLIC_TIP],
                                                    capture_output=True).returncode != 0:
            self.skipTest("this checkout lacks the private boundary commit or the public tip object")
        run(ROOT, "git", "push", "-q", str(self.pub), f"{REAL_PUBLIC_TIP}:refs/heads/main", env=self.env)   # into the scratch bare repo
        env = {**self.env, "SRC": str(ROOT), "PUBLIC_REMOTE": str(self.pub), "PUBLISH_DRY_RUN": "1"}
        env.pop("PUBLISH_BOUNDARY", None)
        result = run(ROOT, "bash", str(PUBLISH), env=env)
        self.assertIn(f"boundary {REAL_BOUNDARY} filters to {REAL_PUBLIC_TIP[:7]}", result.stdout)
        self.assertIn("dry run: would push", result.stdout)


if __name__ == "__main__":
    unittest.main()
