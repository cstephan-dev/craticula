using System.Text.Json;
using Craticula.AgGrid;
using Craticula.Core.Schema;

namespace Craticula.Sql.Tests;

/// <summary>
/// The one place a conformance test turns a fixture directory into a compiled
/// <see cref="SqlQuery"/>, shared by <see cref="PostgresConformanceFixtureTests"/> and
/// <see cref="DialectConformanceFixtureTests"/>. Extracted in #1639 rather than copied: the two
/// suites must differ ONLY in which <see cref="ISqlDialect"/> they pass and which artifact they
/// compare against — if they could also drift in how they feed the pipeline, a per-dialect failure
/// would no longer be evidence about the dialect.
/// </summary>
internal static class FixtureCompiler
{
    /// <summary>
    /// The exact pipeline a host runs, with nothing stubbed: the fixture's own <c>schema.json</c>,
    /// its own request, SEC-3's default <see cref="ResourceLimits"/>, and the dialect under test.
    /// </summary>
    public static SqlQuery Compile(string category, string fixtureDir, ISqlDialect dialect)
    {
        var schema = GridSchema.Parse(Read(category, fixtureDir, "schema.json"));
        var parsed = AgGridRequestParser.Parse(
            Read(category, fixtureDir, "agGridRequest.json"),
            OptionsFor(category, fixtureDir, schema));

        var filter = GridFilterPipeline.Process(
            parsed.NativeFilter,
            parsed.CustomFilters,
            parsed.Page,
            schema,
            new ResourceLimits(),
            parsed.Group,
            parsed.Aggregations);

        return SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = schema,
                Filter = filter,
                Sort = parsed.Sort,
                Page = parsed.Page,
                Group = parsed.Group,
                Aggregations = parsed.Aggregations
            },
            dialect);
    }

    /// <summary>
    /// The harness setup a port must reproduce: the fixture's own <c>GridSchema</c> always (a date
    /// filter's wire value is typed from it), plus a clock pinned to <c>requestContext.now</c> for
    /// the cases that declare one. That <c>now</c> is an instruction to the harness, not a field the
    /// parser reads — see <see cref="AgGridParseOptions.Clock"/>.
    /// </summary>
    private static AgGridParseOptions OptionsFor(string category, string fixtureDir, GridSchema schema)
    {
        using var request = JsonDocument.Parse(Read(category, fixtureDir, "agGridRequest.json"));

        var pinnedNow = request.RootElement.TryGetProperty("requestContext", out var context)
            && context.ValueKind == JsonValueKind.Object
            && context.TryGetProperty("now", out var now)
            && now.ValueKind == JsonValueKind.String
                ? now.GetString()
                : null;

        return new AgGridParseOptions
        {
            Schema = schema,
            Clock = pinnedNow is null ? TimeProvider.System : FixedClock.At(pinnedNow)
        };
    }

    public static string Read(string category, string fixtureDir, params string[] rest) =>
        File.ReadAllText(RepoPaths.Fixture([category, fixtureDir, .. rest]));

    /// <summary>
    /// Both sides of a parameter comparison go through the identical writer, so the assertion is on
    /// JSON <em>values</em> (including each number's exact literal text and each element's type)
    /// without being sensitive to the fixture file's own whitespace. Comparing the serialized text
    /// rather than element-by-element is what keeps <c>"19.99"</c> (a decimal, AR-7) from silently
    /// passing against <c>19.99</c> (a double).
    /// </summary>
    public static string CanonicalJson<T>(T value) => JsonSerializer.Serialize(value, CanonicalOptions);

    private static readonly JsonSerializerOptions CanonicalOptions = new() { WriteIndented = false };
}
