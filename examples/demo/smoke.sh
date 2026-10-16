#!/usr/bin/env bash
# Demo smoke test: sends the same fixed requests (smoke/*.json) to the running demo API for PostgreSQL and for MongoDB
# and checks that both providers answer the same. It starts nothing; run the quickstart in README.md first.
# Needs only bash, curl and python3 (3.8+).
#
#   bash smoke.sh [--api URL]        default URL: http://127.0.0.1:5080
#
# Exit 0: every scenario is equal on both providers and every hostile request got a 400 with an error kind.
#      1: a difference, or a missing 400.
#      2: usage or environment problem (no curl/python3, API unreachable, a provider missing, a database down).
#
# Equality is exact (numbers by value: 12877.0 equals 12877), except avg: PostgreSQL averages decimals with 16 digits and MongoDB with 28, so keys ending in
# "_avg" are compared with a relative tolerance of 1e-6. CSV exports are compared line by line, cell by cell, in full,
# after line endings are normalised: header and every data row must be identical on both providers, with the same
# by-value rule for plain decimals (PostgreSQL writes an amount as 20568.00, MongoDB as 20568).
#
# Test-only: DEMO_SMOKE_SABOTAGE changes one value on the mongo side before comparing, to prove the script can fail
# (it must then exit 1). Never set it otherwise.
#   DEMO_SMOKE_SABOTAGE=1     the first data row of the second rows scenario
#   DEMO_SMOKE_SABOTAGE=csv   the first data row of the first CSV export
set -euo pipefail

api="http://127.0.0.1:5080"
while [ $# -gt 0 ]; do
  case "$1" in
    --api) [ $# -ge 2 ] || { echo "smoke.sh: --api needs a URL" >&2; exit 2; }; api="${2%/}"; shift 2 ;;
    -h|--help) sed -n '2,20p' "${BASH_SOURCE[0]}" | sed 's/^# \{0,1\}//'; exit 0 ;;
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
if ! python3 -c 'import json,sys; p=json.load(open(sys.argv[1]))["providers"]; sys.exit(0 if {"postgres","mongo"} <= set(p) else 1)' "$health" 2>/dev/null; then
  echo "smoke.sh: /api/health does not list both providers (postgres, mongo): $(cat "$health")" >&2
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
  "group by country: sum, avg, count|rows|group-by-country.json|rows"
  "page past the end|rows|past-the-end.json|rows"
  "set values for country|values|set-values-country.json|values"
  "csv export, status filter|export.csv|export-status.json|csv"
  "csv export, formula cell|export.csv|export-formula.json|csv-formula"
  "compiled query view|query|query-text-sort.json|query"
  "hostile: unknown column|rows|bad-unknown-column.json|bad"
  "hostile: page of 100000|rows|bad-oversized-page.json|bad"
  "hostile: malformed JSON|rows|bad-malformed-json.json|bad"
)

: >"$work/index.tsv"
n=0
for entry in "${scenarios[@]}"; do
  IFS='|' read -r name route file kind <<<"$entry"
  n=$((n + 1))
  for provider in postgres mongo; do
    out="$work/$n.$provider"
    code="$(curl -sS --max-time 120 -o "$out.body" -w '%{http_code}' -X POST -H 'Content-Type: application/json' \
      --data-binary "@$here/smoke/$file" "$api/api/$provider/$route" 2>"$out.err")" || code=000
    echo "$code" >"$out.status"
  done
  printf '%s\t%s\t%s\n' "$n" "$name" "$kind" >>"$work/index.tsv"
done

set +e
python3 - "$work" <<'PY'
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
PROVIDERS = ("postgres", "mongo")
ENV_ERROR, DIFFERENT = 2, 1


def read(n, provider):
    base = os.path.join(work, f"{n}.{provider}")
    status = int(open(base + ".status").read().strip())
    with open(base + ".body", "rb") as f:
        body = f.read()
    return status, body, open(base + ".err").read().strip()


def same(a, b, key=""):
    """Exact, except numbers under a key ending in _avg (relative 1e-6)."""
    if isinstance(a, dict) and isinstance(b, dict):
        return a.keys() == b.keys() and all(same(a[k], b[k], k) for k in a)
    if isinstance(a, list) and isinstance(b, list):
        return len(a) == len(b) and all(same(x, y, key) for x, y in zip(a, b))
    numbers = all(isinstance(v, (int, float)) and not isinstance(v, bool) for v in (a, b))
    if numbers and key.endswith("_avg"):
        return math.isclose(a, b, rel_tol=1e-6)
    if numbers:
        return a == b  # 12877.0 and 12877 are the same number: PostgreSQL writes numeric(12,2) with a ".0", MongoDB without
    return type(a) is type(b) and a == b


def first_difference(a, b):
    for i, (x, y) in enumerate(zip(a, b)):
        if not same(x, y):
            keys = [k for k in x if not same(x.get(k), y.get(k), k)] if isinstance(x, dict) and isinstance(y, dict) else []
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


