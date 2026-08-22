using System.Text.Json;
using Craticula.AgGrid;
using Craticula.Core.Schema;
using Craticula.Sql;

namespace Craticula.Dapper.Tests;

/// <summary>
/// One conformance fixture, parsed once into the request shape both <c>Craticula.Sql</c> and
/// <c>Craticula.Dapper</c> share.
///
/// <para><b>The pipeline is the real one, with nothing stubbed</b> — the fixture's own
/// <c>schema.json</c>, its own <c>agGridRequest.json</c>, <see cref="AgGridRequestParser.Parse"/>,
/// <see cref="GridFilterPipeline.Process"/>, SEC-3's default <see cref="ResourceLimits"/>. Mirrors
/// <c>Craticula.EntityFrameworkCore.Tests.FixtureRequest</c>'s own construction, minus the second
/// request TYPE that class needs for its cross-compiler comparison: <c>Craticula.Dapper</c> has no IR
/// of its own — its whole job is executing the SAME <see cref="SqlQueryRequest"/> a host would hand
/// <see cref="SqlQueryBuilder.Build"/> directly, so one parse yields the one request shape this
/// package's two entry points and the raw-SQL oracle both consume.</para>
/// </summary>
internal sealed record FixtureRequest(
    string Category,
    string Fixture,
    GridSchema Schema,
    SqlQueryRequest Sql,
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
            parsed.Export?.Columns);
    }

    /// <summary>The one fixture directory whose name starts with <paramref name="prefix"/> — so a case
    /// is referenced by its number (<c>operators/001</c>) rather than by a title that a later story may
    /// rewrite.</summary>
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

    private static string Read(string category, string fixture, params string[] rest) =>
        File.ReadAllText(RepoPaths.Fixture([category, fixture, .. rest]));
}
