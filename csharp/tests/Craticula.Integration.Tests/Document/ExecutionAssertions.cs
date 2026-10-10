using System.Globalization;
using System.Text.Json;
using Craticula.Integration.Tests.Oracle;

namespace Craticula.Integration.Tests.Containers.Document;

/// <summary>The XEQ-1/XEQ-3/XEQ-4 verdict for one (fixture, engine), shared by the Mongo and Search tests.</summary>
internal static class ExecutionAssertions
{
    /// <summary>
    /// Runs <paramref name="execute"/> and holds its rows to what the fixture says this backend must do: the main
    /// result, an XEQ-4 <c>rows</c> override, or an XEQ-4 <c>error</c> (asserted on purpose, so a fix that makes the
    /// error disappear is noticed). Never edits an expectation and never loosens <see cref="RowComparer"/>.
    /// </summary>
    public static async Task VerifyAsync(
        ExecutionFixture fixture, string backend, string? engine, Func<Task<EngineRows>> execute)
    {
        var label = $"{fixture.Name} on {engine ?? backend}";
        var expectation = fixture.ExpectationFor(backend, engine);

        EngineRows actual;
        try
        {
            actual = await execute().ConfigureAwait(false);
        }
        catch (EngineRefusedException refusal)
        {
            if (expectation is ExpectError)
            {
                return;
            }

            Assert.Fail($"{label}: the engine/compiler REFUSED a query the fixture expects rows from: {refusal.Message}");
            return;
        }

        if (expectation is ExpectError error)
        {
            Assert.Fail(
                $"{label}: XEQ-4 declares an 'error' outcome ({error.Reason}) but the query returned {actual.Rows.Count} row(s). " +
                "Someone should notice and delete the allowedDivergence entry.");
            return;
        }

        var expected = ((ExpectRows)expectation).Result;
        var types = ColumnTypes.Resolve(fixture.Schema, fixture.Request, expected.Columns);
        var problems = RowComparer.Compare(expected, types, actual.Columns, actual.Rows);
        if (problems.Count > 0)
        {
            Assert.Fail(
                $"{label}: {problems.Count} mismatch(es) against expectedRows.json" +
                $"{(fixture.ExpectationFor(backend, engine) != new ExpectRows(fixture.Expected) ? " (XEQ-4 override applied)" : string.Empty)}:\n  " +
                string.Join("\n  ", problems) +
                $"\n  expected: {Render(expected.Columns, expected.Rows.Select(r => (IReadOnlyList<object?>)r.Select(c => (object?)c).ToList()).ToList())}" +
                $"\n  actual:   {Render(actual.Columns, actual.Rows)}");
        }
    }

    /// <summary>The names of the fixtures that carry an <c>expected/&lt;stem&gt;.artifact.json</c> (XEQ-1).</summary>
    public static IEnumerable<object[]> FixturesWith(string stem) =>
        ExecutionFixture.All()
            .Where(name => ExecutionFixture.Load(name).Backends.Contains(stem))
            .Select(name => new object[] { name });

    private static string Render(IReadOnlyList<string> columns, IReadOnlyList<IReadOnlyList<object?>> rows) =>
        $"[{string.Join(", ", columns)}] " + string.Join(" | ", rows.Take(25).Select(r => "(" + string.Join(", ", r.Select(Cell)) + ")"))
        + (rows.Count > 25 ? $" ... (+{rows.Count - 25} more)" : string.Empty);

    private static string Cell(object? value) => value switch
    {
        null => "null",
        string s => "\"" + s + "\"",
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        JsonElement e => e.GetRawText(),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "?"
    };
}