rows_scenarios_seen = 0
table, failures, env_problems = [], 0, []
for line in open(os.path.join(work, "index.tsv"), encoding="utf-8").read().splitlines():
    n, name, kind = line.split("\t")
    pg_status, pg_body, pg_err = read(n, "postgres")
    mg_status, mg_body, mg_err = read(n, "mongo")

    if 0 in (pg_status, mg_status) or pg_status == 503 or mg_status == 503:
        env_problems.append(f"{name}: postgres HTTP {pg_status} {pg_err}, mongo HTTP {mg_status} {mg_err}".strip())
        table.append((name, f"HTTP {pg_status}", f"HTTP {mg_status}", "ENVIRONMENT"))
        continue

    if kind == "bad":
        def error_kind(status, body):
            try:
                return json.loads(body)["error"]["kind"] if status == 400 else None
            except (ValueError, KeyError, TypeError):
                return None
        pk, mk = error_kind(pg_status, pg_body), error_kind(mg_status, mg_body)
        cells = [f"{pg_status} {pk or '-'}", f"{mg_status} {mk or '-'}"]
        ok = pk is not None and pk == mk
        table.append((name, cells[0], cells[1], "equal" if ok else "DIFFERENT: want 400 + the same error kind"))
        failures += 0 if ok else 1
        continue

    if pg_status != 200 or mg_status != 200:
        table.append((name, f"HTTP {pg_status}", f"HTTP {mg_status}", "DIFFERENT: expected 200 on both"))
        failures += 1
        continue

    if kind == "rows":
        pg, mg = json.loads(pg_body), json.loads(mg_body)
        if sabotage and mg["rows"] and rows_scenarios_seen == 1:
            # test-only: pretend mongo returned a different first row
            first = mg["rows"][0]
            key = "id" if "id" in first else next(iter(first))
            first[key] = (first[key] + 1) if isinstance(first[key], int) else f"{first[key]}-sabotaged"
        rows_scenarios_seen += 1
        cells = [f"{len(r['rows'])} rows, lastRow {r['lastRow']}" for r in (pg, mg)]
        ok = pg.get("lastRow") == mg.get("lastRow") and same(pg["rows"], mg["rows"])
        detail = "" if ok else ": " + (
            f"lastRow {pg.get('lastRow')} vs {mg.get('lastRow')}"
            if pg.get("lastRow") != mg.get("lastRow") else first_difference(pg["rows"], mg["rows"]))
        table.append((name, cells[0], cells[1], "equal" if ok else "DIFFERENT" + detail))
        failures += 0 if ok else 1
    elif kind == "values":
        pg, mg = json.loads(pg_body)["values"], json.loads(mg_body)["values"]
        canon = lambda values: sorted(json.dumps(v) for v in values)
        ok = canon(pg) == canon(mg)
        table.append((name, f"{len(pg)} values", f"{len(mg)} values", "equal" if ok else "DIFFERENT"))
        failures += 0 if ok else 1
    elif kind in ("csv", "csv-formula"):
        pg_lines = csv_records(pg_body)
        mg_lines = csv_records(mg_body)
        if sabotage_csv and csv_seen == 0 and len(mg_lines) > 1:
            # test-only: pretend mongo wrote a different first data row
            mg_lines[1] = mg_lines[1] + ["sabotaged"]
        csv_seen += 1
        ok = len(pg_lines) > 1 and csv_same(pg_lines, mg_lines)
        detail = ""
        if not ok and len(pg_lines) > 1:
            if len(pg_lines) != len(mg_lines):
                detail = f": {len(pg_lines)} vs {len(mg_lines)} lines"
            else:
                i = next(i for i, (x, y) in enumerate(zip(pg_lines, mg_lines)) if not csv_same([x], [y]))
                detail = f": line {i} differs"
        if kind == "csv-formula":
            marker = "'=SUM(A1)"
            has = [any(marker in cell for line in lines for cell in line) for lines in (pg_lines, mg_lines)]
            if not all(has):
                ok, detail = False, f": no '=SUM(A1) row (postgres {has[0]}, mongo {has[1]})"
        table.append((name, f"{len(pg_lines) - 1} data rows", f"{len(mg_lines) - 1} data rows",
                      "equal" if ok else "DIFFERENT" + detail))
        failures += 0 if ok else 1
    elif kind == "query":
        pg, mg = json.loads(pg_body), json.loads(mg_body)
        ok = pg.get("kind") == "sql" and mg.get("kind") == "mongo" and pg.get("text") and mg.get("text")
        table.append((name, f"kind {pg.get('kind')}", f"kind {mg.get('kind')}", "equal" if ok else "DIFFERENT"))
        failures += 0 if ok else 1

if env_problems:
    print("smoke.sh: environment problem (a database is down, or the API failed):", file=sys.stderr)
    for p in env_problems:
        print("  " + p, file=sys.stderr)
    print("  Check `docker compose ps` and that `dotnet run --project Api -- seed` has run.", file=sys.stderr)
    sys.exit(ENV_ERROR)

widths = [max(len(r[i]) for r in table + [("scenario", "postgres", "mongo", "result")]) for i in range(3)]
fmt = "{:<%d}  {:<%d}  {:<%d}  {}" % tuple(widths)
print(fmt.format("scenario", "postgres", "mongo", "result"))
print(fmt.format("-" * widths[0], "-" * widths[1], "-" * widths[2], "------"))
for row in table:
    print(fmt.format(*row))
print()
if failures:
    print(f"smoke.sh: {failures} of {len(table)} scenarios differ or lack the expected 400.")
    sys.exit(DIFFERENT)
print(f"smoke.sh: all {len(table)} scenarios are equal on both providers.")
PY
exit $?
