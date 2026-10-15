#!/usr/bin/env bash
# Pack smoke (story #1658, AC-2): packs the ten Craticula packages into a temporary local feed, inspects every
# nupkg (metadata, README, XML docs, embedded PDB + SourceLink, dependency lists) and restores, builds and runs
# clean scratch consumers against that feed (scripts/pack_smoke.py). Needs the .NET 10 SDK, Python 3.10+ and, for
# the Dapper consumer, nuget.org. Takes a few minutes; it is not part of the offline `dotnet test` suite.
# Exit 0 clean; 1 a defect (printed per package); 2 usage or environment error. Arguments pass to pack_smoke.py:
#   --feed <dir>       inspect the .nupkg files already in <dir> instead of packing
#   --keep             keep the temporary folder and print its path
#   --skip-consumers   inspect the packages only
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
  echo "pack-smoke.sh: Python 3.10+ is required on PATH as python3 or python" >&2
  exit 2
fi

exec "$python" scripts/pack_smoke.py "$@"
