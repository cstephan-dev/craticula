#!/usr/bin/env python3
"""Pins the coverage gate (scripts/coverage.py) offline with synthetic cobertura files (story #1657).

Each folder under scripts/coverage-fixtures/ is one complete results dir. Run: python3 scripts/test_coverage.py
(also run by Craticula.Conformance.Tests/CoverageGateTests.cs on every `dotnet test`).
"""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "coverage.py"
FIXTURES = HERE / "coverage-fixtures"


def gate(results: Path, *extra: str) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(SCRIPT), str(results), *extra],
        capture_output=True, text=True, timeout=30,
    )


def row(output: str, assembly: str) -> str:
    return next(line for line in output.splitlines() if line.startswith(assembly + " "))


class CoverageGateTests(unittest.TestCase):
    def test_same_file_under_three_spellings_merges_to_the_union(self):
        # Core's Model.cs is reported absolute, relative and backslash/package-less by three projects,
        # each covering other lines: keyed by raw filename it would be 3 files at 50 % or less.
        result = gate(FIXTURES / "basename-merge")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        core = row(result.stdout, "Craticula.Core")
        self.assertIn("10/10", core)
        self.assertIn("100.0%", core)
        self.assertIn("PASS", core)

    def test_branch_coverage_keeps_the_best_ratio_per_line(self):
        result = gate(FIXTURES / "basename-merge")
        # line 3: 1/2 in one project, 2/2 in the other -> 2/2, not 3/4 and not 1/2
        self.assertRegex(row(result.stdout, "Craticula.Core"), r"100\.0%\s+100\.0%\s+PASS")

    def test_exactly_90_percent_passes(self):
        result = gate(FIXTURES / "exactly-90")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("90/100", row(result.stdout, "Craticula.Core"))
        self.assertIn("90.0%", row(result.stdout, "Craticula.Core"))

    def test_89_9_percent_fails_and_names_the_assembly(self):
        result = gate(FIXTURES / "just-below")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("899/1000", row(result.stdout, "Craticula.Core"))
        self.assertIn("FAIL", row(result.stdout, "Craticula.Core"))
        self.assertIn("FAIL: below 90% lines: Craticula.Core", result.stdout)
        self.assertNotIn("Craticula.AgGrid", result.stdout.splitlines()[-1])

    def test_threshold_option_moves_the_line(self):
        self.assertEqual(0, gate(FIXTURES / "just-below", "--threshold", "89.9").returncode)
        self.assertEqual(1, gate(FIXTURES / "exactly-90", "--threshold", "90.1").returncode)
        self.assertEqual(2, gate(FIXTURES / "exactly-90", "--threshold", "101").returncode)

    def test_missing_in_scope_assembly_is_an_environment_error(self):
        result = gate(FIXTURES / "missing-assembly")
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn("in-scope assembly missing", result.stderr)
        self.assertIn("Craticula.Search", result.stderr)

    def test_out_of_scope_assemblies_are_ignored(self):
        result = gate(FIXTURES / "out-of-scope-ignored")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertNotIn("EntityFrameworkCore", result.stdout)
        self.assertNotIn("Conformance.Driver", result.stdout)

    def test_no_cobertura_files_is_an_environment_error(self):
        with tempfile.TemporaryDirectory() as empty:
            result = gate(Path(empty))
        self.assertEqual(2, result.returncode)
        self.assertIn("no coverage.cobertura.xml", result.stderr)


if __name__ == "__main__":
    unittest.main()
