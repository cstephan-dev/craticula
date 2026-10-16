using System.Text.Json;
using System.Text.Json.Nodes;
using Craticula.AgGrid;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Demo.Api.Grid;

/// <summary>A row or export request after <c>AgGridRequestParser.Parse</c> and <c>GridFilterPipeline.Process</c>.</summary>
public sealed record PreparedRows(AgGridParsedRequest Parsed, FilterNode? Filter)
{
    /// <summary>The request's own row offset; <c>LoadSuccessParamsMapper.Map</c> needs it.</summary>
    public long StartRow => Parsed.Page is RangePage range ? range.StartRow : 0;
}

/// <summary>A set-filter values request after <c>ParseSetValues</c> and <c>ProcessSetValues</c>, with the row cap resolved.</summary>
public sealed record PreparedSetValues(string ColId, FilterNode? Filter, int RowLimit);

/// <summary>
/// The provider-neutral half of the request path: parse the wire JSON, allowlist, limit and coerce it, once, for every
/// provider. The host never builds query text; every later step is a library call.
/// </summary>
public sealed class GridRequestPreparer(GridSchema schema, ResourceLimits limits)
{
    private readonly AgGridParseOptions parseOptions = new() { Schema = schema, DefaultZone = TimeZoneInfo.Utc };

    public GridSchema Schema => schema;

    /// <summary>A grid block: needs a start and end row, and carries no export extension.</summary>
    public PreparedRows PrepareRows(string requestJson)
    {
        var parsed = AgGridRequestParser.Parse(requestJson, parseOptions);

        if (parsed.Export is not null)
        {
            throw new AgGridRequestParseException(
                "malformed-request", "A rows request cannot carry exportFormat/exportScope/exportColumns; use the export route.");
        }

        if (parsed.Page is not RangePage)
        {
            throw new AgGridRequestParseException(
                "malformed-request", "A rows request needs both startRow and endRow; an unbounded read is only available through export.");
        }

        return Process(parsed);
    }

    /// <summary>An export: the same body, forced to the CSV format and the whole filtered, sorted result (EXP-1, EXP-2).</summary>
    public PreparedRows PrepareExport(string requestJson)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(requestJson);
        }
        catch (JsonException e)
        {
            throw new AgGridRequestParseException("malformed-request", "The request body is not valid JSON.", e);
        }

        if (node is not JsonObject body)
        {
            throw new AgGridRequestParseException("malformed-request", "The request root must be a JSON object.");
        }

        body["exportFormat"] ??= "csv";
        body["exportScope"] = "all";

        return Process(AgGridRequestParser.Parse(body.ToJsonString(), parseOptions));
    }

    public PreparedSetValues PrepareSetValues(string requestJson)
    {
        var parsed = AgGridRequestParser.ParseSetValues(requestJson, parseOptions);

        var filter = GridFilterPipeline.ProcessSetValues(
            parsed.NativeFilter, parsed.CustomFilters, parsed.SetValues, schema, limits);

        return new PreparedSetValues(
            parsed.SetValues.ColId, filter, ResourceLimitsValidator.ResolveSetValuesLimit(parsed.SetValues.Limit, limits));
    }

    private PreparedRows Process(AgGridParsedRequest parsed) =>
        new(parsed, GridFilterPipeline.Process(
            parsed.NativeFilter, parsed.CustomFilters, parsed.Page, schema, limits, parsed.Group, parsed.Aggregations));
}
