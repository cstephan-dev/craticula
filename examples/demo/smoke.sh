#!/usr/bin/env bash
# Demo smoke test: sends the same fixed requests (smoke/*.json) to the running demo API for PostgreSQL, MongoDB, the Entity Framework Core provider
# (ef, on PostgreSQL) and SQLite and checks that every provider answers the same as the postgres baseline. It starts nothing; run the quickstart in README.md first.
# Needs only bash, curl and python3 (3.8+).
#
#   bash smoke.sh [--api URL]        default URL: http://127.0.0.1:5080
#   DEMO_SMOKE_PROVIDERS=postgres,mongo bash smoke.sh    compare only the named providers (default: every provider /api/health lists)
#
# Exit 0: every scenario is equal on all providers and every hostile request got a 400 with an error kind.
#      1: a difference, or a missing 400.
#      2: usage or environment problem (no curl/python3, API unreachable, a provider missing, a database down).
#
# Providers: by default every provider /api/health lists, postgres first (the baseline), the others in the order the API reports them.
# DEMO_SMOKE_PROVIDERS (comma separated) names the list instead; postgres is always included and always first. The health gate
# requires postgres and every provider in the list: one that /api/health does not list ends the run with exit 2.
#
# Every other provider is compared with postgres, the baseline. Equality is exact (numbers by value: 12877.0 equals 12877), except avg: PostgreSQL averages decimals with 16 digits and MongoDB with 28, so keys ending in
# "_avg" are compared with a relative tolerance of 1e-6. The divergences are data: the RULES table at the top of the Python block
# lists them per provider. Today it has one entry: for sqlite only, keys ending "_sum" compare with a relative tolerance of 1e-9
# (|a-b| <= 1e-9 * max(1, |a|, |b|)), because SQLite stores NUMERIC as floating point and a sum can differ in the last digits.
# When that tolerance was needed, one line after the table lists where. CSV exports are compared line by line, cell by cell, in full,
# after line endings are normalised: header and every data row must be identical on every provider, with the same
# by-value rule for plain decimals (PostgreSQL writes an amount as 20568.00, MongoDB as 20568).
#
# Test-only: DEMO_SMOKE_SABOTAGE changes one value on the mongo side (no other provider is touched) before comparing, to prove the script can fail
# (it must then exit 1). Never set it otherwise. When mongo is not in the provider list nothing could be sabotaged, so the run ends with exit 2.
#   DEMO_SMOKE_SABOTAGE=1     the first data row of the second rows scenario
#   DEMO_SMOKE_SABOTAGE=csv   the first data row of the first CSV export
set -euo pipefail

api="http://127.0.0.1:5080"
while [ $# -gt 0 ]; do
  case "$1" in
    --api) [ $# -ge 2 ] || { echo "smoke.sh: --api needs a URL" >&2; exit 2; }; api="${2%/}"; shift 2 ;;
    -h|--help) sed -n '2,30p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
    *) echo "smoke.sh: unknown argument '$1' (usage: bash smoke.sh [--api URL])" >&2; exit 2 ;;
  esac
done

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
for tool in curl python3; do
  command -v "$tool" >/dev/null 2>&1 || { echo "smoke.sh: $tool is required on PATH" >&2; exit 2; }
done

work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

health="$work/health.json"
if ! curl -sSf --max-time 5 -o "$health" "$api/api/health" 2>"$work/health.err"; then
  echo "smoke.sh: the API does not answer at $api/api/health ($(tr -d '\n' <"$work/health.err"))." >&2
  echo "  Start it first: dotnet run --project Api (see README.md)." >&2
  exit 2
fi
# postgres first: it is the baseline every other provider is compared with.
# The list is the named one (DEMO_SMOKE_PROVIDERS) or what /api/health reports; either way postgres must be listed by the API.
if ! provider_lines="$(python3 - "$health" "${DEMO_SMOKE_PROVIDERS:-}" <<'PY'
import json
import sys

listed = json.load(open(sys.argv[1]))["providers"]
named = [name.strip() for name in sys.argv[2].split(",") if name.strip()]
wanted = named if named else listed
ordered = ["postgres"] + [name for name in dict.fromkeys(wanted) if name != "postgres"]
missing = [name for name in ordered if name not in listed]
if missing:
    print(f"smoke.sh: /api/health does not list {', '.join(missing)} (it lists: {', '.join(listed)}). "
          "Start the engine and set its connection variable (see README.md), or name the providers to compare with DEMO_SMOKE_PROVIDERS.",
          file=sys.stderr)
    sys.exit(3)
print("\n".join(ordered))
PY
)"; then
  exit 2
