#!/usr/bin/env python3
"""Pins the SEC-2 lint (scripts/lint-sec2.py) offline with synthetic C# files (story #1660, AC-4).

Each folder under scripts/lint-sec2-fixtures/ is one tiny source tree linted with --root; its project folder
(Craticula.Sql, Craticula.Mongo, ...) decides the profile. Run: python3 scripts/test_lint_sec2.py
(also run by Craticula.Conformance.Tests/Sec2LintTests.cs on every `dotnet test`).
"""
import subprocess
import sys
import tempfile
import unittest
from pathlib import Path

HERE = Path(__file__).resolve().parent
SCRIPT = HERE / "lint-sec2.py"
FIXTURES = HERE / "lint-sec2-fixtures"
NO_ALLOWLIST = FIXTURES / "no-allowlist.txt"


def lint(case: str, *paths: str, allowlist: Path = NO_ALLOWLIST) -> subprocess.CompletedProcess:
    return subprocess.run(
        [sys.executable, str(SCRIPT), "--root", str(FIXTURES / case), "--allowlist", str(allowlist), *paths],
        capture_output=True, text=True, encoding="utf-8", timeout=30,
    )


def line_of(case: str, file: str, needle: str) -> int:
    """The 1-based line of the first line of a fixture file that contains `needle`."""
    lines = (FIXTURES / case / file).read_text(encoding="utf-8").splitlines()
    return next(number for number, text in enumerate(lines, 1) if needle in text)


def findings(result: subprocess.CompletedProcess) -> list[str]:
    return [line for line in result.stdout.splitlines() if ": SEC-2: " in line]


