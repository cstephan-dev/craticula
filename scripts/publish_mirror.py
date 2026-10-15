#!/usr/bin/env python3
"""Logic of the private-to-public mirror (story #1659): commit-subject mapping, sync-back checks, Release Please's rule.

Used by scripts/publish-github.sh (the filter's --commit-callback and the sync-back check) and by
scripts/sync-release-commit.sh; pinned by scripts/test_publish_mirror.py. Stdlib only, no network.

The mapping is part of the public history's identity: once a mapped commit is published, its hash depends on this
function, so a change to `map_message` rewrites every public commit after the published boundary and the non-force
push starts failing. Change it only with a new boundary.
"""
import functools
import os
import re
import subprocess
import sys

# The only paths a public (Release Please) commit may touch; they are brought back to private main by
# scripts/sync-release-commit.sh. Keep in step with release-please-config.json (task #2407).
ALLOWED_PATHS = ("csharp/src/Directory.Build.props", "CHANGELOG.md", ".release-please-manifest.json")
PROPS_PATH = ALLOWED_PATHS[0]

_TYPES = {b"FEATURE": b"feat", b"FIX": b"fix", b"TECHNICAL": b"chore"}
_SUBJECT = re.compile(rb"^(FEATURE|FIX|TECHNICAL) #(\d+) (\S.*)$")
_MERGE = re.compile(rb"^Merge #(\d+): (\S.*)$")


def map_message(message: bytes) -> bytes:
    """`FEATURE|FIX|TECHNICAL #<id> text` -> `feat|fix|chore: text (#<id>)`; `Merge #<id>: text` -> `chore: merge text (#<id>)`.

    Only the first line changes: the body and the footers (BREAKING CHANGE: ...) pass through. A subject that matches
    neither shape is returned unchanged.
    """
    subject, sep, rest = message.partition(b"\n")
    text = subject.rstrip()
    m = _SUBJECT.match(text)
    if m:
        return b"%s: %s (#%s)" % (_TYPES[m.group(1)], m.group(3), m.group(2)) + sep + rest
    m = _MERGE.match(text)
    if m:
        return b"chore: merge %s (#%s)" % (m.group(2), m.group(1)) + sep + rest
    return message


@functools.lru_cache(maxsize=None)
def _frozen(path: str) -> frozenset:
    with open(path, "rb") as handle:
        return frozenset(line.strip() for line in handle if line.strip())


def map_unless_frozen(original_id: bytes, message: bytes, frozen_file: str) -> bytes:
    """The filter's callback body: commits at or before the published boundary keep their message byte for byte."""
    return message if original_id in _frozen(frozen_file) else map_message(message)


_CONVENTIONAL = re.compile(r"^(?P<type>[A-Za-z]+)(\([^)]*\))?(?P<bang>!)?: \S")
_BREAKING = re.compile(r"^BREAKING[ -]CHANGE: ", re.MULTILINE)


def release_type(message: str):
    """Release Please's rule for one commit: feat -> minor, fix -> patch, `!` or a BREAKING CHANGE footer -> major,
    anything else (chore, docs, merge, non-conventional) -> None, which opens no release."""
    m = _CONVENTIONAL.match(message.split("\n", 1)[0])
    breaking = bool(_BREAKING.search(message)) or (m is not None and m.group("bang") is not None)
    if m is None:
        return None
    if breaking:
        return "major"
    return {"feat": "minor", "fix": "patch"}.get(m.group("type").lower())


def _git(repo, *args, check=True):
    result = subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True)
    if check and result.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result


def _blob(repo, rev, path):
    result = _git(repo, "rev-parse", "-q", "--verify", f"{rev}:{path}", check=False)
    return result.stdout.strip() if result.returncode == 0 else None


def _is_ancestor(repo, ancestor, descendant):
    return _git(repo, "merge-base", "--is-ancestor", ancestor, descendant, check=False).returncode == 0


EXIT_OK, EXIT_REFUSED, EXIT_SYNC_NEEDED = 0, 1, 3