fi
mapfile -t providers <<<"$provider_lines"
if [ "${DEMO_SMOKE_SABOTAGE:-}" != "" ] && ! printf '%s\n' "${providers[@]}" | grep -qx mongo; then
  echo "smoke.sh: DEMO_SMOKE_SABOTAGE needs mongo in the provider list (it is: ${providers[*]})." >&2
  exit 2
fi

# name|route|request file|kind
scenarios=(
  "text contains + sort|rows|text-contains-sort.json|rows"
  "number greater than|rows|number-gt.json|rows"
  "date in range (customFilters)|rows|date-in-range.json|rows"
  "datetime in range (customFilters)|rows|datetime-in-range.json|rows"
  "boolean equals|rows|boolean-eq.json|rows"
  "set filter on status|rows|set-filter-status.json|rows"
  "set filter with nothing selected (SEM-8)|rows|set-filter-empty.json|rows"
  "notIn with no values, every row (SEM-8)|rows|notin-empty.json|rows"
  "community flat request: text filter, two sorts|rows|community-flat-text.json|rows"
  "community flat request: number AND, date, page 2|rows|community-flat-number-date.json|rows"
  "in range on amount, exclusive bounds (translated)|rows|in-range-amount.json|rows"
  "in range on a date-time column (translated)|rows|in-range-ordered-at.json|rows"
  "in range on a date column plus a set filter|rows|in-range-shipped-on.json|rows"
  "one-bound range becomes greater-than|rows|in-range-one-bound.json|rows"
  "group by country: sum, avg, count|rows|group-by-country.json|rows"
  "page past the end|rows|past-the-end.json|rows"
  "set values for country|values|set-values-country.json|values"
  "csv export, status filter|export.csv|export-status.json|csv"
  "csv export, formula cell|export.csv|export-formula.json|csv-formula"
  "compiled query view|query|query-text-sort.json|query"
  "hostile: native inRange (the library refuses it)|rows|bad-native-inrange.json|bad"
  "hostile: unknown column|rows|bad-unknown-column.json|bad"
  "hostile: page of 100000|rows|bad-oversized-page.json|bad"
  "hostile: malformed JSON|rows|bad-malformed-json.json|bad"
)

: >"$work/index.tsv"
n=0
for entry in "${scenarios[@]}"; do
  IFS='|' read -r name route file kind <<<"$entry"
  n=$((n + 1))
  for provider in "${providers[@]}"; do
    out="$work/$n.$provider"
    code="$(curl -sS --max-time 120 -o "$out.body" -w '%{http_code}' -X POST -H 'Content-Type: application/json' \
      --data-binary "@$here/smoke/$file" "$api/api/$provider/$route" 2>"$out.err")" || code=000
    echo "$code" >"$out.status"
  done
  printf '%s\t%s\t%s\n' "$n" "$name" "$kind" >>"$work/index.tsv"
done

set +e
python3 - "$work" "${providers[@]}" <<'PY'
import csv
import decimal
import io
import json
import math
import os
import re
import sys

work = sys.argv[1]
sabotage = os.environ.get("DEMO_SMOKE_SABOTAGE") == "1"
sabotage_csv = os.environ.get("DEMO_SMOKE_SABOTAGE") == "csv"
csv_seen = 0
rows_scenarios_seen = 0
PROVIDERS = tuple(sys.argv[2:])
BASE, OTHERS = PROVIDERS[0], PROVIDERS[1:]
ENV_ERROR, DIFFERENT = 2, 1

# Where a provider legitimately differs from the baseline, as data: provider -> {key suffix: relative tolerance}. Numbers under a key
# with that suffix compare as |a-b| <= tol * max(1, |a|, |b|). Everything not listed here is exact (except the avg rule below, for all).
RULES = {"sqlite": {"_sum": 1e-9}}
AVG_RULE = {"_avg": 1e-6}
used = {}  # (scenario, provider, key) -> how many values were equal only because of a RULES entry
current_scenario = ""


def read(n, provider):
    base = os.path.join(work, f"{n}.{provider}")
    status = int(open(base + ".status").read().strip())
    with open(base + ".body", "rb") as f:
        body = f.read()
    return status, body, open(base + ".err").read().strip()


