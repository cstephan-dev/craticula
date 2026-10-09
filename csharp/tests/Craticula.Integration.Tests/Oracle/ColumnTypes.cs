using System.Text.Json;
using Craticula.Core.Schema;

namespace Craticula.Integration.Tests.Oracle;

/// <summary>
/// The type XEQ-3 compares each result column by. A column is a schema field, or on a grouped level
/// (<c>rowGroupCols</c> outnumber <c>groupKeys</c>) a <c>valueCols</c> output name typed by its aggregation:
/// <c>count</c> int64, <c>avg</c> always double, anything else the aggregated field's type. A BM-3 values
/// query's one column is the field named by <c>setValuesRequest.colId</c>. Anything else is a mistake in the
/// fixture, never an unchecked cell.
/// </summary>
public static class ColumnTypes
{
    public static IReadOnlyList<FieldType> Resolve(GridSchema schema, JsonElement request, IReadOnlyList<string> columns) =>
        columns.Select(column => Resolve(schema, request, column)).ToList();

    private static FieldType Resolve(GridSchema schema, JsonElement request, string column)
    {
        var grouped = request.TryGetProperty("rowGroupCols", out var groupCols)
            && groupCols.GetArrayLength() > request.GetProperty("groupKeys").GetArrayLength();
        if (grouped)
        {
            foreach (var value in request.GetProperty("valueCols").EnumerateArray())
            {
                if (value.GetProperty("id").GetString() != column)
                {
                    continue;
                }

                return value.GetProperty("aggFunc").GetString() switch
                {
                    "count" => FieldType.Int64,
                    "avg" => FieldType.Double,
                    _ => FieldOf(schema, value.GetProperty("field").GetString()!, column)
                };
            }
        }

        return FieldOf(schema, column, column);
    }

    private static FieldType FieldOf(GridSchema schema, string field, string column) =>
        schema.Fields.TryGetValue(field, out var definition)
            ? definition.Type
            : throw new InvalidOperationException(
                $"Column '{column}' is neither a schema field nor an aggregate output name (XEQ-3).");
}