def check_extra(repo, public, head):
    """Sync-back gate of the publish. The commits on the public side that the filtered head does not contain must touch
    only ALLOWED_PATHS (our own publish merges are accepted); then private main must already carry the public tip's
    state of those paths. Returns (exit code, message, sha to sync from)."""
    extra = _git(repo, "rev-list", f"{head}..{public}").stdout.split()
    newest_release = None
    for sha in extra:
        parents = _git(repo, "rev-list", "--parents", "-n", "1", sha).stdout.split()[1:]
        if len(parents) > 1:
            own = (_git(repo, "rev-parse", f"{sha}^{{tree}}").stdout == _git(repo, "rev-parse", f"{parents[-1]}^{{tree}}").stdout
                   and _is_ancestor(repo, parents[-1], head))
            if not own:
                return EXIT_REFUSED, f"public commit {sha} is a merge this publish did not make (path: <merge>)", None
            continue
        paths = _git(repo, "diff-tree", "-r", "--name-only", "--root", "--no-commit-id", sha).stdout.split("\n")
        for path in (p for p in paths if p):
            if path not in ALLOWED_PATHS:
                return EXIT_REFUSED, f"public commit {sha} touches {path}; only {', '.join(ALLOWED_PATHS)} may change on the public side", None
        if newest_release is None:
            newest_release = sha
    stale = [p for p in ALLOWED_PATHS if _blob(repo, head, p) != _blob(repo, public, p)]
    if stale:
        if newest_release is None:
            return EXIT_REFUSED, f"public tip differs from the filtered head in {', '.join(stale)} with no release commit to sync", None
        return EXIT_SYNC_NEEDED, (
            f"the public release commit {newest_release} is not in private main yet ({', '.join(stale)} differ): "
            f"run scripts/sync-release-commit.sh {newest_release} --work-item <id>, merge it to main, then publish again"), newest_release
    return EXIT_OK, "", None


def read_version(repo):
    path = os.path.join(repo, PROPS_PATH)
    with open(path, encoding="utf-8") as handle:
        found = re.findall(r"<Version>([^<]+)</Version>", handle.read())
    if len(found) != 1:
        raise RuntimeError(f"{PROPS_PATH} must hold exactly one <Version>, found {len(found)}")
    return found[0].strip()


def apply_release(repo, sha):
    """Write the public state of ALLOWED_PATHS at `sha` into the working tree of `repo`; return the paths changed.

    Refuses a merge commit, a commit touching another path, and a private working copy that moved on since the commit's
    parent (a silent overwrite of a private edit is worse than a manual merge)."""
    parents = _git(repo, "rev-list", "--parents", "-n", "1", sha).stdout.split()[1:]
    if len(parents) != 1:
        raise RuntimeError(f"{sha} must be a release commit with one parent, it has {len(parents)}")
    for path in (p for p in _git(repo, "diff-tree", "-r", "--name-only", "--no-commit-id", sha).stdout.split("\n") if p):
        if path not in ALLOWED_PATHS:
            raise RuntimeError(f"{sha} touches {path}; only {', '.join(ALLOWED_PATHS)} may change on the public side")
    changed = []
    for path in ALLOWED_PATHS:
        target = _blob(repo, sha, path)
        full = os.path.join(repo, path)
        current = None
        if os.path.exists(full):
            current = _git(repo, "hash-object", "--", full).stdout.strip()
        if current == target:
            continue
        if current != _blob(repo, parents[0], path):
            raise RuntimeError(f"{path} was edited in private main since the public commit's parent; merge it by hand")
        if target is None:
            os.remove(full)
        else:
            os.makedirs(os.path.dirname(full), exist_ok=True)
            with open(full, "wb") as handle:
                handle.write(subprocess.run(["git", "-C", repo, "cat-file", "blob", target], capture_output=True, check=True).stdout)
        changed.append(path)
    return changed


def main(argv):
    command = argv[1] if len(argv) > 1 else ""
    try:
        if command == "check-extra" and len(argv) == 5:
            code, message, sha = check_extra(argv[2], argv[3], argv[4])
            if message:
                print(("⛔ " if code == EXIT_REFUSED else "") + message, file=sys.stderr)
            return code
        if command == "apply-release" and len(argv) == 4:
            for path in apply_release(argv[2], argv[3]):
                print(path)
            return 0
        if command == "version" and len(argv) == 3:
            print(read_version(argv[2]))
            return 0
    except RuntimeError as error:
        print(f"⛔ {error}", file=sys.stderr)
        return EXIT_REFUSED
    print("usage: publish_mirror.py check-extra <repo> <public> <head> | apply-release <repo> <sha> | version <repo>", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