def same(a, b, key="", provider=None, track=False):
    """Exact, except numbers under a key ending _avg (relative 1e-6) and the provider's RULES (relative, per key suffix)."""
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(same(a[k], b[k], k, provider, track) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(same(x, y, key, provider, track) for x, y in zip(a, b))
    numbers = all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in (a, b))
    if numbers:
        if a == b:
            return True  # 12877.0 and 12877 are the same number: PostgreSQL writes numeric(12,2) with a ".0", MongoDB without
        for suffix, tol in RULES.get(provider, {}).items():
            if key.endswith(suffix):
                ok = abs(a - b) <= tol * max(1, abs(a), abs(b))
                if ok and track:
                    used[(current_scenario, provider, key)] = used.get((current_scenario, provider, key), 0) + 1
                return ok
        if key.endswith("_avg"):
            return math.isclose(a, b, rel_tol=AVG_RULE["_avg"])
        return False
    return type(a) is type(b) and a == b


def first_difference(a, b, provider):
    for i, (x, y) in enumerate(zip(a, b)):
        if not same(x, y, provider=provider):
            keys = [k for k in x if not same(x.get(k), y.get(k), k, provider)] if isinstance(x, dict) and isinstance(y, dict) else []
            what = ", ".join(f"{k}: {x[k]!r} vs {y.get(k)!r}" for k in keys[:2]) or "different shape"
            return f"row {i} ({what})"
    return f"lengths {len(a)} vs {len(b)}"


def csv_records(body):
    """The CSV as a list of lines, each a list of cells; line endings are normalised first."""
    text = body.decode("utf-8").replace("\r\n", "\n").replace("\r", "\n")
    return list(csv.reader(io.StringIO(text, newline=""))) if text else []


DECIMAL = re.compile(r"-?\d+(\.\d+)?")


def csv_cell_same(a, b):
    """Exact text, except two plain decimals that are the same number: PostgreSQL writes numeric(12,2) as 20568.00, MongoDB as 20568."""
    if a == b:
        return True
    return bool(DECIMAL.fullmatch(a) and DECIMAL.fullmatch(b) and decimal.Decimal(a) == decimal.Decimal(b))


def csv_same(a, b):
    return len(a) == len(b) and all(
        len(x) == len(y) and all(csv_cell_same(c, d) for c, d in zip(x, y)) for x, y in zip(a, b))


def error_kind(status, body):
    try:
        return json.loads(body)["error"]["kind"] if status == 400 else None
    except (ValueError, KeyError, TypeError):
        return None


def compare(kind, base_status, base_body, other, status, body):
    """Compare one provider's answer with the baseline. Returns (cell, ok, detail)."""
    global rows_scenarios_seen, csv_seen
    if kind == "bad":
        bk, ok_kind = error_kind(base_status, base_body), error_kind(status, body)
        ok = bk is not None and bk == ok_kind
        return f"{status} {ok_kind or '-'}", ok, "" if ok else f": want 400 + the same error kind as {BASE} ({bk or '-'})"
    if status != 200:
        return f"HTTP {status}", False, ": expected 200"
    if kind == "rows":
        pg, ot = json.loads(base_body), json.loads(body)
        if sabotage and other == "mongo" and ot["rows"] and rows_scenarios_seen == 1:
            # test-only: pretend mongo returned a different first row
            first = ot["rows"][0]
            key = "id" if "id" in first else next(iter(first))
            first[key] = (first[key] + 1) if isinstance(first[key], int) else f"{first[key]}-sabotaged"
        cell = f"{len(ot['rows'])} rows, lastRow {ot['lastRow']}"
        ok = pg.get("lastRow") == ot.get("lastRow") and same(pg["rows"], ot["rows"], provider=other, track=True)
        detail = "" if ok else ": " + (
            f"lastRow {pg.get('lastRow')} vs {ot.get('lastRow')}"
            if pg.get("lastRow") != ot.get("lastRow") else first_difference(pg["rows"], ot["rows"], other))
        return cell, ok, detail
    if kind == "values":
        pg, ot = json.loads(base_body)["values"], json.loads(body)["values"]
        canon = lambda values: sorted(json.dumps(v) for v in values)
        return f"{len(ot)} values", canon(pg) == canon(ot), ""
    if kind in ("csv", "csv-formula"):
        pg_lines, ot_lines = csv_records(base_body), csv_records(body)
        if sabotage_csv and other == "mongo" and csv_seen == 0 and len(ot_lines) > 1:
            # test-only: pretend mongo wrote a different first data row
            ot_lines[1] = ot_lines[1] + ["sabotaged"]
        ok = len(pg_lines) > 1 and csv_same(pg_lines, ot_lines)
        detail = ""
        if not ok and len(pg_lines) > 1:
            if len(pg_lines) != len(ot_lines):
                detail = f": {len(pg_lines)} vs {len(ot_lines)} lines"
            else:
                i = next(i for i, (x, y) in enumerate(zip(pg_lines, ot_lines)) if not csv_same([x], [y]))
                detail = f": line {i} differs"
        if kind == "csv-formula":
            has = any("'=SUM(A1)" in cell for line in ot_lines for cell in line)
            if not has:
                ok, detail = False, f": no '=SUM(A1) row ({other})"
        return f"{len(ot_lines) - 1} data rows", ok, detail
    if kind == "query":
        ot = json.loads(body)
        if other == "mongo":
            ok = ot.get("kind") == "mongo" and bool(ot.get("text"))
        elif other == "ef":
            # EF Core inlines parameter values in its text (shown as comment lines), so only the shape is asserted
            ok = ot.get("kind") == "sql" and ot.get("dialect") == "ef-npgsql" and bool(ot.get("text"))
        else:
            ok = ot.get("kind") == "sql" and bool(ot.get("text"))
        return f"kind {ot.get('kind')}" + (f", {ot.get('dialect')}" if other == "ef" else ""), ok, ""
    raise ValueError(kind)


