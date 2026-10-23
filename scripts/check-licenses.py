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
    never lifts the base license, and an exception that is itself denied (Commons-Clause) denies the term; a trailing `+` is ignored.
  - denied family (GPL, LGPL, AGPL, SSPL, BUSL, Elastic, Commons-Clause, any "non-commercial"): always a failure.
  - case by case (MPL-2.0): accepted only when an ADR in docs/adr/ has `Status: Accepted`, names MPL-2.0 and names the package as a whole token. FR-09. If docs/adr/ does not
    exist (the public mirror has no docs/) the check FAILS CLOSED and says the folder is missing.
  - anything else (a license file, a bare URL, an id not on the list): a failure, unless an override covers the package.
  - forbidden package names (MySql.Data: "use MySqlConnector") fail whatever license they declare, and cannot be overridden.
  - test-only package names (Oracle.ManagedDataAccess.Core, Testcontainers.Oracle) fail in every scope but `tests`.
  - an override (scripts/license-overrides.json: package, exact version, license, reason) replaces what the tool read, for
    a package whose license the tool cannot parse. It needs a non-empty reason (exit 2 otherwise). Its license is judged by
    the same policy (the denied scan includes license names in prose), and it cannot lift what the tool read as denied or as
    MPL-2.0: such a package fails with "override cannot lift a denied license". An override license must be an allowed SPDX
    id, or carry "vendor_terms": true (a vendor's own free-use terms, reviewed by name); the flag never excuses a denied name. An optional "scopes" list limits it to those scopes (the real entries say ["tests"]). An override that matches no package is stale and fails (exit 1), like a stale sec2 allowlist
    entry; a version bump therefore re-opens the review.

Exit codes: 0 clean; 1 at least one policy failure or stale override, each printed as
`<scope>: <package> <version>: license '<license>': <why> [project: <projects>]`; 2 usage or environment error: the tool is
missing, exits non-zero, or prints nothing usable (no output, or an empty list for a scope whose projects name packages), a scope has no project, a malformed policy or overrides file.
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
# License names as prose (a license file's text, a URL, an override written in words) are not SPDX ids, so they are scanned.
PROSE_DENIED = ("general public", "affero", "server side public", "business source", "elastic license", "commons clause",
                "commons-clause", "non commercial", "non-commercial", "noncommercial")
PROSE_ADR = ("mozilla",)


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

    def free_text(self, text: str) -> int | None:
        """A verdict for text that is not an SPDX expression, when it names a denied family or MPL in words; else None."""
        low = text.lower().replace("_", " ")
        if (re.search(r"(?<![a-z])(a|l)?gpl(?![a-z])", low) or any(m in low for m in self.denied_markers)
                or any(m in low for m in PROSE_DENIED)):
            return DENIED
        if any(m in low for m in PROSE_ADR) or re.search(r"(?<![a-z])mpl(?![a-z])", low):
            return ADR
        return None

    def verdict(self, expression: str) -> tuple[int, str]:
        """(verdict, the license id that decided it) for an SPDX expression or free text."""
        text = (expression or "").strip()
        if not text or not re.fullmatch(r"[A-Za-z0-9.+()\- ]+", text):
            # empty, a URL or a file name is not an SPDX expression; a URL or text that names a denied license is still denied
            found = self.free_text(text)
            return (found, text) if found is not None else (UNKNOWN, text)
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
            plain = token[:-1] if token.endswith("+") else token
            level, decided = self.leaf(plain), plain
            if pos < len(tokens) and tokens[pos].upper() == "WITH":
                if pos + 1 >= len(tokens):
                    raise ValueError("WITH without an exception")
                exception = tokens[pos + 1]
                pos += 2
                # an exception never lifts the base license (the worse verdict stays); an exception that is itself a denied
                # family (Commons-Clause) denies the term
                if self.leaf(exception) == DENIED:
                    level, decided = DENIED, f"{plain} WITH {exception}"
            return level, decided

        try:
            result = parse_or()
            if pos != len(tokens):
                raise ValueError("trailing tokens")
        except ValueError:
            # not parseable: never allowed, and a denied name inside it is still named
            return (DENIED if self.free_text(text) == DENIED else UNKNOWN), text
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
        vendor = entry.get("vendor_terms", False)
        if not isinstance(vendor, bool):
            raise CheckError(f"{path}: override for {self.package} has a 'vendor_terms' that is not true or false")
        self.vendor_terms = vendor  # the license is a vendor's own terms (not SPDX), reviewed and accepted by name
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


ADR_ACCEPTED = re.compile(r"^[\s|*>#-]*status\b[\s*:|]*accepted\b", re.IGNORECASE | re.MULTILINE)


def adr_names(adr_dir: Path, package: str) -> bool | None:
    """True when an ADR with `Status: Accepted` names the package as a whole token and names MPL-2.0; False when none does;
    None when docs/adr/ is missing. The id `Foo` is not found in `Foo.Bar` or `My-Foo`; a final full stop is not part of it."""
    if not adr_dir.is_dir():
        return None
    name = re.compile(r"(?<![A-Za-z0-9._-])" + re.escape(package) + r"(?![A-Za-z0-9_-])(?!\.[A-Za-z0-9])", re.IGNORECASE)
    for adr in sorted(adr_dir.glob("*.md")):
        text = adr.read_text(encoding="utf-8", errors="replace")
        if ADR_ACCEPTED.search(text) and "MPL-2.0" in text and name.search(text):
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


def scope_declares_packages(projects: list[Path]) -> bool:
    """True when a project of the scope names a package (a PackageReference, or a package in its restored assets), so an
    empty tool list cannot be right. A scope of project references only (csharp/tools, csharp/samples today) is empty."""
    for project in projects:
        if "PackageReference" in project.read_text(encoding="utf-8", errors="replace"):
            return True
        assets = project.parent / "obj" / "project.assets.json"
        if assets.is_file() and '"type": "package"' in assets.read_text(encoding="utf-8", errors="replace"):
            return True
    return False


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
        # the tool exits 0 on success (checked on the real tree), so any other exit is an environment error, output or not
        if done.returncode != 0 or not output.is_file():
            raise CheckError(f"nuget-license exited {done.returncode} for scope '{scope}'"
                             f"{'' if output.is_file() else ' and wrote no output'}: {(done.stderr or done.stdout).strip()[:400]}")
        packages = parse_tool_output(output.read_text(encoding="utf-8"), scope)
        if not packages and scope_declares_packages(projects):
            raise CheckError(f"nuget-license read no packages for scope '{scope}' ({len(projects)} project(s)): "
                             "the projects name packages, so was the solution restored? An empty list would pass everything")
        return packages


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
            read_level = policy.verdict(declared)[0]
            if override is not None:
                override.used = True
            if override is not None and read_level in (DENIED, ADR):
                # an override fixes a license the tool could not read; it never lifts one the tool read as refused
                level, decided = read_level, ""
                why = ("override cannot lift a denied license (the tool read "
                       f"'{declared}', which is {'denied' if read_level == DENIED else 'accepted only with an ADR'}); "
                       "remove the override (FR-08)")
            else:
                if override is not None:
                    shown = f"{override.license} (override)"
                    declared = override.license
                level, decided = policy.verdict(declared)
                if override is not None and level == UNKNOWN:
                    if override.vendor_terms:
                        level = OK  # a reviewed vendor license: the override carries the reason and the flag
                    else:
                        why = ("override license is not an allowed SPDX id; write the real SPDX id, or set "
                               "\"vendor_terms\": true for a reviewed vendor license (FR-08)")
            if why is not None:
                pass
            elif level == DENIED:
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
