using System.Globalization;
using System.Text.Json;
using Craticula.AgGrid;
using Craticula.Core.Export;
using Craticula.Core.Schema;

namespace Craticula.Export.Csv.Tests;

/// <summary>
/// The one place a fixture directory becomes the writer's three inputs — the resolved
/// <see cref="ExportSpec"/>, the row stream and the <see cref="CsvWriterOptions"/> — mirroring
/// <c>Craticula.Sql.Tests.FixtureCompiler</c>'s role for the compilers.
///
/// <para>The spec comes out of the REAL <see cref="AgGridRequestParser.Parse"/>, not out of a
/// hand-built object: the column list and every header string are EXP-1(b)'s resolution of the
/// fixture's own request against the fixture's own schema, so the expected bytes grade the two
/// stories together. Feeding a hand-written column list here would let the writer pass while the
/// projection it is supposed to serialize is wrong.</para>
/// </summary>
internal static class WriterFixture
{
    /// <summary>A fixture's parsed request, as a host would parse it.</summary>
    public static ExportSpec ExportSpecOf(string category, string fixtureDir, GridSchema schema)
    {
        var parsed = AgGridRequestParser.Parse(
            Read(category, fixtureDir, "agGridRequest.json"),
            new AgGridParseOptions { Schema = schema });

        return parsed.Export
            ?? throw new InvalidOperationException(
                $"{category}/{fixtureDir} carries a rowStream.json but its request is not an export.");
    }

    public static GridSchema SchemaOf(string category, string fixtureDir) =>
        GridSchema.Parse(Read(category, fixtureDir, "schema.json"));

    /// <summary>
    /// EXP-4/EXP-5's writer configuration, from <c>writerConfig.json</c> when the fixture overrides a
    /// default and from <see cref="CsvWriterOptions.ForFormat"/> when it does not. The absence of the
    /// file is meaningful, not a shortcut: a fixture proving a default must be compared against bytes
    /// the writer's own defaults produced.
    /// </summary>
    public static CsvWriterOptions OptionsOf(string category, string fixtureDir, ExportFormat format)
    {
        var options = CsvWriterOptions.ForFormat(format);
        var path = RepoPaths.Fixture(category, fixtureDir, "writerConfig.json");
        if (!File.Exists(path))
        {
            return options;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        if (root.TryGetProperty("delimiter", out var delimiter))
        {
            options = options with { Delimiter = Single(delimiter, "delimiter") };
        }

        if (root.TryGetProperty("decimalSeparator", out var decimalSeparator))
        {
            options = options with { DecimalSeparator = Single(decimalSeparator, "decimalSeparator") };
        }

        if (root.TryGetProperty("bom", out var bom))
        {
            options = options with { WriteByteOrderMark = bom.GetBoolean() };
        }

        if (root.TryGetProperty("headerRow", out var headerRow))
        {
            options = options with { IncludeHeaderRow = headerRow.GetBoolean() };
        }

        return options;
    }

    /// <summary>The row stream's declared column list — asserted against the resolved projection by
    /// the conformance suite, so the fixture cannot quietly carry columns the request never
    /// projected.</summary>
    public static IReadOnlyList<string> StreamColumns(string category, string fixtureDir)
    {
        using var document = JsonDocument.Parse(Read(category, fixtureDir, "rowStream.json"));
        return [.. document.RootElement.GetProperty("columns").EnumerateArray().Select(column => column.GetString()!)];
    }

    /// <summary>
    /// The rows, decoded into the CLR values an execution adapter would have produced — a
    /// <see cref="decimal"/> for a <c>decimal</c> field, a <see cref="DateOnly"/> for a <c>date</c>
    /// one, <see langword="null"/> for an absent cell. Decoding against the schema's declared types
    /// rather than handing the writer the JSON text is what keeps EXP-5's formatting layer in the
    /// path: a pre-stringified <c>"1234.50"</c> would pass through untouched and prove nothing about
    /// invariant-culture formatting or the decimal separator.
    /// </summary>
    public static IReadOnlyList<ExportRow> Rows(string category, string fixtureDir, GridSchema schema, ExportSpec spec)
    {
        using var document = JsonDocument.Parse(Read(category, fixtureDir, "rowStream.json"));

        var rows = new List<ExportRow>();
        foreach (var element in document.RootElement.GetProperty("rows").EnumerateArray())
        {
            var cells = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var column in spec.Columns)
            {
                cells[column.ColId] = element.TryGetProperty(column.ColId, out var value)
                    ? Decode(value, schema.Fields[column.ColId].Type, column.ColId)
                    : null;
            }

            rows.Add(ExportRow.FromCells(cells, spec.Columns));
        }

        return rows;
    }

    private static object? Decode(JsonElement value, FieldType type, string colId) => value.ValueKind switch
    {
        JsonValueKind.Null => null,
        _ => type switch
        {
            FieldType.String => value.GetString(),
            FieldType.Int64 => value.GetInt64(),
            FieldType.Double => value.GetDouble(),
            FieldType.Boolean => value.GetBoolean(),

            // A decimal is spelled as a JSON STRING in a row stream, for the same reason a mongo
            // artifact spells one as {"$numberDecimal": "..."}: a JSON number would let the file's
            // own parser decide the scale, and "1234.50" would be indistinguishable from "1234.5"
            // long before the writer saw it.
            FieldType.Decimal => decimal.Parse(RequireString(value, colId), CultureInfo.InvariantCulture),
            FieldType.Date => DateOnly.ParseExact(RequireString(value, colId), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Datetime => DateTimeOffset.Parse(
                RequireString(value, colId), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind),
            _ => throw new InvalidOperationException($"No row-stream decoding defined for field type {type}.")
        }
    };

    private static string RequireString(JsonElement value, string colId) =>
        value.ValueKind == JsonValueKind.String
            ? value.GetString()!
            : throw new InvalidOperationException(
                $"Cell '{colId}' must be a JSON string (see schemas/row-stream.schema.json), not {value.ValueKind}.");

    private static char Single(JsonElement value, string property)
    {
        var text = value.GetString();
        return text is { Length: 1 }
            ? text[0]
            : throw new InvalidOperationException($"writerConfig.json's '{property}' must be exactly one character.");
    }

    public static string Read(string category, string fixtureDir, params string[] rest) =>
        File.ReadAllText(RepoPaths.Fixture([category, fixtureDir, .. rest]));
}
