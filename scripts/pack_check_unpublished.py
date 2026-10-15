#!/usr/bin/env python3
"""Fails when a Craticula package version is already on nuget.org (story #1658, release workflow).

Usage: pack_check_unpublished.py [--root <repo>] [--version <v>]   (stdlib, Python 3.10+, needs nuget.org)

The version defaults to <Version> in csharp/src/Directory.Build.props; the ids are the ten packages of
scripts/pack_smoke.py (PACKAGES), so there is one list. For each id it reads
https://api.nuget.org/v3-flatcontainer/<lowercase id>/index.json:
  - 404: the id was never published, fine;
  - 200 and the version is listed: defect (a published version is permanent; bump <Version>);
  - any other answer or a network error (after three attempts): the check fails closed, exit 2.
Exit codes: 0 no version is published; 1 at least one is; 2 the lookup could not be completed.
"""
from __future__ import annotations

import argparse
import json
import sys
import time
import urllib.error
import urllib.request
from pathlib import Path
from typing import Callable

sys.path.insert(0, str(Path(__file__).resolve().parent))
import pack_smoke as smoke  # noqa: E402

INDEX_URL = "https://api.nuget.org/v3-flatcontainer/{id}/index.json"
ATTEMPTS = 3


class RegistryLookupError(Exception):
    """The registry answered something that is neither a version list nor 404."""


def fetch_versions(package_id: str) -> list[str] | None:
    """The versions nuget.org lists for the id, or None on 404. Raises RegistryLookupError when it cannot tell."""
    url = INDEX_URL.format(id=package_id.lower())
    last = ""
    for attempt in range(ATTEMPTS):
        try:
            with urllib.request.urlopen(url, timeout=30) as response:  # noqa: S310 (fixed https URL)
                payload = json.load(response)
            versions = payload.get("versions")
            if not isinstance(versions, list):
                raise RegistryLookupError(f"{package_id}: unexpected answer from {url}")
            return [str(v) for v in versions]
        except urllib.error.HTTPError as error:
            if error.code == 404:
                return None
            last = f"HTTP {error.code}"
        except (urllib.error.URLError, TimeoutError, json.JSONDecodeError) as error:
            last = str(error)
        if attempt + 1 < ATTEMPTS:
            time.sleep(2)
    raise RegistryLookupError(f"{package_id}: lookup failed after {ATTEMPTS} attempts ({last})")


def normalize(version: str) -> str:
    return smoke.norm_version(version.lower().split("+", 1)[0])


def published_ids(version: str, ids: tuple[str, ...] = smoke.PACKAGES,
                  fetch: Callable[[str], list[str] | None] = fetch_versions) -> list[str]:
    """The ids whose `version` is already listed. Raises RegistryLookupError from `fetch` (fail closed)."""
    wanted = normalize(version)
    return [package_id for package_id in ids
            if wanted in {normalize(v) for v in (fetch(package_id) or [])}]


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description="Fail when the version is already on nuget.org.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parent.parent)
    parser.add_argument("--version", help="default: <Version> of csharp/src/Directory.Build.props")
    parser.add_argument("--ids", nargs="+", help="override the ids (testing the check itself)")
    args = parser.parse_args(argv)
    try:
        version = args.version or smoke.read_version_property(args.root.resolve() / "csharp" / "src" / "Directory.Build.props")
    except ValueError as error:
        print(f"pack-check: {error}", file=sys.stderr)
        return 2
    ids = tuple(args.ids) if args.ids else smoke.PACKAGES
    try:
        found = published_ids(version, ids)
    except RegistryLookupError as error:
        print(f"pack-check: {error}", file=sys.stderr)
        return 2
    if found:
        for package_id in found:
            print(f"pack-check: {package_id} {version} is already on nuget.org; a published version is permanent, "
                  f"bump <Version> in csharp/src/Directory.Build.props and release the new one")
        return 1
    print(f"pack-check: OK, none of the {len(ids)} ids has version {version} on nuget.org")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
