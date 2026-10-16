#!/usr/bin/env python3
"""Oracle check for spec/fixtures/community-infinite (AG-6).

oracle.json maps each case to the existing fixture whose IR it equals (or null for a hand-derived case). For every mapped case
the expected artifacts, the schema, the dataset and the expected rows must be byte-identical to the source's, and the request
must differ from the source's only by the group/value/pivot/context fields a Community request does not send and by the
zone-less date spelling. Exit 0 = every mapped case equals its oracle, 1 = a difference, 2 = a case is missing.
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent / "spec" / "fixtures"
CATEGORY = ROOT / "community-infinite"
DROPPED = {"rowGroupCols", "valueCols", "pivotCols", "pivotMode", "groupKeys", "requestContext"}
FILES = ("schema.json", "dataset.json", "expectedRows.json")


def normalise(value):
    """The source request with Community's date spelling ('YYYY-MM-DD' -> 'YYYY-MM-DD 00:00:00')."""
    if isinstance(value, dict):
        return {k: (v + " 00:00:00" if k in ("dateFrom", "dateTo") and isinstance(v, str) and re.fullmatch(r"\d{4}-\d\d-\d\d", v)
                    else normalise(v)) for k, v in value.items()}
    if isinstance(value, list):
        return [normalise(v) for v in value]
    return value


def check(mapping):
    problems = []
    for case, source in sorted(mapping.items()):
        if source is None:
            continue
        mine, theirs = CATEGORY / case, ROOT / source
        if not mine.is_dir() or not theirs.is_dir():
            problems.append((2, f"{case}: case or source folder missing ({source})"))
            continue
        for name in FILES:
            if (mine / name).read_bytes() != (theirs / name).read_bytes():
                problems.append((1, f"{case}: {name} differs from {source}"))
        for artifact in sorted((theirs / "expected").glob("*.artifact.json")):
            other = mine / "expected" / artifact.name
            if not other.exists() or other.read_bytes() != artifact.read_bytes():
                problems.append((1, f"{case}: expected/{artifact.name} differs from {source}"))
        request = json.loads((mine / "agGridRequest.json").read_text())
        expected = {k: v for k, v in normalise(json.loads((theirs / "agGridRequest.json").read_text())).items() if k not in DROPPED}
        if request != expected:
            problems.append((1, f"{case}: agGridRequest.json is not {source}'s request minus the fields Community does not send"))
    return problems


def main():
    mapping = json.loads((CATEGORY / "oracle.json").read_text())
    on_disk = {p.name for p in CATEGORY.iterdir() if p.is_dir()}
    problems = [(2, f"{name}: folder has no oracle.json entry") for name in sorted(on_disk - set(mapping))]
    problems += [(2, f"{name}: oracle.json entry has no folder") for name in sorted(set(mapping) - on_disk)]
    problems += check(mapping)
    for _, message in problems:
        print("FAIL", message)
    if problems:
        return max(code for code, _ in problems)
    print(f"OK, {sum(1 for s in mapping.values() if s)} cases equal their oracle, {sum(1 for s in mapping.values() if s is None)} hand-derived")
    return 0


if __name__ == "__main__":
    sys.exit(main())
