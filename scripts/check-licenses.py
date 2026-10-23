#!/usr/bin/env python3
"""License policy gate: every NuGet package under csharp/ must carry a license the policy accepts (ADR-0003, TRD OS-6;
spec FR-08, FR-09, FR-14, NFR-03, SC-04).

Usage: check-licenses.py [--root <repo>] [--allowlist <file>] [--overrides <file>] [--adr-dir <dir>]
                         [--tool "<command>"] [--json-dir <dir>]            (stdlib, Python 3.10+)

HOW IT RUNS. The `nuget-license` dotnet tool (pinned in csharp/.config/dotnet-tools.json) lists every package of a set of
projects with its transitive packages and the license it read (SPDX expression, or the text of a license file or URL the
tool could not map). The tool prints one flat, de-duplicated list per run and does not say which project uses a package, so
the projects run in three scopes and this script applies the policy to each list:
    src    csharp/src        the packages that ship: strictest, no test-only package may appear;
    tests  csharp/tests      the test projects: the test-only packages (Oracle driver, Testcontainers.Oracle) are accepted;
    other  csharp/tools, csharp/samples
Every csproj under csharp/ must belong to a scope, or the check stops (exit 2): a new project folder cannot escape it.
The project named in a failure line is read from each project's obj/project.assets.json (restore first), else the scope.

THE POLICY (policy file scripts/license-allowlist.json):
  - allowed SPDX id: accepted. A compound expression: `A OR B` needs one accepted side, `A AND B` needs both; `WITH <exception>`
    and a trailing `+` are ignored.
  - denied family (GPL, LGPL, AGPL, SSPL, BUSL, Elastic, Commons-Clause, any "non-commercial"): always a failure.
  - case by case (MPL-2.0): accepted only when an ADR in docs/adr/ names the package and MPL-2.0. FR-09. If docs/adr/ does not
    exist (the public mirror has no docs/) the check FAILS CLOSED and says the folder is missing.
  - anything else (a license file, a bare URL, an id not on the list): a failure, unless an override covers the package.
  - forbidden package names (MySql.Data: "use MySqlConnector") fail whatever license they declare, and cannot be overridden.
  - test-only package names (Oracle.ManagedDataAccess.Core, Testcontainers.Oracle) fail in every scope but `tests`.
  - an override (scripts/license-overrides.json: package, exact version, license, reason) replaces what the tool read, for
    a package whose license the tool cannot parse. It needs a non-empty reason (exit 2 otherwise). Its license is judged by
    the same policy, except that a license that is neither allowed nor denied (e.g. a vendor's free-use terms) is accepted
    as a reviewed exception. An optional "scopes" list limits it to those scopes (the real entries say ["tests"]). An override that matches no package is stale and fails (exit 1), like a stale sec2 allowlist
    entry; a version bump therefore re-opens the review.

Exit codes: 0 clean; 1 at least one policy failure or stale override, each printed as
`<scope>: <package> <version>: license '<license>': <why> [project: <projects>]`; 2 usage or environment error: the tool is
missing or prints nothing usable, a scope has no project, a malformed policy or overrides file.
`--json-dir` reads `<scope>.json` (the tool's JSON array) instead of running the tool: the offline tests use it.
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import re
import shlex
import subprocess
import sys
import tempfile
import time
from pathlib import Path

HERE = Path(__file__).resolve().parent
ROOT = HERE.parent

# scope name -> folders under csharp/ (relative to --root)
SCOPES: dict[str, tuple[str, ...]] = {
    "src": ("csharp/src",),
    "tests": ("csharp/tests",),
    "other": ("csharp/tools", "csharp/samples"),
}
TEST_SCOPE = "tests"
DEFAULT_TOOL = "dotnet tool run nuget-license --"
TOOL_TIMEOUT_SECONDS = 150  # NFR-03: the whole check adds under 3 minutes; a hung tool fails visibly (spec threat model)

OK, ADR, UNKNOWN, DENIED = 0, 1, 2, 3  # leaf verdicts, best to worst


class CheckError(Exception):
    """A usage or environment error: exit 2."""


def load_json(path: Path, what: str):
    try:
        return json.loads(path.read_text(encoding="utf-8"))
    except (OSError, ValueError) as error:
        raise CheckError(f"{what} {path} is unreadable or not JSON: {error}") from error


class Policy:
    def __init__(self, data: dict, path: Path):
        try:
            self.allowed = {i.lower(): i for i in data["allowed"]}
            self.case_by_case = {i.lower(): i for i in data["case_by_case"]}
            self.denied_prefixes = [p.lower() for p in data["denied_prefixes"]]
            self.denied_markers = [m.lower() for m in data["denied_markers"]]
            self.forbidden = {k.lower(): v for k, v in data["forbidden_packages"].items()}
            self.test_only = [p.lower() for p in data["test_only_packages"]]
        except (KeyError, TypeError, AttributeError) as error:
            raise CheckError(f"policy file {path} is malformed: {error!r}") from error

    def leaf(self, license_id: str) -> int:
        low = license_id.lower()
        if any(marker in low for marker in self.denied_markers):
            return DENIED
        if any(low.startswith(prefix) for prefix in self.denied_prefixes):
            return DENIED
        if low in self.allowed:
            return OK
        if low in self.case_by_case:
            return ADR
        return UNKNOWN

    def verdict(self, expression: str) -> tuple[int, str]:
        """(verdict, the license id that decided it) for an SPDX expression or free text."""
        text = (expression or "").strip()
        if not text or not re.fullmatch(r"[A-Za-z0-9.+()\- ]+", text):
            # empty, a URL or a file name is not an SPDX expression; a URL or text that names a GNU license is still denied
            if re.search(r"(?<![a-z])(a|l)?gpl(?![a-z])", text.lower()) or any(m in text.lower() for m in self.denied_markers):
                return DENIED, text
            return UNKNOWN, text
        tokens = re.findall(r"\(|\)|[^\s()]+", text)
        pos = 0

        def parse_or() -> tuple[int, str]:
            nonlocal pos
            best = parse_and()
            while pos < len(tokens) and tokens[pos].upper() == "OR":
                pos += 1
                other = parse_and()
                if other[0] < best[0]:
                    best = other
            return best

        def parse_and() -> tuple[int, str]:
            nonlocal pos
            worst = parse_term()
            while pos < len(tokens) and tokens[pos].upper() == "AND":
                pos += 1
                other = parse_term()
                if other[0] > worst[0]:
                    worst = other
            return worst

        def parse_term() -> tuple[int, str]:
            nonlocal pos
            if pos >= len(tokens):
                raise ValueError("unexpected end")
            token = tokens[pos]
            pos += 1
            if token == "(":
                inner = parse_or()
                if pos >= len(tokens) or tokens[pos] != ")":
                    raise ValueError("unbalanced")
                pos += 1
                return inner
            if token in (")",) or token.upper() in ("AND", "OR", "WITH"):
                raise ValueError("unexpected operator")
            if pos < len(tokens) and tokens[pos].upper() == "WITH":
                pos += 2  # the exception id is ignored
            plain = token[:-1] if token.endswith("+") else token
            return self.leaf(plain), plain

        try:
            result = parse_or()
            if pos != len(tokens):
                raise ValueError("trailing tokens")
        except ValueError:
            return UNKNOWN, text
        return result


class Override:
    def __init__(self, entry: dict, index: int, path: Path):
        if not isinstance(entry, dict):
            raise CheckError(f"{path}: entry {index} is not an object")
        for key in ("package", "version", "license", "reason"):
            value = entry.get(key)
            if not isinstance(value, str) or not value.strip():
                label = entry.get("package", f"entry {index}")
                raise CheckError(f"{path}: override for {label} has no non-empty '{key}' (a written reason is required, ADR-0003)")
        self.package = entry["package"].strip()
        self.version = entry["version"].strip()
        self.license = entry["license"].strip()
        self.reason = entry["reason"].strip()
        scopes = entry.get("scopes")
        if scopes is not None and (not isinstance(scopes, list) or not scopes or not set(scopes) <= set(SCOPES)):
            raise CheckError(f"{path}: override for {self.package} has 'scopes' that is not a non-empty list of {sorted(SCOPES)}")
        self.scopes = scopes  # None = every scope
        self.used = False

    def matches(self, package: str, version: str, scope: str) -> bool:
        return (self.package.lower() == package.lower() and self.version == version
                and (self.scopes is None or scope in self.scopes))


def load_overrides(path: Path) -> list[Override]:
    data = load_json(path, "overrides file")
    entries = data.get("overrides") if isinstance(data, dict) else None
    if not isinstance(entries, list):
        raise CheckError(f"overrides file {path} must be an object with an 'overrides' array")
    return [Override(entry, index, path) for index, entry in enumerate(entries, 1)]


def adr_names(adr_dir: Path, package: str) -> bool | None:
    """True when an ADR names the package and MPL-2.0; False when none does; None when docs/adr/ is missing."""
    if not adr_dir.is_dir():
        return None
    name = re.compile(r"(?<![A-Za-z0-9.])" + re.escape(package) + r"(?![A-Za-z0-9])", re.IGNORECASE)
    for adr in sorted(adr_dir.glob("*.md")):
        text = adr.read_text(encoding="utf-8", errors="replace")
        if name.search(text) and "MPL-2.0" in text:
            return True
    return False


def scope_projects(root: Path, scope: str) -> list[Path]:
    found: list[Path] = []
    for folder in SCOPES[scope]:
        base = root / folder
        if base.is_dir():
            found += [p for p in base.rglob("*.csproj") if not {"obj", "bin"} & set(p.relative_to(base).parts)]
    return sorted(found)


def check_all_projects_in_scope(root: Path) -> None:
    csharp = root / "csharp"
    known = {p for scope in SCOPES for p in scope_projects(root, scope)}
    stray = [p for p in csharp.rglob("*.csproj") if not {"obj", "bin"} & set(p.relative_to(csharp).parts) and p not in known]
    if stray:
        names = ", ".join(str(p.relative_to(root)) for p in sorted(stray))
        raise CheckError(f"project outside the license scopes ({', '.join(f for s in SCOPES.values() for f in s)}): {names}")


def run_tool(tool: str, root: Path, scope: str) -> list[dict]:
    projects = scope_projects(root, scope)
    if not projects:
        raise CheckError(f"scope '{scope}' ({', '.join(SCOPES[scope])}) holds no .csproj")
    with tempfile.TemporaryDirectory(prefix="check-licenses-") as tmp:
        project_list = Path(tmp) / "projects.json"
        output = Path(tmp) / "licenses.json"
        project_list.write_text(json.dumps([str(p) for p in projects]), encoding="utf-8")
        command = shlex.split(tool) + ["-ji", str(project_list), "-t", "-o", "Json", "-fo", str(output)]
        try:
            done = subprocess.run(command, cwd=root / "csharp", capture_output=True, text=True, encoding="utf-8",
                                  timeout=TOOL_TIMEOUT_SECONDS)
        except (OSError, subprocess.TimeoutExpired) as error:
            raise CheckError(f"nuget-license could not run for scope '{scope}': {error}") from error
        if not output.is_file():
            raise CheckError(f"nuget-license (exit {done.returncode}) wrote no output for scope '{scope}': "
                             f"{(done.stderr or done.stdout).strip()[:400]}")
        return parse_tool_output(output.read_text(encoding="utf-8"), scope)


def parse_tool_output(text: str, scope: str) -> list[dict]:
    try:
        data = json.loads(text)
    except ValueError as error:
        raise CheckError(f"nuget-license output for scope '{scope}' is not JSON: {error}") from error
    if not isinstance(data, list):
        raise CheckError(f"nuget-license output for scope '{scope}' is not an array")
    for entry in data:
        if not isinstance(entry, dict) or "PackageId" not in entry or "PackageVersion" not in entry:
            raise CheckError(f"nuget-license output for scope '{scope}' has an entry without PackageId/PackageVersion")
    return data


def read_json_dir(json_dir: Path, scope: str) -> list[dict]:
    path = json_dir / f"{scope}.json"
    if not path.is_file():
        return []
    return parse_tool_output(path.read_text(encoding="utf-8"), scope)


def project_index(root: Path, scope: str) -> dict[tuple[str, str], list[str]]:
    """(package id lower, version) -> project names, from each scoped project's obj/project.assets.json."""
    index: dict[tuple[str, str], list[str]] = {}
    for project in scope_projects(root, scope):
        assets = project.parent / "obj" / "project.assets.json"
        if not assets.is_file():
            continue
        try:
            libraries = json.loads(assets.read_text(encoding="utf-8")).get("libraries", {})
        except ValueError:
            continue
        for key in libraries:
            name, _, version = key.partition("/")
            index.setdefault((name.lower(), version), []).append(project.stem)
    return index


