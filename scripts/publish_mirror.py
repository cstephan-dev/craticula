#!/usr/bin/env python3
"""Logic of the private-to-public mirror (story #1659): commit-subject mapping, sync-back checks, Release Please's rule.

Used by the publish script (the filter's --commit-callback, the sync-back check, the merge commit message) and by the
sync script, both absent from the public mirror; pinned by scripts/test_publish_mirror.py. Stdlib only, no network.

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
# Release Please (googleapis/release-please src/commit.ts splitMessages, read at v17.3.0, the version the pinned action
# bundles) splits one commit message into several entries at every blank line that is followed by `type: `; each entry is
# parsed as a conventional commit of its own. Mirrored here so the merge commit's body can be tested.
_SPLIT = re.compile(r"\r?\n\r?\n(?=(?:feat|fix|docs|style|refactor|perf|test|build|ci|chore|revert)(?:\(.*?\))?: )")
_RANK = {None: 0, "patch": 1, "minor": 2, "major": 3}


def _entry_type(entry: str):
    m = _CONVENTIONAL.match(entry.split("\n", 1)[0])
    if m is None:
        return None
    if _BREAKING.search(entry) or m.group("bang") is not None:
        return "major"
    return {"feat": "minor", "fix": "patch"}.get(m.group("type").lower())


def release_type(message: str):
    """Release Please's rule for one commit message: every entry (see _SPLIT) counts; feat -> minor, fix -> patch,
    `!` or a BREAKING CHANGE footer -> major; the strongest wins. Anything else (chore, docs, merge, non-conventional)
    -> None, which opens no release."""
    best = None
    for entry in _SPLIT.split(message):
        found = _entry_type(entry)
        if _RANK[found] > _RANK[best]:
            best = found
    return best


def _git(repo, *args, check=True):
    result = subprocess.run(["git", "-C", repo, *args], capture_output=True, text=True)
    if check and result.returncode != 0:
        raise RuntimeError(f"git {' '.join(args)} failed: {result.stderr.strip()}")
    return result


def _entry(repo, rev, path):
    """(mode, blob id) of `path` at `rev`, or None when absent."""
    out = _git(repo, "ls-tree", rev, "--", path, check=False).stdout.strip()
    if not out:
        return None
    meta, _, _name = out.partition("\t")
    mode, _kind, blob = meta.split()
    return mode, blob


def _blob(repo, rev, path):
    found = _entry(repo, rev, path)
    return found[1] if found else None


def _is_ancestor(repo, ancestor, descendant):
    return _git(repo, "merge-base", "--is-ancestor", ancestor, descendant, check=False).returncode == 0


EXIT_OK, EXIT_REFUSED, EXIT_SYNC_NEEDED = 0, 1, 3
TRAILER = "Public-Sync"
_FULL_SHA = re.compile(r"^[0-9a-f]{40}$")
MERGE_SUBJECT = "chore: merge the private history into the public mirror"
MERGE_LIST_CAP = 200


def recorded_syncs(repo, head):
    """Full shas of the public commits that private history records as absorbed: the `Public-Sync: <sha>` trailers in
    the history of `head`. The sync script writes one; a public change copied into private by hand carries one too."""
    out = _git(repo, "log", f"--format=%(trailers:key={TRAILER},valueonly)", head).stdout
    return {line.strip() for line in out.splitlines() if _FULL_SHA.match(line.strip())}


def _paths(repo, *args):
    return [p for p in _git(repo, "diff-tree", "-r", "--name-only", "--root", "--no-commit-id", *args).stdout.split("\n") if p]


def check_extra(repo, public, head):
    """Sync-back gate of the publish, run on the filtered clone. The commits on the public side that the filtered head
    does not contain are examined oldest first:
      - our own publish merge (tree equal to its second parent's, which the head contains) and a merge whose diff
        against its first parent touches only ALLOWED_PATHS are accepted (the commits they bring are examined themselves);
      - a commit whose full sha private history records in a `Public-Sync:` trailer is accepted, whatever it touched;
      - any other commit touching a path outside ALLOWED_PATHS is refused, naming commit and path;
      - any other commit touching only ALLOWED_PATHS has to be synced first (EXIT_SYNC_NEEDED, the oldest one is named).
    Nothing compares file contents with the head's: after the first release the head legitimately moves on.
    Returns (exit code, message, sha to sync from)."""
    extra = _git(repo, "rev-list", "--topo-order", "--reverse", f"{head}..{public}").stdout.split()
    recorded = recorded_syncs(repo, head)
    pending = None
    for sha in extra:
        parents = _git(repo, "rev-list", "--parents", "-n", "1", sha).stdout.split()[1:]
        if sha in recorded:
            continue
        if len(parents) > 1:
            own = (_git(repo, "rev-parse", f"{sha}^{{tree}}").stdout == _git(repo, "rev-parse", f"{parents[-1]}^{{tree}}").stdout
                   and _is_ancestor(repo, parents[-1], head))
            outside = [p for p in _paths(repo, parents[0], sha) if p not in ALLOWED_PATHS]
            if not own and outside:
                return EXIT_REFUSED, f"public commit {sha} is a merge this publish did not make and it touches {outside[0]} (only {', '.join(ALLOWED_PATHS)} may change on the public side)", None
            continue
        for path in _paths(repo, sha):
            if path not in ALLOWED_PATHS:
                return EXIT_REFUSED, (f"public commit {sha} touches {path}; only {', '.join(ALLOWED_PATHS)} may change on the public side "
                                      f"(copy the change into private main with a '{TRAILER}: {sha}' trailer in its commit message)"), None
        if pending is None:
            pending = sha
    if pending:
        return EXIT_SYNC_NEEDED, (
            f"public commit {pending} is not recorded in private main (no '{TRAILER}: {pending}' trailer in its history): "
            f"from a work branch run scripts/sync-release-commit.sh {pending} --work-item <id>, merge that to main, then publish again"), pending
    return EXIT_OK, "", None


_VERSION_ELEMENT = re.compile(r"<Version>([^<]*)</Version>")
_VERSION_VALUE = re.compile(r"^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$")


def _versions(text, where):
    found = _VERSION_ELEMENT.findall(text)
    if len(found) != 1:
        raise RuntimeError(f"{where} must hold exactly one <Version>, found {len(found)}")
    value = found[0].strip()
    if not _VERSION_VALUE.match(value):
        raise RuntimeError(f"{where} holds the <Version> value {value!r}, which is not a plain semantic version")
    return value


def read_version(repo):
    with open(os.path.join(repo, PROPS_PATH), encoding="utf-8") as handle:
        return _versions(handle.read(), PROPS_PATH)


def _cat(repo, blob):
    return subprocess.run(["git", "-C", repo, "cat-file", "blob", blob], capture_output=True, check=True).stdout


def _first_difference(old, new):
    old_lines, new_lines = old.splitlines(), new.splitlines()
    for number in range(max(len(old_lines), len(new_lines))):
        a = old_lines[number] if number < len(old_lines) else "<end of file>"
        b = new_lines[number] if number < len(new_lines) else "<end of file>"
        if a != b:
            return f"line {number + 1}: parent has {a.strip()!r}, public commit has {b.strip()!r}"
    return "line endings differ"


def _read_private(repo, path):
    full = os.path.join(repo, path)
    if os.path.islink(full):
        raise RuntimeError(f"{path} is a symbolic link in the private working tree; refusing to write through it")
    if not os.path.exists(full):
        return None
    with open(full, "rb") as handle:
        return handle.read()


def _write(repo, path, data):
    full = os.path.join(repo, path)
    os.makedirs(os.path.dirname(full) or ".", exist_ok=True)
    with open(full, "wb") as handle:
        handle.write(data)


def apply_release(repo, sha):
    """Bring the public release commit `sha` into the working tree of `repo`; return the paths changed.

    Only the <Version> value is taken from the public props file (the private file is edited in place, never replaced):
    the public commit's change to that file must be that value and nothing else, or the sync refuses and names the
    difference. CHANGELOG.md and the manifest are copied whole; both must be regular files without a NUL byte. Refuses a
    merge commit, a commit touching another path, and a private file that moved on since the commit's parent (a silent
    overwrite of a private edit is worse than a manual merge)."""
    parents = _git(repo, "rev-list", "--parents", "-n", "1", sha).stdout.split()[1:]
    if len(parents) != 1:
        raise RuntimeError(f"{sha} must be a release commit with one parent, it has {len(parents)} (sync the single-parent release commit instead)")
    base = parents[0]
    for path in _paths(repo, sha):
        if path not in ALLOWED_PATHS:
            raise RuntimeError(f"{sha} touches {path}; only {', '.join(ALLOWED_PATHS)} may change on the public side")
    writes = []

    # The version value, in the private props file.
    pub_blob, old_blob = _blob(repo, sha, PROPS_PATH), _blob(repo, base, PROPS_PATH)
    if pub_blob != old_blob:
        if pub_blob is None or old_blob is None:
            raise RuntimeError(f"{PROPS_PATH} is added or deleted by {sha}; only its <Version> value may change")
        pub_text, old_text = _cat(repo, pub_blob).decode("utf-8"), _cat(repo, old_blob).decode("utf-8")
        new_version, old_version = _versions(pub_text, f"{PROPS_PATH} at {sha}"), _versions(old_text, f"{PROPS_PATH} at {base}")
        masked_pub, masked_old = _VERSION_ELEMENT.sub("<Version>@</Version>", pub_text), _VERSION_ELEMENT.sub("<Version>@</Version>", old_text)
        if masked_pub != masked_old:
            raise RuntimeError(f"{sha} changes {PROPS_PATH} beyond the <Version> value ({_first_difference(masked_old, masked_pub)}); merge it by hand")
        current = _read_private(repo, PROPS_PATH)
        if current is None:
            raise RuntimeError(f"{PROPS_PATH} is missing in the private working tree")
        private_text = current.decode("utf-8")
        private_version = _versions(private_text, PROPS_PATH)
        if private_version != new_version:
            if private_version != old_version:
                raise RuntimeError(f"<Version> in {PROPS_PATH} was edited in private main ({private_version}) since the public commit's parent ({old_version}); merge it by hand")
            writes.append((PROPS_PATH, _VERSION_ELEMENT.sub(lambda _m: f"<Version>{new_version}</Version>", private_text, count=1).encode("utf-8")))

    # The two plain-text files, copied whole.
    for path in ALLOWED_PATHS[1:]:
        entry = _entry(repo, sha, path)
        if entry is None:
            if _entry(repo, base, path) is None:
                continue
            raise RuntimeError(f"{sha} deletes {path}; refusing")
        mode, target = entry
        if mode not in ("100644", "100755"):
            raise RuntimeError(f"{path} at {sha} is not a regular file (mode {mode}); refusing")
        data = _cat(repo, target)
        if b"\x00" in data:
            raise RuntimeError(f"{path} at {sha} contains a NUL byte; refusing")
        current = _read_private(repo, path)
        if current == data:
            continue
        have = None if current is None else subprocess.run(
            ["git", "-C", repo, "hash-object", "--stdin"], input=current, capture_output=True, check=True).stdout.decode().strip()
        if have != _blob(repo, base, path):
            raise RuntimeError(f"{path} was edited in private main since the public commit's parent; merge it by hand")
        writes.append((path, data))
    for path, data in writes:      # every check passed: nothing is written before this point
        _write(repo, path, data)
    return [path for path, _data in writes]


_TYPE_RANK = {"feat": 1, "fix": 2, "perf": 3}


def merge_message(repo, public, head, cap=MERGE_LIST_CAP):
    """Message of the mirror's merge commit (parents: public tip, filtered head).

    Release Please reads the merge commit, dated at publish time, and walks history by date until the last release, so
    the commits this merge brings in that are dated before the release commit are never counted by themselves.
    Release Please splits one message into entries at each blank line followed by `type: ` (see _SPLIT), so the body
    lists the mapped subject of every non-merge commit PUBLIC_TIP..HEAD as its own paragraph (a BREAKING CHANGE footer
    stays with its commit), strongest first, at most `cap`, then one count line. Commits dated after the newest public-only commit are left out:
    Release Please counts those by themselves, listing them would double them in the changelog.
    The subject stays a `chore:` so the merge itself counts for nothing."""
    cutoff = _git(repo, "log", "--no-merges", "-1", "--format=%ct", f"{head}..{public}").stdout.strip()
    raw = _git(repo, "log", "--no-merges", "--format=%x01%ct%x00%s%x00%b", f"{public}..{head}").stdout
    entries = []
    for chunk in raw.split("\x01")[1:]:
        stamp, subject, body = chunk.split("\x00", 2)
        if cutoff and int(stamp) > int(cutoff):
            continue
        m = _CONVENTIONAL.match(subject)
        if m is None or not _SPLIT.match("\n\n" + subject):
            if m is None or m.group("bang") is None:
                continue
        footers = [line for line in body.splitlines() if _BREAKING.match(line)]
        subject = subject.strip()
        if m.group("bang") is not None:
            # Release Please's split pattern has no `!`: `feat!: x` after a blank line would stay inside the previous entry.
            # State the break as a footer on a plain `feat: x` instead.
            subject = re.sub(r"^([A-Za-z]+(?:\([^)]*\))?)!: ", r"\1: ", subject)
            if not footers:
                footers = [f"BREAKING CHANGE: {subject.split(': ', 1)[1]}"]
        breaking = bool(footers)
        entries.append((0 if breaking else _TYPE_RANK.get(m.group("type").lower(), 4), "\n\n".join([subject, *footers])))
    entries.sort(key=lambda item: item[0])     # stable: strongest type first, newest first within a type
    paragraphs = [text for _rank, text in entries[:cap]]
    if len(entries) > cap:
        paragraphs.append(f"... and {len(entries) - cap} more commits not listed")
    return "\n\n".join([MERGE_SUBJECT, *paragraphs]) + "\n"


def main(argv):
    command = argv[1] if len(argv) > 1 else ""
    try:
        if command == "check-extra" and len(argv) == 5:
            code, message, _sha = check_extra(argv[2], argv[3], argv[4])
            if message:
                print(("\u26d4 " if code == EXIT_REFUSED else "") + message, file=sys.stderr)
            return code
        if command == "apply-release" and len(argv) == 4:
            for path in apply_release(argv[2], argv[3]):
                print(path)
            return 0
        if command == "version" and len(argv) == 3:
            print(read_version(argv[2]))
            return 0
        if command == "merge-message" and len(argv) == 5:
            sys.stdout.write(merge_message(argv[2], argv[3], argv[4]))
            return 0
        if command == "is-recorded" and len(argv) == 5:
            return 0 if argv[4] in recorded_syncs(argv[2], argv[3]) else 1
    except RuntimeError as error:
        print(f"\u26d4 {error}", file=sys.stderr)
        return EXIT_REFUSED
    print("usage: publish_mirror.py check-extra <repo> <public> <head> | apply-release <repo> <sha> | version <repo> | "
          "merge-message <repo> <public> <head> | is-recorded <repo> <head> <sha>", file=sys.stderr)
    return 2


if __name__ == "__main__":
    sys.exit(main(sys.argv))
