#!/usr/bin/env python3
"""Pins the license policy check (scripts/check-licenses.py; ADR-0003, spec FR-08, FR-09, FR-14, NFR-03, SC-04) offline.

Each folder under scripts/license-fixtures/ is one case: the JSON that `nuget-license -o Json` prints per scope, read with
--json-dir, so no tool, no network and no restored project is needed. Run: python3 scripts/test_check_licenses.py
(also run by Craticula.Conformance.Tests/LicenseCheckTests.cs on every `dotnet test`).
"""
import json
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "check-licenses.py"
FIXTURES = HERE / "license-fixtures"
OVERRIDES = FIXTURES / "overrides"


def check(case: str, overrides: str = "none.json", adr: str | None = None, root: Path | None = None) -> subprocess.CompletedProcess:
    command = [sys.executable, str(SCRIPT), "--json-dir", str(FIXTURES / case), "--overrides", str(OVERRIDES / overrides),
               "--adr-dir", str(FIXTURES / case / (adr or "adr"))]
    if root is not None:
        command += ["--root", str(root)]
    return subprocess.run(command, capture_output=True, text=True, encoding="utf-8", timeout=30)


def pkg(package: str, license_text: str | None, version: str = "1.0.0") -> dict:
    entry = {"PackageId": package, "PackageVersion": version, "LicenseInformationOrigin": 0}
    if license_text is not None:
        entry["License"] = license_text
    return entry


def check_tmp(packages: list[dict], overrides: list[dict] | None = None, adrs: dict[str, str] | None = None,
              scope: str = "tests") -> subprocess.CompletedProcess:
    """Run the checker on packages and override entries written to a temp folder (cases that need no fixture folder)."""
    with tempfile.TemporaryDirectory() as tmp:
        base = Path(tmp)
        (base / "json").mkdir()
        (base / "json" / f"{scope}.json").write_text(json.dumps(packages), encoding="utf-8")
        (base / "ov.json").write_text(json.dumps({"overrides": overrides or []}), encoding="utf-8")
        adr = base / "adr"
        if adrs is not None:
            adr.mkdir()
            for name, text in adrs.items():
                (adr / name).write_text(text, encoding="utf-8")
        return subprocess.run([sys.executable, str(SCRIPT), "--json-dir", str(base / "json"), "--overrides", str(base / "ov.json"),
                               "--adr-dir", str(adr)], capture_output=True, text=True, encoding="utf-8", timeout=30)


def override(package: str, license_text: str, **extra) -> dict:
    return {"package": package, "version": "1.0.0", "license": license_text, "reason": "Synthetic reason.", **extra}


def failures(result: subprocess.CompletedProcess) -> list[str]:
    return [line for line in result.stdout.splitlines() if not line.startswith("check-licenses:")]


