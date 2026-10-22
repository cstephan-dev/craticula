using System.Text.Json;
using System.Text.Json.Nodes;
using Craticula.Integration.Tests.Containers.Sql;
using Craticula.Integration.Tests.Oracle;

namespace Craticula.Integration.Tests.Parity;

/// <summary>One filter-operator family, the unit of the strength report's "non-empty share by operator family".</summary>
internal enum OperatorFamily
{
    Equality,
    Range,
    Text,
    NullCheck,
    Boolean,
    Unfiltered
}

/// <summary>How hard one run of the property actually looks (T-3, #2270): see <see cref="ParityStrength"/>.</summary>
internal sealed record ParityStrengthReport(
    int Cases,
    int NonEmpty,
    int IdsInNonEmpty,
    IReadOnlyDictionary<OperatorFamily, (int Cases, int NonEmpty)> Families,
    int MultiKeyCases,
    int MultiKeyTied,
    int NullOrderCases,
    int WindowedPastZero,
    int WindowCutsMidResult,
    int WindowTruncates,
    int FilterMatchedNothing)
{
    public double NonEmptyShare => Cases == 0 ? 0 : (double)NonEmpty / Cases;

    public double MeanIdsPerNonEmpty => NonEmpty == 0 ? 0 : (double)IdsInNonEmpty / NonEmpty;

    public double TieShare => MultiKeyCases == 0 ? 0 : (double)MultiKeyTied / MultiKeyCases;

    public double MidCutShare => Cases == 0 ? 0 : (double)WindowCutsMidResult / Cases;

    public string Summary() =>
        $"{Cases} cases: {NonEmpty} returned rows ({NonEmptyShare:P1}), {MeanIdsPerNonEmpty:F1} ids per non-empty case; " +
        $"first-key ties in {MultiKeyTied} of {MultiKeyCases} multi-key sorts ({TieShare:P1}); " +
        $"nulls in the first sort key of {NullOrderCases}; window past row 0 in {WindowedPastZero}, cutting mid-result in {WindowCutsMidResult} ({MidCutShare:P1}), " +
        $"truncating the result in {WindowTruncates}; filter matched nothing in {FilterMatchedNothing}; non-empty by family: " +
        string.Join(", ", Families.OrderBy(f => f.Key).Select(f => $"{f.Key} {f.Value.NonEmpty}/{f.Value.Cases}"));
}

/// <summary>
/// The strength of the parity property for one <c>(seed, cases, rows)</c>, measured without Docker. A property that
/// compares empty windows passes for nothing, so this runs every generated request on the in-process SQLite engine (the
/// tier's own harness, seeded with the parity dataset) and counts what the request actually exercises: whether rows come
/// back, how many, whether a multi-key sort has a first-key tie for the tiebreaker to break (SEM-6), whether a sort key is
/// NULL in the result (SEM-3's placement), and whether the window cuts the ordered result in the middle (PAR-1's
/// "window included"). The result is one engine's view, which is enough: the metrics are about the request, and the property
/// itself says the eight engines agree on it.
/// </summary>
internal static class ParityStrength
{
    public static async Task<ParityStrengthReport> MeasureAsync(long seed, int cases, int rows)
    {
        var dataset = ParityDataset.Generate(seed, rows);
        using var document = JsonDocument.Parse(dataset.ToJson());
        var byId = document.RootElement.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("row"))
            .ToDictionary(r => r.GetProperty("id").GetInt64(), r => r.Clone());