def evaluate_scope(scope: str, packages: list[dict], policy: Policy, overrides: list[Override], adr_dir: Path, root: Path) -> list[str]:
    failures: list[tuple[str, str, str, str]] = []  # package, version, license, why
    for entry in packages:
        package, version = entry["PackageId"], entry["PackageVersion"]
        declared = entry.get("License") or entry.get("LicenseUrl") or ""  # empty: the package declares no license at all
        low = package.lower()
        why = None
        shown = declared or "(none declared)"
        forbidden = next((msg for pattern, msg in policy.forbidden.items() if fnmatch.fnmatchcase(low, pattern)), None)
        if forbidden is not None:
            why = f"package is rejected by name: {forbidden} (ADR-0003)"
        elif any(fnmatch.fnmatchcase(low, pattern) for pattern in policy.test_only) and scope != TEST_SCOPE:
            why = "accepted only in test projects under csharp/tests, never in csharp/src, csharp/tools or csharp/samples (ADR-0003, FR-08)"
        else:
            override = next((o for o in overrides if o.matches(package, version, scope)), None)
            if override is not None:
                override.used = True
                shown = f"{override.license} (override)"
                declared = override.license
            level, decided = policy.verdict(declared)
            if override is not None and level == UNKNOWN:
                level = OK  # a reviewed exception: the override carries the reason
            if level == DENIED:
                why = f"license {decided or 'text'} is denied (copyleft, source-available or non-commercial), FR-08"
            elif level == UNKNOWN:
                why = ("license is not on the allowlist and not an SPDX id the policy knows; "
                       "if the tool misread it, add an entry with a reason to scripts/license-overrides.json (FR-08)")
            elif level == ADR:
                found = adr_names(adr_dir, package)
                if found is None:
                    why = (f"{decided} is accepted only when an ADR in docs/adr/ names the package, and the folder "
                           f"{adr_dir} is missing, so the check fails closed (FR-09)")
                elif not found:
                    why = f"{decided} needs an ADR in docs/adr/ that names the package and {decided} (FR-09); none does"
        if why is not None:
            failures.append((package, version, shown, why))
    if not failures:
        return []
    index = project_index(root, scope)
    lines = []
    for package, version, shown, why in sorted(failures):
        projects = sorted(set(index.get((package.lower(), version), []))) or [f"{scope} scope: {', '.join(SCOPES[scope])}"]
        lines.append(f"{scope}: {package} {version}: license '{shown}': {why} [project: {', '.join(projects)}]")
    return lines


