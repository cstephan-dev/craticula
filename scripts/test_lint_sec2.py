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


def located(result: subprocess.CompletedProcess) -> list[str]:
    """Each finding as `file:line: SEC-2: expression [key: Member | position]`, its reason left out."""
    out = []
    for finding in findings(result):
        head, _, rest = finding.partition(" — ")
        out.append(f"{head} [{rest.rsplit(' [', 1)[1]}")
    return out


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

    # -- the allowlist trusts one position, never a whole flow (review S1) ----------------------------------------

    def test_trust_on_the_validating_function_still_checks_every_caller_of_a_helper(self):
        # ResolvePath's return is trusted; FieldReference's parameter is traced to each call site, so the caller
        # that skips ResolvePath is reported where its raw value enters.
        case = "transitive-trust"
        result = lint(case, "Craticula.Mongo", allowlist=FIXTURES / case / "allowlist.txt")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        raw = line_of(case, "Craticula.Mongo/FieldPaths.cs", "var path = request.ColId;")
        self.assertEqual(
            [f"Craticula.Mongo/FieldPaths.cs:{raw}: SEC-2: request.ColId [key: FieldPaths.Raw | value of local path]"],
            located(result))

    def test_a_trust_entry_on_a_parameter_as_passed_in_is_refused(self):
        case = "transitive-trust"
        result = lint(case, "Craticula.Mongo", allowlist=FIXTURES / case / "parameter-allowlist.txt")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        hole = line_of(case, "Craticula.Mongo/FieldPaths.cs", '=> $"${path}";')
        raw = line_of(case, "Craticula.Mongo/FieldPaths.cs", "var path = request.ColId;")
        self.assertEqual(
            [f"Craticula.Mongo/FieldPaths.cs:{hole}: SEC-2: path [key: FieldPaths.FieldReference | interpolation hole]",
             f"Craticula.Mongo/FieldPaths.cs:{raw}: SEC-2: request.ColId [key: FieldPaths.Raw | value of local path]"],
            located(result))
        self.assertIn("trusts every call site, present and future", result.stdout)

    def test_a_trust_entry_covers_its_own_position_not_the_same_text_elsewhere_in_the_member(self):
        # The entry trusts `value` iterated into `character`; the same `value` appended whole is checked again.
        case = "escaper-position"
        result = lint(case, "Craticula.Mongo", allowlist=FIXTURES / case / "allowlist.txt")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        whole = line_of(case, "Craticula.Mongo/MongoRegexEscaping.cs", "escaped.Append(value);")
        self.assertEqual(
            [f"Craticula.Mongo/MongoRegexEscaping.cs:{whole}: SEC-2: value "
             f"[key: MongoRegexEscaping.Escape | StringBuilder.Append argument]"],
            located(result))
        self.assertIn("1 finding(s) and 0 stale allowlist entries", result.stdout)  # the loop's entry still matched

    def test_a_not_query_text_member_is_silenced_and_its_result_in_query_text_fails(self):
        case = "not-query-text"
        listed = lint(case, "Craticula.Dapper", allowlist=FIXTURES / case / "allowlist.txt")
        self.assertEqual(1, listed.returncode, listed.stdout + listed.stderr)
        lookup = line_of(case, "Craticula.Dapper/ValueReader.cs", "SELECT * FROM t WHERE c = '")
        self.assertEqual(
            [f"Craticula.Dapper/ValueReader.cs:{lookup}: SEC-2: Truncate(userValue) "
             f"[key: ValueReader.Lookup | concatenation operand]"],
            located(listed))
        self.assertIn("declared never query text (allowlist line 2)", listed.stdout)

        # Without the entry Truncate's own concatenation is query text built from a value the lint cannot trace.
        unlisted = lint(case, "Craticula.Dapper")
        body = line_of(case, "Craticula.Dapper/ValueReader.cs", "Convert.ToString(value")
        self.assertIn(f"Craticula.Dapper/ValueReader.cs:{body}: SEC-2: Convert.ToString(", unlisted.stdout)

    def test_a_not_query_text_entry_that_silences_nothing_is_stale(self):
        case = "not-query-text"
        result = lint(case, "Craticula.Dapper", allowlist=FIXTURES / case / "stale-allowlist.txt")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        self.assertIn("stale-allowlist.txt:3: SEC-2: stale allowlist entry not-query-text "
                      "Craticula.Dapper/ValueReader.cs::ValueReader.ToInt64", result.stdout)

    def test_an_entry_whose_file_is_missing_or_outside_the_scope_fails_and_a_partial_run_skips_the_rest(self):
        case = "stale-scope"
        result = lint(case, "--stats", "Craticula.Sql", allowlist=FIXTURES / case / "allowlist.txt")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        stale = [line for line in findings(result) if "stale allowlist entry" in line]
        self.assertEqual(2, len(stale), result.stdout)
        self.assertIn("allowlist.txt:2: SEC-2: stale allowlist entry trust Craticula.Sql/Missing.cs", stale[0])
        self.assertIn("its file does not exist", stale[0])
        self.assertIn("allowlist.txt:3: SEC-2: stale allowlist entry trust Craticula.Core/Outside.cs", stale[1])
        self.assertIn("outside the files the lint scans", stale[1])
        self.assertIn("1 not judged (outside the given paths)", result.stdout)

    # -- types hidden by `var`, and statements handed to the database (review S2) ---------------------------------

    def test_a_var_local_takes_the_string_type_of_its_initializer(self):
        case = "var-concatenation"
        result = lint(case, "Craticula.Dapper")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        file = "Craticula.Dapper/Statements.cs"
        concatenated = line_of(case, file, "var sql = head + request.Value;")
        appended = line_of(case, file, "sql += request.Value;")
        self.assertEqual(
            [f"{file}:{concatenated}: SEC-2: request.Value [key: Statements.Concatenated | concatenation operand]",
             f"{file}:{appended}: SEC-2: request.Value [key: Statements.Appended | concatenation operand]"],
            located(result))

    def test_a_raw_value_handed_to_a_database_command_fails_and_the_compiled_statement_passes(self):
        case = "command-sinks"
        result = lint(case, "Craticula.Dapper")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        file = "Craticula.Dapper/Commands.cs"
        expected = [
            ("new CommandDefinition(request.Value)", "Commands.Definition | command text of new CommandDefinition"),
            ("command.CommandText = request.Value", "Commands.Text | CommandText assignment"),
            ("new SqlCommand(request.Value", "Commands.Constructed | command text of new SqlCommand"),
            ("{ CommandText = request.Value }", "Commands.Initialized | CommandText assignment"),
            ("ExecuteSqlRaw(request.Value)", "Commands.Raw | SQL text of ExecuteSqlRaw"),
            ("FromSqlRaw(request.Value)", "Commands.FromRaw | SQL text of FromSqlRaw"),
            ("ExecuteAsync(request.Value)", "Commands.Dapper | SQL text of ExecuteAsync"),
        ]
        self.assertEqual(
            [f"{file}:{line_of(case, file, needle)}: SEC-2: request.Value [key: {key}]" for needle, key in expected],
            located(result))

    # -- calls the lint used to miss or trust by name (review S3) ------------------------------------------------

    def test_a_method_group_is_a_call_site_whose_arguments_cannot_be_traced(self):
        # WhereClause has one safe direct call; ConvertAll hands it raw columns. Record members and properties that
        # share a method's name are not method groups.
        case = "method-group"
        result = lint(case, "Craticula.Sql")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        group = line_of(case, "Craticula.Sql/Clauses.cs", "ConvertAll(WhereClause)")
        self.assertEqual(
            [f"Craticula.Sql/Clauses.cs:{group}: SEC-2: WhereClause [key: Clauses.Unsafe | delegate of WhereClause]"],
            located(result))

    def test_an_inherited_or_unresolved_unqualified_call_is_a_call_site(self):
        case = "inherited-call"
        result = lint(case, "Craticula.Sql")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        file = "Craticula.Sql/Ordering.cs"
        lines = [number for number, text in enumerate(
            (FIXTURES / case / file).read_text(encoding="utf-8").splitlines(), 1) if "Order(userColumn)" in text]
        self.assertEqual(
            [f"{file}:{lines[0]}: SEC-2: userColumn [key: UserOrdering.Unsafe | argument column of Order]",
             f"{file}:{lines[1]}: SEC-2: userColumn [key: FrameworkOrdering.Unsafe | argument column of Order]"],
            located(result))

    def test_only_the_listed_escapers_are_trusted_never_a_framework_escape_method(self):
        case = "framework-escape"
        result = lint(case, "Craticula.Search")
        self.assertEqual(1, result.returncode, result.stdout + result.stderr)
        line = line_of(case, "Craticula.Search/Scripts.cs", "Uri.EscapeDataString(userValue)")
        self.assertEqual(
            [f"Craticula.Search/Scripts.cs:{line}: SEC-2: Uri.EscapeDataString(userValue) "
             f"[key: Scripts.PriceAbove | concatenation operand]"],
            located(result))


if __name__ == "__main__":
    unittest.main()
