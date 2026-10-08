#!/usr/bin/env bash
# Builds the C# conformance driver and grades it with the language-neutral runner
# (spec/conformance-runner.md §9). Extra arguments pass to conformance/run.py
# (--only, --json, --timeout ...). Needs the .NET 10 SDK and Python 3.10+.
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
  echo "conformance.sh: Python 3.10+ is required on PATH as python3 or python" >&2
  exit 2
fi

# A fixed output folder (no target-framework folder to go stale) and a path relative to the repo root,
# so the --driver string is the same under Git Bash on Windows as anywhere else (spec §9).
out="csharp/tools/Craticula.Conformance.Driver/bin/conformance"
dotnet build csharp/tools/Craticula.Conformance.Driver -c Release -v q -o "$out"

exec "$python" conformance/run.py --driver "dotnet $out/Craticula.Conformance.Driver.dll" "$@"