class Sec2LintTests(unittest.TestCase):
    def test_a_raw_value_interpolated_into_sql_text_fails_with_file_and_line(self):
        result = lint("raw-value-interpolated", "Craticula.Sql")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        line = line_of("raw-value-interpolated", "Craticula.Sql/RawInterpolation.cs", "{request.Value}")
        self.assertEqual(
            [f"Craticula.Sql/RawInterpolation.cs:{line}: SEC-2: request.Value"],
            [f.split(" — ")[0] for f in findings(result)])

    def test_a_raw_value_concatenated_with_plus_fails(self):
        # The quoted table name in the same chain passes; only the client's value is reported.
        result = lint("raw-value-concatenated", "Craticula.Sql")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        line = line_of("raw-value-concatenated", "Craticula.Sql/RawConcatenation.cs", "+ request.Name +")
        self.assertEqual(
            [f"Craticula.Sql/RawConcatenation.cs:{line}: SEC-2: request.Name"],
            [f.split(" — ")[0] for f in findings(result)])

    def test_a_raw_value_appended_to_a_string_builder_fails(self):
        result = lint("raw-value-appended", "Craticula.Sql")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        line = line_of("raw-value-appended", "Craticula.Sql/RawAppend.cs", "sql.Append(sortColumn)")
        self.assertIn(f"Craticula.Sql/RawAppend.cs:{line}: SEC-2: sortColumn", result.stdout)

    def test_bind_quoted_identifiers_literals_enums_and_constants_pass(self):
        result = lint("safe-holes", "Craticula.Sql")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], findings(result))
        self.assertIn("PASS: SEC-2 lint, 1 files", result.stdout)

    def test_an_allowlisted_expression_passes_and_the_same_expression_unlisted_fails(self):
        listed = lint("allowlisted", "Craticula.Sql", allowlist=FIXTURES / "allowlisted" / "allowlist.txt")
        self.assertEqual(0, listed.returncode, listed.stdout + listed.stderr)
        self.assertIn("1 allowlisted", listed.stdout)

        unlisted = lint("allowlisted", "Craticula.Sql")
        self.assertEqual(1, unlisted.returncode, unlisted.stdout + unlisted.stderr)
        line = line_of("allowlisted", "Craticula.Sql/QuotingDialect.cs", "identifier.Replace(")
        self.assertIn(
            f"Craticula.Sql/QuotingDialect.cs:{line}: SEC-2: "
            'identifier.Replace("\\"", "\\"\\"", StringComparison.Ordinal)',
            unlisted.stdout)

    def test_a_stale_allowlist_entry_fails(self):
        result = lint("safe-holes", "Craticula.Sql", allowlist=FIXTURES / "safe-holes" / "stale-allowlist.txt")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("stale-allowlist.txt:2: SEC-2: stale allowlist entry", result.stdout)

    def test_exception_messages_are_not_query_text(self):
        # Interpolated, concatenated and target-typed `new(...)` messages, all over untraceable parameters.
        result = lint("exception-message", "Craticula.Sql")
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertEqual([], findings(result))

    def test_an_unsafe_argument_is_reported_at_the_call_site_not_in_the_helper(self):
        result = lint("parameter-flow", "Craticula.Sql")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        call_site = line_of("parameter-flow", "Craticula.Sql/ParameterFlow.cs", "Equal(leaf.Field,")
        helper = line_of("parameter-flow", "Craticula.Sql/ParameterFlow.cs", "{quotedColumn} = {placeholder}")
        self.assertEqual(1, len(findings(result)), result.stdout)
        self.assertIn(f"Craticula.Sql/ParameterFlow.cs:{call_site}: SEC-2: leaf.Field", result.stdout)
        self.assertIn(f"reaches query text at Craticula.Sql/ParameterFlow.cs:{helper}", result.stdout)

    def test_a_local_is_judged_by_every_value_assigned_to_it(self):
        result = lint("local-reassigned", "Craticula.Sql")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        line = line_of("local-reassigned", "Craticula.Sql/Reassigned.cs", "operand = userValue;")
        self.assertEqual(1, len(findings(result)), result.stdout)
        self.assertIn(f"Craticula.Sql/Reassigned.cs:{line}: SEC-2: userValue", result.stdout)

    def test_a_method_borrowing_a_primitive_name_is_checked_with_its_parameters_untrusted(self):
        result = lint("primitive-squatter", "Craticula.Sql")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        line = line_of("primitive-squatter", "Craticula.Sql/Squatter.cs", "Bind(string value) => value;")
        self.assertIn(f"Craticula.Sql/Squatter.cs:{line}: SEC-2: value", result.stdout)
        self.assertIn("primitive Bind", result.stdout)

    def test_an_escaped_pattern_is_a_finding_in_sql_text_and_a_value_on_mongo(self):
        sql = lint("escape-profiles", "Craticula.Sql")
        self.assertEqual(1, sql.returncode, sql.stdout + sql.stderr)
        self.assertIn("SqlPatternEscaping.Escape(value, '\\\\')", sql.stdout)
        self.assertIn("bind it (SEC-2, SEC-4)", sql.stdout)

        mongo = lint("escape-profiles", "Craticula.Mongo")
        self.assertEqual(0, mongo.returncode, mongo.stdout + mongo.stderr)

    def test_compiled_query_text_passes_and_an_arbitrary_sql_member_fails(self):
        result = lint("compiled-query", "Craticula.Dapper")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        line = line_of("compiled-query", "Craticula.Dapper/CountWrap.cs", "({query.Sql})")
        self.assertEqual(
            [f"Craticula.Dapper/CountWrap.cs:{line}: SEC-2: query.Sql"],
            [f.split(" — ")[0] for f in findings(result)])

    def test_a_missing_or_empty_scope_is_an_environment_error(self):
        missing = lint("safe-holes", "Craticula.Missing")
        self.assertEqual(2, missing.returncode, missing.stdout + missing.stderr)
        self.assertIn("scope path not found", missing.stderr)

        with tempfile.TemporaryDirectory() as empty:
            result = subprocess.run(
                [sys.executable, str(SCRIPT), "--root", empty, "--allowlist", str(NO_ALLOWLIST), "."],
                capture_output=True, text=True, encoding="utf-8", timeout=30)
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn("no .cs file under the scope path", result.stderr)

    def test_a_malformed_or_missing_allowlist_is_an_environment_error(self):
        malformed = lint("safe-holes", "Craticula.Sql", allowlist=FIXTURES / "malformed-allowlist.txt")
        self.assertEqual(2, malformed.returncode, malformed.stdout + malformed.stderr)
        self.assertIn("malformed-allowlist.txt:2: malformed entry", malformed.stderr)

        missing = lint("safe-holes", "Craticula.Sql", allowlist=FIXTURES / "no-such-allowlist.txt")
        self.assertEqual(2, missing.returncode, missing.stdout + missing.stderr)
        self.assertIn("cannot read the allowlist", missing.stderr)

    def test_a_file_the_lint_cannot_parse_fails_closed(self):
        result = lint("unparseable", "Craticula.Sql")
        self.assertEqual(2, result.returncode, result.stdout + result.stderr)
        self.assertIn("Craticula.Sql/Broken.cs: cannot parse", result.stderr)


if __name__ == "__main__":
    unittest.main()
