#!/usr/bin/env bash
# Coverage gate (story #1657): runs the offline test suite with coverlet's cobertura collector, merges
# the per-project results per assembly and fails when an in-scope package (Core, AgGrid, Sql, Mongo,
# Search, Export.Csv) is below the line threshold (scripts/coverage.py). Needs the .NET 10 SDK and
# Python 3.10+. A failing test run fails the gate.
#   --threshold N   minimum line coverage in percent (default 90)
#   --keep          keep the results folder and print its path
#   --from <dir>    skip the test run and merge an existing results folder
# Any other argument passes to coverage.py.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$root"

python=""
for candidate in python3 python; do
  if command -v "$candidate" >/dev/null 2>&1 \
     && "$candidate" -c 'import sys; sys.exit(0 if sys.version_info >= (3, 10) else 1)' 2>/dev/null; then
    python="$candidate"
    break
  fi
done
if [ -z "$python" ]; then
  echo "coverage.sh: Python 3.10+ is required on PATH as python3 or python" >&2
  exit 2
fi

keep=0
from=""
pass=()
while [ $# -gt 0 ]; do
  case "$1" in
    --keep) keep=1; shift ;;
    --from)
      if [ $# -lt 2 ]; then echo "coverage.sh: --from needs a folder" >&2; exit 2; fi
      from="$2"; shift 2 ;;
    *) pass+=("$1"); shift ;;
  esac
done

if [ -n "$from" ]; then
  results="$from"
else
  results="$(mktemp -d)"
  if [ "$keep" -eq 0 ]; then
    trap 'rm -rf "$results"' EXIT
  else
    echo "coverage.sh: results kept in $results" >&2
  fi
  # The offline suite: no Docker, no live databases. A test failure stops the gate here (set -e).
  dotnet test csharp/Craticula.sln \
    --filter "FullyQualifiedName!~MongoDB.Driver.Tests&FullyQualifiedName!~OpenSearch.Client.Tests&FullyQualifiedName!~Integration.Tests.Containers" \
    --collect:"XPlat Code Coverage" --results-directory "$results"
fi

# No exec: the exit trap must still remove the temporary folder.
status=0
"$python" scripts/coverage.py "$results" ${pass[@]+"${pass[@]}"} || status=$?
exit "$status"