def run() -> int:
    parser = argparse.ArgumentParser(description="ADR-0003 license policy check")
    parser.add_argument("--root", type=Path, default=ROOT)
    parser.add_argument("--allowlist", type=Path, default=HERE / "license-allowlist.json")
    parser.add_argument("--overrides", type=Path, default=HERE / "license-overrides.json")
    parser.add_argument("--adr-dir", type=Path, default=None, help="default <root>/docs/adr")
    parser.add_argument("--tool", default=DEFAULT_TOOL, help="command that runs nuget-license (default: the local dotnet tool)")
    parser.add_argument("--json-dir", type=Path, default=None, help="read <scope>.json instead of running the tool (tests)")
    args = parser.parse_args()
    root = args.root.resolve()
    adr_dir = args.adr_dir if args.adr_dir is not None else root / "docs" / "adr"

    started = time.monotonic()
    policy = Policy(load_json(args.allowlist, "policy file"), args.allowlist)
    overrides = load_overrides(args.overrides)

    if args.json_dir is None:
        check_all_projects_in_scope(root)
    lines: list[str] = []
    checked = 0
    for scope in SCOPES:
        packages = read_json_dir(args.json_dir, scope) if args.json_dir else run_tool(args.tool, root, scope)
        checked += len(packages)
        lines += evaluate_scope(scope, packages, policy, overrides, adr_dir, root)
    for override in overrides:
        if not override.used:
            lines.append(f"overrides: {override.package} {override.version}: stale override (no such package in any scope); "
                         f"remove it or update the version, and re-check the reason: {override.reason}")
    for line in lines:
        print(line)
    elapsed = time.monotonic() - started
    print(f"check-licenses: {checked} package entries in {len(SCOPES)} scopes, {len(lines)} failure(s), {elapsed:.1f} s")
    return 1 if lines else 0


def main() -> int:
    for stream in (sys.stdout, sys.stderr):
        stream.reconfigure(encoding="utf-8", errors="replace")
    try:
        return run()
    except CheckError as error:
        print(f"check-licenses.py: {error}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
