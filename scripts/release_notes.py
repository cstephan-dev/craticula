#!/usr/bin/env python3
"""Adds the spec-version line to a release's notes. Pure stdlib, no network.

The release workflow reads the current notes with `gh release view`, runs this script on that file and uploads the
result with `gh release edit --notes-file`. The line is `Spec version X.Y.Z · conformance fixture suite: N fixtures`;
N counts the fixture folders `spec/fixtures/<family>/<case>`. A notes file that already has a line starting with
`Spec version ` is left alone, so a re-run adds nothing.

Usage: python3 scripts/release_notes.py --spec-version-file spec/VERSION --fixtures spec/fixtures --append-to <notes file>
"""
import argparse
import re
import sys
from pathlib import Path

SEMVER = re.compile(r"\d+\.\d+\.\d+")
PREFIX = "Spec version "


def read_spec_version(version_file: Path) -> str:
    text = Path(version_file).read_text(encoding="utf-8").strip()
    if not SEMVER.fullmatch(text):
        raise ValueError(f"{version_file}: found {text!r}, expected a MAJOR.MINOR.PATCH version")
    return text


def count_fixtures(fixtures_dir: Path) -> int:
    root = Path(fixtures_dir)
    count = sum(1 for family in root.iterdir() if family.is_dir() for case in family.iterdir() if case.is_dir())
    if count <= 0:
        raise ValueError(f"{fixtures_dir}: found no fixture folders (expected <family>/<case>)")
    return count


def build_line(version: str, fixtures: int) -> str:
    if not SEMVER.fullmatch(version):
        raise ValueError(f"spec version {version!r} is not MAJOR.MINOR.PATCH")
    if fixtures <= 0:
        raise ValueError(f"fixture count {fixtures} is not a positive integer")
    return f"{PREFIX}{version} · conformance fixture suite: {fixtures} fixtures"


def append_once(notes: str, line: str) -> str:
    """Returns the notes with the line appended after a blank line, or unchanged when a `Spec version ` line exists."""
    if any(ln.startswith(PREFIX) for ln in notes.splitlines()):
        return notes
    body = notes.rstrip("\n")
    return f"{line}\n" if not body else f"{body}\n\n{line}\n"


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("--spec-version-file", required=True, type=Path)
    parser.add_argument("--fixtures", required=True, type=Path)
    parser.add_argument("--append-to", required=True, type=Path)
    args = parser.parse_args(argv)
    try:
        line = build_line(read_spec_version(args.spec_version_file), count_fixtures(args.fixtures))
        notes = args.append_to.read_text(encoding="utf-8") if args.append_to.exists() else ""
        updated = append_once(notes, line)
    except (OSError, ValueError) as exc:
        print(f"release_notes: {exc}", file=sys.stderr)
        return 1
    if updated != notes:
        args.append_to.write_text(updated, encoding="utf-8")
    print(line)
    return 0


if __name__ == "__main__":
    sys.exit(main())
