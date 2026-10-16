#!/usr/bin/env python3
"""Oracle check for spec/fixtures/community-infinite (AG-7).

oracle.json maps each case to the existing fixture whose IR it equals (or null for a hand-derived case). For every mapped case
the expected artifacts, the schema, the dataset and the expected rows must be byte-identical to the source's, and the request
must equal the source's after `canonical()` (see there for what is ignored: the group/value/pivot/context fields, a sort item's
`type`, `filterModel` null vs `{}`, a midnight time on a date, `condition1/2` vs `conditions[]`). A folder without an oracle.json entry
(or the reverse) is reported too. Exit 0 = every mapped case equals its oracle, 1 = a difference, 2 = a case is missing.
"""
import json
import pathlib
import re
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent / "spec" / "fixtures"
CATEGORY = ROOT / "community-infinite"
DROPPED = {"rowGroupCols", "valueCols", "pivotCols", "pivotMode", "groupKeys", "requestContext"}
FILES = ("schema.json", "dataset.json", "expectedRows.json")


def canonical(value, top=True):
    """A request in a form that ignores what the parser ignores and what AG Grid spells two ways.

    Dropped: the group/value/pivot/context fields, a sort item's `type` (AG Grid 35+), `filterModel: null` vs `{}`, a midnight
    time on a date value ('YYYY-MM-DD 00:00:00' vs 'YYYY-MM-DD'), and the legacy `condition1`/`condition2` pair vs `conditions[]`.
    """
    if isinstance(value, dict):
        if "condition1" in value and "condition2" in value:
            value = {k: v for k, v in value.items() if k not in ("condition1", "condition2")} | {
                "conditions": [value["condition1"], value["condition2"]]}
        out = {}
        for key, item in value.items():
            if top and key in DROPPED:
                continue
            if key in ("dateFrom", "dateTo") and isinstance(item, str):
                item = re.sub(r" 00:00:00$", "", item)
            out[key] = canonical(item, False)
        if top and out.get("filterModel") is None:
            out["filterModel"] = {}
        if not top and "sort" in out and "colId" in out:
            out.pop("type", None)
        return out
    if isinstance(value, list):
        return [canonical(v, False) for v in value]
    return value


def check(mapping):
    on_disk = {p.name for p in CATEGORY.iterdir() if p.is_dir()}
    problems = [(2, f"{name}: folder has no oracle.json entry") for name in sorted(on_disk - set(mapping))]
    problems += [(2, f"{name}: oracle.json entry has no folder") for name in sorted(set(mapping) - on_disk)]
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
        if canonical(json.loads((mine / "agGridRequest.json").read_text())) != canonical(json.loads((theirs / "agGridRequest.json").read_text())):
            problems.append((1, f"{case}: agGridRequest.json is not {source}'s request minus the fields Community does not send"))
    return problems


def main():
    mapping = json.loads((CATEGORY / "oracle.json").read_text())
    problems = check(mapping)
    for _, message in problems:
        print("FAIL", message)
    if problems:
        return max(code for code, _ in problems)
    print(f"OK, {sum(1 for s in mapping.values() if s)} cases equal their oracle, {sum(1 for s in mapping.values() if s is None)} hand-derived")
    return 0


if __name__ == "__main__":
    sys.exit(main())
