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
