using System.Text.Json;
using Craticula.AgGrid;
using Craticula.Core.Schema;
using Craticula.Integration.Tests.Oracle;
using Craticula.Sql;

namespace Craticula.Integration.Tests.Containers.Sql;

/// <summary>
/// Turns a fixture's own <c>agGridRequest.json</c> into the compiled <see cref="SqlQuery"/> an engine runs,
/// through the real pipeline with nothing stubbed (parser, <see cref="GridFilterPipeline"/>, SEC-3's default
/// <see cref="ResourceLimits"/>, <see cref="SqlQueryBuilder"/>). Dispatches on the request document like a host
/// endpoint: a <c>setValuesRequest</c> envelope is BM-3's distinct-values query, anything else a row fetch
/// (ordinary, grouped, drill-down leaf or export projection). Mirrors <c>Craticula.Sql.Tests.FixtureCompiler</c>,
/// which is internal to its own assembly.
/// </summary>
internal static class SqlFixtureCompiler
{
    public static SqlQuery Compile(ExecutionFixture fixture, ISqlDialect dialect)
    {
        var requestJson = File.ReadAllText(Path.Combine(fixture.Directory, "agGridRequest.json"));
        var options = new AgGridParseOptions { Schema = fixture.Schema, Clock = ClockFor(requestJson) };

        return IsSetValuesRequest(requestJson)
            ? CompileSetValues(requestJson, options, fixture.Schema, dialect)
            : CompileRowFetch(requestJson, options, fixture.Schema, dialect);
    }

    private static SqlQuery CompileRowFetch(string requestJson, AgGridParseOptions options, GridSchema schema, ISqlDialect dialect)
    {
        var parsed = AgGridRequestParser.Parse(requestJson, options);
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
                Aggregations = parsed.Aggregations,
                Projection = parsed.Export?.ProjectionColumns
            },
            dialect);
    }

    private static SqlQuery CompileSetValues(string requestJson, AgGridParseOptions options, GridSchema schema, ISqlDialect dialect)
    {
        var limits = new ResourceLimits();
        var parsed = AgGridRequestParser.ParseSetValues(requestJson, options);
        var filter = GridFilterPipeline.ProcessSetValues(parsed.NativeFilter, parsed.CustomFilters, parsed.SetValues, schema, limits);

        return SqlQueryBuilder.BuildSetValues(
            new SqlSetValuesRequest
            {
                Schema = schema,
                ColId = parsed.SetValues.ColId,
                Filter = filter,
                RowLimit = ResourceLimitsValidator.ResolveSetValuesLimit(parsed.SetValues.Limit, limits)
            },
            dialect);
    }

    private static bool IsSetValuesRequest(string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        return document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("setValuesRequest", out _);
    }

    // A fixture's requestContext.now is an instruction to the harness (AgGridParseOptions.Clock), not a field the
    // parser reads: pin the clock to it so date presets resolve as the fixture's author derived them.
    private static TimeProvider ClockFor(string requestJson)
    {
        using var document = JsonDocument.Parse(requestJson);
        return document.RootElement.TryGetProperty("requestContext", out var context)
            && context.ValueKind == JsonValueKind.Object
            && context.TryGetProperty("now", out var now)
            && now.ValueKind == JsonValueKind.String
                ? new PinnedClock(DateTimeOffset.Parse(now.GetString()!, System.Globalization.CultureInfo.InvariantCulture))
                : TimeProvider.System;
    }

    private sealed class PinnedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
