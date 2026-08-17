using System.Text.Json;
using Craticula.AgGrid;
using Craticula.Core.Schema;
using Craticula.Sql;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// One conformance fixture, parsed once into the two request shapes the two paths take.
///
/// <para><b>The pipeline is the real one, with nothing stubbed</b> — the fixture's own
/// <c>schema.json</c>, its own <c>agGridRequest.json</c>,
/// <see cref="AgGridRequestParser.Parse"/>, <see cref="GridFilterPipeline.Process"/>, SEC-3's default
/// <see cref="ResourceLimits"/>. It is deliberately the same construction
/// <c>Craticula.Sql.Tests.FixtureCompiler</c> performs, for the reason that class records about its own
/// two callers: if the two sides could drift in how they FEED the pipeline, a disagreement between them
/// would stop being evidence about the compilers.</para>
///
/// <para><b>The two requests are built from ONE parse.</b> That matters for what AR-4's harness proves:
/// with a single <see cref="AgGridParsedRequest"/> feeding both, the filter tree, the sort array and the
/// page spec are literally the same objects, so any difference in the rows the two paths return is a
/// difference in the two compilers and cannot be a difference in their inputs.</para>
/// </summary>
internal sealed record FixtureRequest(
    string Category,
    string Fixture,
    GridSchema Schema,
    SqlQueryRequest Sql,
    EfCoreQueryRequest Ef,
    IReadOnlyList<Craticula.Core.Export.ExportColumn>? ExportColumns)
{
    public string Name => $"{Category}/{Fixture}";

    public static FixtureRequest Load(string category, string fixture)
    {
        var schema = GridSchema.Parse(Read(category, fixture, "schema.json"));
        var requestJson = Read(category, fixture, "agGridRequest.json");

        var parsed = AgGridRequestParser.Parse(requestJson, new AgGridParseOptions { Schema = schema });

        var filter = GridFilterPipeline.Process(
            parsed.NativeFilter,
            parsed.CustomFilters,
            parsed.Page,
            schema,
            new ResourceLimits(),
            parsed.Group,
            parsed.Aggregations);

        return new FixtureRequest(
            category,
            fixture,
            schema,
            new SqlQueryRequest
            {
                Schema = schema,
                Filter = filter,
                Sort = parsed.Sort,
                Page = parsed.Page,
                Group = parsed.Group,
                Aggregations = parsed.Aggregations,
                Projection = parsed.Export?.ProjectionColumns
            },
            new EfCoreQueryRequest
            {
                Schema = schema,
                Filter = filter,
                Sort = parsed.Sort,
                Page = parsed.Page,
                Group = parsed.Group,
                Aggregations = parsed.Aggregations,
                Projection = parsed.Export?.ProjectionColumns
            },
            // EXP-1(b)'s resolved columns WITH their headers — the writer's half of the export contract,
            // which no compiler and no execution adapter ever sees (a header reaches no artifact). Kept
            // here so the CsvWriter end-to-end test uses the columns the real parser resolved rather
            // than a list assembled in a test.
            parsed.Export?.Columns);
    }

    /// <summary>The one fixture directory whose name starts with <paramref name="prefix"/> — so a case
    /// is referenced by its number (<c>operators/001</c>) rather than by a title that a later story may
    /// rewrite, which is how a hand-listed fixture reference rots.</summary>
    public static string ResolveDirectory(string category, string prefix)
    {
        var matches = Directory
            .EnumerateDirectories(RepoPaths.Fixture(category))
            .Select(path => Path.GetFileName(path)!)
            .Where(name => name.StartsWith(prefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        return matches.Count == 1
            ? matches[0]
            : throw new InvalidOperationException(
                $"Expected exactly one fixture under '{category}' starting with '{prefix}', found {matches.Count}: " +
                string.Join(", ", matches));
    }

    /// <summary>The SQL the fixture's own Postgres artifact pins, for the tests that want to state what
    /// the other path emits without re-deriving it. Read, never compared byte-for-byte here — that is
    /// <c>Craticula.Sql.Tests</c>' job, and duplicating it would give this suite a second reason to fail
    /// for a change that has nothing to do with EF.</summary>
    public string PostgresArtifactSql()
    {
        using var artifact = JsonDocument.Parse(Read(Category, Fixture, "expected", "postgres.artifact.json"));
        return artifact.RootElement.GetProperty("sql").GetString()!;
    }

    private static string Read(string category, string fixture, params string[] rest) =>
        File.ReadAllText(RepoPaths.Fixture([category, fixture, .. rest]));
}