table, failures, env_problems = [], 0, []
for line in open(os.path.join(work, "index.tsv"), encoding="utf-8").read().splitlines():
    n, name, kind = line.split("\t")
    current_scenario = name
    answers = {p: read(n, p) for p in PROVIDERS}

    if any(a[0] == 0 or a[0] == 503 for a in answers.values()):
        env_problems.append(name + ": " + ", ".join(f"{p} HTTP {a[0]} {a[2]}".strip() for p, a in answers.items()))
        table.append((name, *[f"HTTP {answers[p][0]}" for p in PROVIDERS], "ENVIRONMENT"))
        continue

    base_status, base_body, _ = answers[BASE]
    cells = {}
    if kind == "query":
        # the baseline answers its own shape: parameterised SQL whose values never appear inline
        b = json.loads(base_body) if base_status == 200 else {}
        base_ok = base_status == 200 and b.get("kind") == "sql" and bool(b.get("text"))
        cells[BASE] = f"kind {b.get('kind')}"
    elif kind == "bad":
        k = error_kind(base_status, base_body)
        base_ok = k is not None
        cells[BASE] = f"{base_status} {k or '-'}"
    else:
        base_ok = base_status == 200
        if base_ok:
            if kind == "rows":
                r = json.loads(base_body)
                cells[BASE] = f"{len(r['rows'])} rows, lastRow {r['lastRow']}"
            elif kind == "values":
                cells[BASE] = f"{len(json.loads(base_body)['values'])} values"
            else:
                cells[BASE] = f"{len(csv_records(base_body)) - 1} data rows"
        else:
            cells[BASE] = f"HTTP {base_status}"
    problems = [] if base_ok else [f"{BASE} did not answer as expected"]
    for other in OTHERS:
        status, body, _ = answers[other]
        cell, ok, detail = compare(kind, base_status, base_body, other, status, body) if base_ok else (f"HTTP {status}", False, "")
        cells[other] = cell
        if not ok:
            problems.append(f"{other}{detail}")
    if kind == "rows":
        rows_scenarios_seen += 1
    elif kind in ("csv", "csv-formula"):
        csv_seen += 1
    failures += 1 if problems else 0
    table.append((name, *[cells[p] for p in PROVIDERS], "equal" if not problems else "DIFFERENT: " + "; ".join(problems)))

if env_problems:
    print("smoke.sh: environment problem (a database is down, or the API failed):", file=sys.stderr)
    for p in env_problems:
        print("  " + p, file=sys.stderr)
    print("  Check `docker compose ps` and that `dotnet run --project Api -- seed` has run.", file=sys.stderr)
    sys.exit(ENV_ERROR)

header = ("scenario", *PROVIDERS, "result")
widths = [max(len(r[i]) for r in table + [header]) for i in range(len(PROVIDERS) + 1)]
fmt = "  ".join("{:<%d}" % w for w in widths) + "  {}"
print(fmt.format(*header[:-1], header[-1]))
print(fmt.format(*["-" * w for w in widths[:-1]], "-" * widths[-1], "------"))
for row in table:
    print(fmt.format(*row))
print()
if used:
    parts = [f"{scenario}: {provider} {key} ({count} value{'s' if count != 1 else ''})" for (scenario, provider, key), count in used.items()]
    print("smoke.sh: a RULES tolerance was needed (equal only within it, not exactly): " + "; ".join(parts) + ".")
if failures:
    print(f"smoke.sh: {failures} of {len(table)} scenarios differ or lack the expected 400.")
    sys.exit(DIFFERENT)
print(f"smoke.sh: all {len(table)} scenarios are equal on all {len(PROVIDERS)} providers ({', '.join(PROVIDERS)}).")
PY
exit $?
