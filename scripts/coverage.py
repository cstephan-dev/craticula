#!/usr/bin/env python3
"""Coverage gate: merges the cobertura files of every test project per assembly and enforces a
line-coverage threshold on the six compile-only packages (story #1657).

Usage: coverage.py <results_dir> [--threshold N]      (stdlib only, Python 3.10+)

Why merge: each test project collects coverage for the assemblies it loads, and a package is
exercised by several projects (Core.Tests alone covers Core at about 83 %, the union at 93 %).
Each test project writes its report relative to its own <source> root (the repo folder, src/, or the
package folder), so the same file appears under different relative names. Lines are keyed by the file's
full path, the report's <source> joined with its filename and normalised (an absolute filename is kept as
it is), plus the line number: the same file always merges, two files that only share a name never do.
A line is covered when any report gives it hits > 0. Branches are merged the same way, keeping the best
covered/total ratio per (file, line); they are reported, never enforced.

Exit codes: 0 every in-scope assembly at or above the threshold; 1 at least one below it;
2 usage or environment error (no cobertura file, an in-scope assembly missing from the data).
"""
from __future__ import annotations

import argparse
import posixpath
import re
import sys
import xml.etree.ElementTree as ET
from fractions import Fraction
from pathlib import Path

IN_SCOPE = (
    "Craticula.Core",
    "Craticula.AgGrid",
    "Craticula.Sql",
    "Craticula.Mongo",
    "Craticula.Search",
    "Craticula.Export.Csv",
)

_CONDITION = re.compile(r"\((\d+)/(\d+)\)")


def _is_absolute(path: str) -> bool:
    return path.startswith("/") or re.match(r"^[A-Za-z]:/", path) is not None


def file_key(sources: list[str], filename: str) -> str:
    """The file's full path: <source> + filename, separators unified and normalised. Coverlet writes one
    <source> per report; with several, the first is used (the path only has to be the same across reports
    for the same file and different for different files)."""
    name = filename.replace("\\", "/")
    if not _is_absolute(name) and sources:
        name = sources[0].replace("\\", "/").rstrip("/") + "/" + name
    return posixpath.normpath(name)


def merge(files: list[Path]) -> tuple[dict, dict]:
    """Returns (lines, branches): per in-scope assembly, {(file, line): hits} and
    {(file, line): (covered, total)}, file being file_key()."""
    lines: dict[str, dict[tuple[str, int], int]] = {}
    branches: dict[str, dict[tuple[str, int], tuple[int, int]]] = {}
    for path in files:
        try:
            root = ET.parse(path).getroot()
        except ET.ParseError as error:
            raise ValueError(f"{path}: not valid XML ({error})") from error
        sources = [(source.text or "").strip() for source in root.iter("source") if (source.text or "").strip()]
        for package in root.iter("package"):
            assembly = package.get("name")
            if assembly not in IN_SCOPE:
                continue
            line_map = lines.setdefault(assembly, {})
            branch_map = branches.setdefault(assembly, {})
            for cls in package.iter("class"):
                name = file_key(sources, cls.get("filename", ""))
                for line in cls.iter("line"):
                    key = (name, int(line.get("number")))
                    line_map[key] = max(line_map.get(key, 0), int(line.get("hits", "0")))
                    match = _CONDITION.search(line.get("condition-coverage", ""))
                    if match:
                        covered, total = int(match.group(1)), int(match.group(2))
                        best = branch_map.get(key)
                        if best is None or Fraction(covered, total) > Fraction(*best):
                            branch_map[key] = (covered, total)
    return lines, branches


def percent(covered: int, total: int) -> str:
    return "n/a" if total == 0 else f"{100 * covered / total:.1f}%"


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("results_dir", type=Path, help="folder holding the coverage.cobertura.xml files (searched recursively)")
    parser.add_argument("--threshold", type=float, default=90.0, help="minimum line coverage per assembly, in percent (default 90)")
    args = parser.parse_args()

    if not 0 <= args.threshold <= 100:
        print("coverage.py: --threshold must be between 0 and 100", file=sys.stderr)
        return 2
    files = sorted(args.results_dir.rglob("coverage.cobertura.xml")) if args.results_dir.is_dir() else []
    if not files:
        print(f"coverage.py: no coverage.cobertura.xml under {args.results_dir}", file=sys.stderr)
        return 2
    try:
        lines, branches = merge(files)
    except ValueError as error:
        print(f"coverage.py: {error}", file=sys.stderr)
        return 2
    missing = [a for a in IN_SCOPE if not lines.get(a)]
    if missing:
        print(f"coverage.py: in-scope assembly missing from the coverage data: {', '.join(missing)}", file=sys.stderr)
        return 2

    # Exact comparison on integers: covered * 100 >= threshold * total, threshold as a Fraction of its decimal text.
    threshold = Fraction(str(args.threshold))
    width = max(len(a) for a in IN_SCOPE)
    print(f"Coverage per assembly ({len(files)} cobertura files merged; line threshold {args.threshold:g}%)")
    print(f"{'Assembly':<{width}}  {'Lines':>13}  {'Line %':>7}  {'Branch %':>8}  Status")
    failing = []
    for assembly in IN_SCOPE:
        total = len(lines[assembly])
        covered = sum(1 for hits in lines[assembly].values() if hits > 0)
        b_covered = sum(c for c, _ in branches[assembly].values())
        b_total = sum(t for _, t in branches[assembly].values())
        ok = covered * 100 >= threshold * total
        if not ok:
            failing.append(assembly)
        print(
            f"{assembly:<{width}}  {f'{covered}/{total}':>13}  {percent(covered, total):>7}  "
            f"{percent(b_covered, b_total):>8}  {'PASS' if ok else 'FAIL'}"
        )
    if failing:
        print(f"FAIL: below {args.threshold:g}% lines: {', '.join(failing)}")
        return 1
    print(f"PASS: all {len(IN_SCOPE)} assemblies at or above {args.threshold:g}% lines")
    return 0


if __name__ == "__main__":
    sys.exit(main())