class LicenseCheckTests(unittest.TestCase):
    def assert_fails(self, result, *needles: str):
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        text = "\n".join(failures(result))
        for needle in needles:
            self.assertIn(needle, text)

    def test_a_clean_tree_passes(self):
        result = check("clean")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], failures(result))

    # FR-08 / AC13: a denied family fails and the line names package, version, license and where it sits.
    def test_every_denied_family_fails_naming_package_version_and_license(self):
        for case, license_id in [("gpl", "GPL-3.0-only"), ("lgpl", "LGPL-2.1-or-later"), ("agpl", "AGPL-3.0-only"),
                                 ("sspl", "SSPL-1.0"), ("busl", "BUSL-1.1"), ("elastic", "Elastic-2.0"),
                                 ("commons-clause", "Commons-Clause"), ("non-commercial", "CC-BY-NC-4.0")]:
            with self.subTest(license=license_id):
                result = check(case)
                self.assert_fails(result, "Example.Bad", "4.5.6", f"'{license_id}'", "denied", "tests scope")

    def test_a_compound_expression_needs_every_side_of_an_AND_and_one_side_of_an_OR(self):
        self.assert_fails(check("and-expression"), "MIT AND GPL-2.0-only")
        self.assertEqual(0, check("clean").returncode)  # "MIT OR GPL-2.0-only" in the clean case

    def test_a_license_the_tool_could_not_read_fails_and_points_to_the_overrides_file(self):
        self.assert_fails(check("unknown-license"), "Example.Unknown", "1.0.0", "license-overrides.json")

    def test_a_license_url_that_names_a_gnu_license_is_denied_not_just_unknown(self):
        self.assert_fails(check("gnu-url"), "Example.Bad", "denied", "agpl.html")

    def test_the_failure_line_names_the_project_read_from_the_assets_file(self):
        with tempfile.TemporaryDirectory() as tmp:
            project = Path(tmp) / "csharp" / "tests" / "Example.Widget.Tests"
            (project / "obj").mkdir(parents=True)
            (project / "Example.Widget.Tests.csproj").write_text("<Project />", encoding="utf-8")
            (project / "obj" / "project.assets.json").write_text(
                json.dumps({"libraries": {"Example.Bad/4.5.6": {"type": "package"}}}), encoding="utf-8")
            result = check("gpl", root=Path(tmp))
        self.assert_fails(result, "Example.Bad 4.5.6", "[project: Example.Widget.Tests]")

    # FR-09 / AC14.
    def test_mpl_without_an_adr_that_names_the_package_fails(self):
        self.assert_fails(check("mpl-no-adr"), "Example.Mpl", "MPL-2.0", "ADR", "none does")

    def test_mpl_fails_closed_when_the_adr_folder_is_missing(self):
        result = check("mpl-adr-absent")
        self.assert_fails(result, "Example.Mpl", "is missing", "fails closed")

    def test_mpl_passes_when_an_adr_names_the_package(self):
        result = check("mpl-with-adr")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    # AC15: the named trap, whatever license the package declares and even with an override.
    def test_mysql_data_is_rejected_by_name_with_the_connector_hint(self):
        self.assert_fails(check("mysql-data"), "MySql.Data 9.0.0", "use MySqlConnector")
        self.assert_fails(check("mysql-data-permissive-label"), "use MySqlConnector")
        self.assert_fails(check("mysql-data", overrides="mysql.json"), "use MySqlConnector", "stale override")

    # AC16: the Oracle driver and Testcontainers.Oracle are test-project packages only.
    def test_the_oracle_driver_passes_under_tests_and_fails_under_src_tools_and_samples(self):
        result = check("oracle-in-tests", overrides="oracle.json")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assert_fails(check("oracle-in-src", overrides="oracle.json"),
                          "src: Oracle.ManagedDataAccess.Core 23.0.0", "test projects under csharp/tests", "stale override")
        self.assert_fails(check("oracle-in-tools", overrides="oracle.json"), "other: Oracle.ManagedDataAccess.Core")

    def test_testcontainers_oracle_fails_under_src_even_with_an_allowed_license(self):
        self.assert_fails(check("testcontainers-oracle-in-src"), "Testcontainers.Oracle 4.0.0", "'MIT'", "test projects")

    def test_the_oracle_driver_without_an_override_fails_as_an_unreadable_license(self):
        self.assert_fails(check("oracle-in-tests"), "Oracle.ManagedDataAccess.Core", "LICENSE.txt")

    # Overrides: a reason is mandatory, a stale entry fails, a denied license cannot be laundered.
    def test_an_override_without_a_reason_is_an_error(self):
        for name in ("no-reason.json", "missing-reason.json", "blank-reason.json"):
            with self.subTest(overrides=name):
                result = check("oracle-in-tests", overrides=name)
                self.assertEqual(2, result.returncode, result.stdout + result.stderr)
                self.assertIn("reason", result.stderr)
                self.assertIn("Oracle.ManagedDataAccess.Core", result.stderr)

    def test_an_override_that_matches_no_package_is_stale_and_fails(self):
        self.assert_fails(check("clean", overrides="stale.json"), "Example.Gone 9.9.9", "stale override")

    def test_an_override_for_another_version_does_not_apply_and_is_stale(self):
        self.assert_fails(check("oracle-in-tests", overrides="wrong-version.json"),
                          "Oracle.ManagedDataAccess.Core 23.0.0", "stale override")

    def test_an_override_fixes_a_license_the_tool_misread(self):
        result = check("unknown-license", overrides="unknown-override.json")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_an_override_limited_to_other_scopes_does_not_apply(self):
        # the unknown-license case sits in the tests scope; this override is for src only, so it is neither used nor accepted
        result = check("unknown-license", overrides="src-only.json")
        self.assert_fails(result, "Example.Unknown", "license-overrides.json", "stale override")

    def test_an_override_with_an_unknown_scope_is_an_error(self):
        result = check("unknown-license", overrides="bad-scopes.json")
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn("scopes", result.stderr)

    def test_an_override_cannot_launder_a_denied_license(self):
        self.assert_fails(check("gpl", overrides="gpl-laundering.json"), "Example.Bad", "denied")

    # F1: an override must not launder a refused license.
    def test_an_override_cannot_lift_what_the_tool_read_as_denied_or_needing_an_adr(self):
        for replacement in ("MIT", "GNU General Public License v3.0", "Business Source License 1.1", "Server Side Public License",
                            "Apache-2.0 WITH Commons-Clause"):
            with self.subTest(replacement=replacement):
                result = check_tmp([pkg("Example.Bad", "GPL-3.0-only")], [override("Example.Bad", replacement)])
                self.assert_fails(result, "Example.Bad", "GPL-3.0-only", "override cannot lift a denied license")
        result = check("mpl-no-adr", overrides="mozilla.json")
        self.assert_fails(result, "Example.Mpl", "MPL-2.0", "override cannot lift a denied license")

    def test_an_override_license_that_is_denied_prose_or_needs_an_adr_is_judged_by_the_policy(self):
        for text in ("GNU General Public License v3.0", "GNU Affero General Public License", "Business Source License 1.1",
                     "Server Side Public License", "Elastic License 2.0", "Apache-2.0 WITH Commons-Clause",
                     "Commons Clause License", "Non Commercial Use Only", "non-commercial terms", "Mozilla Public License 2.0"):
            with self.subTest(license=text):
                result = check_tmp([pkg("Example.Odd", "https://example.invalid/license.txt")], [override("Example.Odd", text)], adrs=None)
                self.assertEqual(1, result.returncode, result.stdout + result.stderr)

    def test_an_override_with_a_non_spdx_license_needs_the_vendor_terms_flag(self):
        packages = [pkg("Example.Odd", "https://example.invalid/license.txt")]
        self.assert_fails(check_tmp(packages, [override("Example.Odd", "Vendor free-use terms")]), "Example.Odd", "vendor_terms")
        accepted = check_tmp(packages, [override("Example.Odd", "Vendor free-use terms", vendor_terms=True)])
        self.assertEqual(0, accepted.returncode, accepted.stdout + accepted.stderr)
        # the flag is no way round the denied scan
        self.assert_fails(check_tmp(packages, [override("Example.Odd", "Business Source License 1.1", vendor_terms=True)]), "denied")

    def test_a_with_exception_never_makes_a_denied_license_allowed_and_a_denied_exception_denies(self):
        for expression in ("GPL-3.0-only WITH Classpath-exception-2.0", "Apache-2.0 WITH Commons-Clause"):
            with self.subTest(expression=expression):
                self.assert_fails(check_tmp([pkg("Example.Bad", expression)]), "Example.Bad", "denied")
        ok = check_tmp([pkg("Example.Fine", "Apache-2.0 WITH LLVM-exception")])
        self.assertEqual(0, ok.returncode, ok.stdout + ok.stderr)

    # N1: parser edge cases.
    def test_a_plus_suffix_and_trailing_junk(self):
        self.assertEqual(0, check_tmp([pkg("Example.Fine", "MIT+")]).returncode)
        self.assert_fails(check_tmp([pkg("Example.Bad", "GPL-2.0+")]), "denied")
        self.assert_fails(check_tmp([pkg("Example.Bad", "MIT GPL-3.0")]), "Example.Bad")

    def test_a_non_commercial_marker_and_a_case_folded_override_match(self):
        self.assert_fails(check_tmp([pkg("Example.Bad", "Free for Non-Commercial use")]), "denied")
        accepted = check_tmp([pkg("Example.Odd", "https://example.invalid/license.txt")], [override("example.ODD", "Apache-2.0")])
        self.assertEqual(0, accepted.returncode, accepted.stdout + accepted.stderr)

    # F2: the MPL-2.0 ADR rule.
    def test_an_mpl_adr_must_be_accepted_name_the_package_as_a_whole_token_and_name_mpl(self):
        accepted = "# ADR-0010: accept Foo.Bar\n\nStatus: Accepted   Date: 2026-10-23\n\nFoo.Bar is MPL-2.0 and we do not modify it.\n"
        cases = {
            "accepted ADR naming Foo.Bar": (accepted, "Foo.Bar", True),
            "accepted for Foo.Bar is not for Foo": (accepted, "Foo", False),
            "case-folded status": (accepted.replace("Accepted", "accepted"), "Foo.Bar", True),
            "table status": (accepted.replace("Status: Accepted", "| Status | Accepted |"), "Foo.Bar", True),
            "sentence-final package name": (accepted.replace("Foo.Bar is MPL-2.0", "We accept MPL-2.0 for Foo.Bar."), "Foo.Bar", True),
            "Proposed": (accepted.replace("Accepted", "Proposed"), "Foo.Bar", False),
            "Superseded": (accepted.replace("Accepted", "Superseded by ADR-0011"), "Foo.Bar", False),
            "Rejected": (accepted.replace("Accepted", "Rejected"), "Foo.Bar", False),
            "Deprecated": (accepted.replace("Accepted", "Deprecated"), "Foo.Bar", False),
            "template status line": (accepted.replace("Status: Accepted", "Status: Proposed | Accepted | Deprecated"), "Foo.Bar", False),
            "policy ADR that mentions MPL-2.0 only": ("# ADR-0003\n\nStatus: Accepted\n\nMPL-2.0 needs an ADR per package.\n", "Foo.Bar", False),
            "no MPL token": (accepted.replace("MPL-2.0", "a weak copyleft license"), "Foo.Bar", False),
            "Microsoft.Data is not Microsoft.Data.SqlClient": (
                "Status: Accepted\nMicrosoft.Data.SqlClient is MPL-2.0.\n", "Microsoft.Data", False),
            "xunit is not xunit.abstractions": ("Status: Accepted\nxunit.abstractions is MPL-2.0.\n", "xunit", False),
            "prefix of a longer word": ("Status: Accepted\nOracleCloud is MPL-2.0.\n", "Oracle", False),
            "suffix of a longer id": ("Status: Accepted\nMy-Foo.Bar is MPL-2.0.\n", "Foo.Bar", False),
        }
        for label, (text, package, expected) in cases.items():
            with self.subTest(label):
                result = check_tmp([pkg(package, "MPL-2.0")], adrs={"0010-x.md": text})
                self.assertEqual(0 if expected else 1, result.returncode, result.stdout + result.stderr)

    # N2 / N3: the tool's exit and empty output.
    def test_a_tool_that_exits_non_zero_or_prints_an_empty_list_is_an_environment_error(self):
        for body, label in (("sys.exit(3)", "non-zero exit"), ("pass", "no output file"), ("write('[]')", "empty list"),
                            ("write('[]'); sys.exit(1)", "output and a failing exit")):
            with self.subTest(label), tempfile.TemporaryDirectory() as tmp:
                root = Path(tmp)
                for folder in ("src/Example.Lib", "tests", "tools"):
                    (root / "csharp" / folder).mkdir(parents=True, exist_ok=True)
                (root / "csharp" / "src" / "Example.Lib" / "Example.Lib.csproj").write_text(
                    '<Project><ItemGroup><PackageReference Include="Example.Dep" /></ItemGroup></Project>', encoding="utf-8")
                fake = root / "fake_tool.py"
                fake.write_text("import sys\nout = sys.argv[sys.argv.index('-fo') + 1]\n"
                                "def write(text):\n    open(out, 'w').write(text)\n" + body + "\n", encoding="utf-8")
                result = subprocess.run([sys.executable, str(SCRIPT), "--root", tmp, "--overrides", str(OVERRIDES / "none.json"),
                                         "--tool", f"{sys.executable} {fake}"], capture_output=True, text=True, timeout=30)
                self.assertEqual(2, result.returncode, result.stdout + result.stderr)

    def test_an_empty_list_is_fine_for_a_scope_whose_projects_name_no_package(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            for folder in ("src/Example.Lib", "tests", "tools/Example.Tool"):
                (root / "csharp" / folder).mkdir(parents=True, exist_ok=True)
            (root / "csharp" / "src" / "Example.Lib" / "Example.Lib.csproj").write_text(
                '<Project><ItemGroup><PackageReference Include="Example.Dep" /></ItemGroup></Project>', encoding="utf-8")
            (root / "csharp" / "tools" / "Example.Tool" / "Example.Tool.csproj").write_text("<Project />", encoding="utf-8")
            fake = root / "fake_tool.py"
            fake.write_text("import sys, json\nout = sys.argv[sys.argv.index('-fo') + 1]\nlist_file = json.load(open(sys.argv[sys.argv.index('-ji') + 1]))\n"
                            "pkgs = [{'PackageId': 'Example.Dep', 'PackageVersion': '1.0.0', 'License': 'MIT'}] if 'src' in list_file[0] else []\n"
                            "open(out, 'w').write(json.dumps(pkgs))\n", encoding="utf-8")
            # the tests scope has no project at all: that is the scope-level error, so give it one without packages
            (root / "csharp" / "tests" / "Example.T").mkdir()
            (root / "csharp" / "tests" / "Example.T" / "Example.T.csproj").write_text("<Project />", encoding="utf-8")
            result = subprocess.run([sys.executable, str(SCRIPT), "--root", tmp, "--overrides", str(OVERRIDES / "none.json"),
                                     "--tool", f"{sys.executable} {fake}"], capture_output=True, text=True, timeout=30)
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    # Environment errors exit 2.
    def test_a_malformed_policy_file_is_an_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            policy = Path(tmp) / "policy.json"
            policy.write_text("{\"allowed\": []}", encoding="utf-8")
            result = subprocess.run([sys.executable, str(SCRIPT), "--json-dir", str(FIXTURES / "clean"), "--allowlist", str(policy),
                                     "--overrides", str(OVERRIDES / "none.json")], capture_output=True, text=True, timeout=30)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn("malformed", result.stderr)

    def test_a_missing_tool_is_an_error_not_a_pass(self):
        with tempfile.TemporaryDirectory() as tmp:
            project = Path(tmp) / "csharp" / "src" / "Example.Lib"
            project.mkdir(parents=True)
            (project / "Example.Lib.csproj").write_text("<Project />", encoding="utf-8")
            for scope in ("tests", "tools"):
                (Path(tmp) / "csharp" / scope).mkdir(parents=True, exist_ok=True)
            result = subprocess.run([sys.executable, str(SCRIPT), "--root", tmp, "--overrides", str(OVERRIDES / "none.json"),
                                     "--tool", "nuget-license-that-does-not-exist"], capture_output=True, text=True, timeout=30)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)

    def test_a_project_outside_every_scope_is_an_error(self):
        with tempfile.TemporaryDirectory() as tmp:
            project = Path(tmp) / "csharp" / "extras" / "Example.Extra"
            project.mkdir(parents=True)
            (project / "Example.Extra.csproj").write_text("<Project />", encoding="utf-8")
            result = subprocess.run([sys.executable, str(SCRIPT), "--root", tmp, "--overrides", str(OVERRIDES / "none.json")],
                                    capture_output=True, text=True, timeout=30)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn("outside the license scopes", result.stderr)

    def test_the_real_override_file_and_policy_are_well_formed(self):
        data = json.loads((HERE / "license-overrides.json").read_text(encoding="utf-8"))
        for entry in data["overrides"]:
            self.assertTrue(entry["reason"].strip(), entry["package"])
        policy = json.loads((HERE / "license-allowlist.json").read_text(encoding="utf-8"))
        self.assertEqual({"MIT", "Apache-2.0", "BSD-2-Clause", "BSD-3-Clause", "ISC", "0BSD", "Unlicense", "PostgreSQL", "Zlib"},
                         set(policy["allowed"]))  # ADR-0003 allowed list
        self.assertEqual(["MPL-2.0"], policy["case_by_case"])


if __name__ == "__main__":
    unittest.main()