        var schema = ParitySchema.Create();
        var seedFixture = ExecutionFixture.FromMemory("parity-seed", schema, "{}", ExecutionFixture.ReadSeed(document.RootElement));
        var engine = new SqliteEngine();
        try
        {
            await engine.SeedAsync(seedFixture);

            var families = Enum.GetValues<OperatorFamily>().ToDictionary(f => f, _ => (Cases: 0, NonEmpty: 0));
            int nothing = 0, nonEmpty = 0, ids = 0, multiKey = 0, tied = 0, nullOrder = 0, pastZero = 0, midCut = 0, truncates = 0;

            for (var index = 0; index < cases; index++)
            {
                var request = ParityRequestGenerator.Generate(seed, index);
                var windowed = await RunAsync(engine, schema, request);

                var node = JsonNode.Parse(request)!.AsObject();
                var start = node["startRow"]!.GetValue<int>();
                var end = node["endRow"]!.GetValue<int>();
                node["startRow"] = 0;
                node["endRow"] = 1000;
                var total = await RunAsync(engine, schema, node.ToJsonString());

                var isNonEmpty = windowed.Count > 0;
                nonEmpty += isNonEmpty ? 1 : 0;
                ids += windowed.Count;

                foreach (var family in FamiliesOf(request))
                {
                    var (c, n) = families[family];
                    families[family] = (c + 1, n + (isNonEmpty ? 1 : 0));
                }

                if (start > 0)
                {
                    pastZero++;
                    midCut += total.Count > start ? 1 : 0;
                }

                truncates += total.Count > end ? 1 : 0;
                nothing += total.Count == 0 ? 1 : 0;

                var keys = SortKeys(node);
                if (keys.Count >= 1 && total.Count > 0)
                {
                    var first = keys[0];
                    var values = total.Select(id => Cell(byId[id], first)).ToList();
                    nullOrder += values.Any(v => v is null) && values.Any(v => v is not null) ? 1 : 0;
                    if (keys.Count >= 2)
                    {
                        multiKey++;
                        tied += values.GroupBy(v => v ?? "<null>").Any(g => g.Count() > 1) ? 1 : 0;
                    }
                }
                else if (keys.Count >= 2)
                {
                    multiKey++;
                }
            }

            return new ParityStrengthReport(cases, nonEmpty, ids, families, multiKey, tied, nullOrder, pastZero, midCut, truncates, nothing);
        }
        finally
        {
            await ((IAsyncLifetime)engine).DisposeAsync();
        }
    }

    private static async Task<IReadOnlyList<long>> RunAsync(SqliteEngine engine, Craticula.Core.Schema.GridSchema schema, string request)
    {
        var query = SqlFixtureCompiler.Compile(request, schema, engine.Dialect);
        var result = await engine.QueryAsync(query);
        var at = result.Columns.ToList().FindIndex(c => string.Equals(c, "id", StringComparison.OrdinalIgnoreCase));
        return [.. result.Rows.Select(row => Convert.ToInt64(row[at], System.Globalization.CultureInfo.InvariantCulture))];
    }

    private static List<string> SortKeys(JsonObject request) =>
        [.. request["sortModel"]!.AsArray().Select(k => k!["colId"]!.GetValue<string>())];

    private static string? Cell(JsonElement row, string field) =>
        row.GetProperty(field) is { ValueKind: not JsonValueKind.Null } value ? value.GetRawText() : null;

    /// <summary>The operator families a request's filter uses (wire spellings of native and customFilters nodes).</summary>
    private static IReadOnlySet<OperatorFamily> FamiliesOf(string request)
    {
        var found = new HashSet<OperatorFamily>();
        var root = JsonNode.Parse(request)!;
        if (root["filterModel"] is null && root["customFilters"] is null)
        {
            found.Add(OperatorFamily.Unfiltered);
            return found;
        }

        void Visit(JsonNode? node, string? parentField)
        {
            switch (node)
            {
                case JsonObject obj:
                    if (obj["op"]?.GetValue<string>() is { } op)
                    {
                        found.Add(FamilyOf(op, null));
                    }

                    if (obj["type"]?.GetValue<string>() is { } type && obj["filterType"] is not null)
                    {
                        found.Add(FamilyOf(null, type, obj["filterType"]!.GetValue<string>()));
                    }

                    if (obj["filterType"]?.GetValue<string>() == "set")
                    {
                        found.Add(parentField == "active" ? OperatorFamily.Boolean : OperatorFamily.Equality);
                    }

                    foreach (var (name, child) in obj)
                    {
                        Visit(child, name is "filterModel" or "conditions" or "condition1" or "condition2" ? parentField : name);
                    }

                    break;
                case JsonArray array:
                    foreach (var child in array)
                    {
                        Visit(child, parentField);
                    }

                    break;
            }
        }

        Visit(root["filterModel"], null);
        Visit(root["customFilters"], null);
        return found;
    }

    private static OperatorFamily FamilyOf(string? op, string? nativeType, string? filterType = null)
    {
        if (op is null)
        {
            op = nativeType switch
            {
                "equals" => "eq",
                "notEqual" => "neq",
                "greaterThan" => "gt",
                "greaterThanOrEqual" => "gte",
                "lessThan" => "lt",
                "lessThanOrEqual" => "lte",
                _ => nativeType!
            };
        }

        return op switch
        {
            "eq" or "neq" or "in" or "notIn" => OperatorFamily.Equality,
            "gt" or "gte" or "lt" or "lte" or "inRange" => OperatorFamily.Range,
            "contains" or "notContains" or "startsWith" or "endsWith" => OperatorFamily.Text,
            "blank" or "notBlank" => OperatorFamily.NullCheck,
            "true" or "false" => OperatorFamily.Boolean,
            _ => OperatorFamily.Equality
        };
    }
}
