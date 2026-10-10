using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Craticula.AgGrid;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Xunit.Abstractions;

namespace Craticula.Integration.Tests.Parity;

/// <summary>
/// Offline guards for the parity generator (T-3, PAR-1..PAR-4): no engine, no Docker. They prove the generator is a pure
/// function of its seed, that every request it writes is one the real adapter accepts as an ordinary row request inside
/// SEC-3's limits, that a default run visits every operator and field type, and that it keeps to the register of PAR-3.
/// </summary>
public sealed partial class ParityGeneratorTests(ITestOutputHelper output)
{
    private const long DefaultSeed = 20261010;
    private const int DefaultCases = 200;
    private const int DefaultRows = 150;

    private static readonly GridSchema Schema = ParitySchema.Create();

    [Fact]
    public void Schema_parses_and_declares_one_field_per_type_with_the_stable_key_first()
    {
        Assert.Equal("id", Schema.StableKey);
        Assert.Equal(ParitySchema.FieldNames, Schema.FieldOrder);
        Assert.Equal(
            Enum.GetValues<FieldType>().OrderBy(t => t),
            Schema.Fields.Values.Select(f => f.Type).Distinct().OrderBy(t => t));
    }

    [Fact]
    public void Schema_declares_nothing_on_the_register()
    {
        // PAR-3: no case-sensitive string (divergence #5), no analyzed search mode (SEM-7), no groupable field (GRP-2 #3/#4).
        foreach (var (name, field) in Schema.Fields)
        {
            Assert.False(field.Groupable, $"{name} must not be groupable");
            if (field.Text is { } text)
            {
                Assert.Equal(CaseSensitivity.Insensitive, text.CaseSensitivity);
                Assert.Equal(SearchMode.Substring, text.SearchMode);
            }
        }
    }

    [Fact]
    public void Dataset_is_deterministic_per_seed_and_differs_across_seeds()
    {
        Assert.Equal(ParityDataset.Generate(DefaultSeed, DefaultRows).ToJson(), ParityDataset.Generate(DefaultSeed, DefaultRows).ToJson());
        Assert.NotEqual(ParityDataset.Generate(DefaultSeed, DefaultRows).ToJson(), ParityDataset.Generate(DefaultSeed + 1, DefaultRows).ToJson());
    }

    [Fact]
    public void Dataset_follows_XEQ2_and_the_register()
    {
        var dataset = ParityDataset.Generate(DefaultSeed, DefaultRows);
        using var document = JsonDocument.Parse(dataset.ToJson());
        var rows = document.RootElement.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("row")).ToList();

        Assert.Equal(DefaultRows, rows.Count);

        // XEQ-2: unique, non-null stableKey; every schema field present in every row (a null is an explicit null).
        var ids = rows.Select(r => r.GetProperty("id").GetInt64()).ToList();
        Assert.Equal(DefaultRows, ids.Distinct().Count());
        Assert.All(rows, row => Assert.Equal(ParitySchema.FieldNames, row.EnumerateObject().Select(p => p.Name)));

        // Every non-key field holds nulls, duplicates and several distinct values.
        foreach (var field in ParitySchema.FieldNames.Skip(1))
        {
            var values = rows.Select(r => r.GetProperty(field)).ToList();
            Assert.Contains(values, v => v.ValueKind == JsonValueKind.Null);
            var distinct = values.Where(v => v.ValueKind != JsonValueKind.Null).Select(v => v.GetRawText()).ToList();
            Assert.True(distinct.Distinct().Count() > 1, $"{field} has a single value");
            Assert.True(distinct.Count > distinct.Distinct().Count(), $"{field} has no duplicate values");
        }

        // Register: no empty string (SQL-7); datetimes carry exactly three fractional digits (SEM-4, XEQ-2).
        Assert.All(rows, row =>
        {
            foreach (var property in row.EnumerateObject().Where(p => p.Value.ValueKind == JsonValueKind.String))
            {
                Assert.NotEqual(string.Empty, property.Value.GetString());
            }

            if (row.GetProperty("seen") is { ValueKind: JsonValueKind.String } seen)
            {
                Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$", seen.GetString()!);
            }
        });
    }

    [Fact]
    public void Requests_are_deterministic_per_seed_and_differ_across_seeds()
    {
        var first = Requests(DefaultSeed);
        Assert.Equal(first, Requests(DefaultSeed));
        Assert.NotEqual(first, Requests(DefaultSeed + 1));
        Assert.True(first.Distinct().Count() > DefaultCases * 9 / 10, "requests should be mostly distinct");
    }

    [Fact]
    public void Every_default_request_is_an_ordinary_row_request_inside_the_limits()
    {
        var limits = new ResourceLimits();
        foreach (var (json, index) in Requests(DefaultSeed).Select((json, index) => (json, index)))
        {
            var parsed = Parse(json, index);
            var page = Assert.IsType<RangePage>(parsed.Page);
            var size = page.EndRow - page.StartRow;

            // PAR-1: an ordinary row request has no grouping, aggregation or export.
            Assert.True(parsed.Group is null, $"case {index}: grouped");
            Assert.True(parsed.Aggregations is null or { Count: 0 }, $"case {index}: aggregated");
            Assert.Null(parsed.Export);
            using var wire = JsonDocument.Parse(json);
            Assert.Equal(0, wire.RootElement.GetProperty("rowGroupCols").GetArrayLength());
            Assert.Equal(0, wire.RootElement.GetProperty("valueCols").GetArrayLength());
            Assert.Equal(0, wire.RootElement.GetProperty("groupKeys").GetArrayLength());
            Assert.False(wire.RootElement.TryGetProperty("setValuesRequest", out _));
            Assert.False(wire.RootElement.TryGetProperty("exportFormat", out _));

            // Window: startRow < endRow, at most 100 rows, offsets below 10000, OpenSearch's default max_result_window (PERF-2, OSE-10); SEC-3 limits only the page size.
            Assert.InRange(page.StartRow, 0, 10000);
            Assert.True(page.StartRow < page.EndRow);
            Assert.InRange(size, 1, ParityRequestGenerator.MaxWindow);
            Assert.True(page.StartRow + size <= 10000);
            Assert.True(size <= limits.MaxPageSize);
        }
    }

    [Fact]
    public void A_default_run_visits_every_operator_node_kind_and_field_type()
    {
        var operators = new Dictionary<ComparisonOperator, int>();
        var nodeKinds = new Dictionary<string, int> { ["and"] = 0, ["or"] = 0, ["not"] = 0 };
        var filterTypes = new Dictionary<FieldType, int>();
        var sortTypes = new Dictionary<FieldType, int>();
        int windowsPastZero = 0, multiKeySorts = 0, unfiltered = 0, merged = 0;

        foreach (var (json, index) in Requests(DefaultSeed).Select((json, index) => (json, index)))
        {
            var parsed = Parse(json, index);
            var filter = GridFilterPipeline.Process(parsed.NativeFilter, parsed.CustomFilters, parsed.Page, Schema, new ResourceLimits());
            if (filter is null)
            {
                unfiltered++;
            }

            if (parsed.NativeFilter is not null && parsed.CustomFilters is not null)
            {
                merged++;
            }

            Walk(filter, operators, nodeKinds, filterTypes);
            foreach (var key in parsed.Sort ?? [])
            {
                Increment(sortTypes, Schema.Fields[key.Field].Type);
            }

            windowsPastZero += ((RangePage)parsed.Page).StartRow > 0 ? 1 : 0;
            multiKeySorts += (parsed.Sort?.Count ?? 0) >= 2 ? 1 : 0;
        }

        output.WriteLine("operators: " + string.Join(", ", operators.OrderBy(o => o.Key).Select(o => $"{o.Key}={o.Value}")));
        output.WriteLine("node kinds: " + string.Join(", ", nodeKinds.Select(o => $"{o.Key}={o.Value}")));
        output.WriteLine("filter field types: " + string.Join(", ", filterTypes.OrderBy(o => o.Key).Select(o => $"{o.Key}={o.Value}")));
        output.WriteLine("sort field types: " + string.Join(", ", sortTypes.OrderBy(o => o.Key).Select(o => $"{o.Key}={o.Value}")));
        output.WriteLine($"unfiltered={unfiltered} native+custom merged={merged} windows past row 0={windowsPastZero} multi-key sorts={multiKeySorts}");

        foreach (var op in Enum.GetValues<ComparisonOperator>())
        {
            Assert.True(operators.GetValueOrDefault(op) >= 1, $"operator {op} never generated");
        }

        Assert.All(nodeKinds, kind => Assert.True(kind.Value >= 1, $"node kind {kind.Key} never generated"));
        foreach (var type in Enum.GetValues<FieldType>())
        {
            Assert.True(filterTypes.GetValueOrDefault(type) >= 1, $"no filter on a {type} field");
        }

        // The sort keys avoid the string field (register row 1) and cover every other type.
        Assert.False(sortTypes.ContainsKey(FieldType.String), "a string field was used as a sort key");
        foreach (var type in Enum.GetValues<FieldType>().Where(t => t != FieldType.String))
        {
            Assert.True(sortTypes.GetValueOrDefault(type) >= 1, $"no sort on a {type} field");
        }

        Assert.True(unfiltered >= 1);
        Assert.True(merged >= 1);
        Assert.True(windowsPastZero >= 1);
        Assert.True(multiKeySorts >= 1);
    }

    [Fact]
    public void A_default_run_visits_every_field_and_operator_pair_the_generator_offers()
    {
        var pairs = new HashSet<(string Field, ComparisonOperator Op)>();
        foreach (var (json, index) in Requests(DefaultSeed).Select((json, index) => (json, index)))
        {
            var parsed = Parse(json, index);
            foreach (var comparison in Comparisons(GridFilterPipeline.Process(
                         parsed.NativeFilter, parsed.CustomFilters, parsed.Page, Schema, new ResourceLimits())))
            {
                pairs.Add((comparison.Field, comparison.Op));
            }
        }

        output.WriteLine($"{pairs.Count} distinct (field, operator) pairs of {ParityRequestGenerator.CatalogSize} offered");

        // A boolean's eq/neq arrive as the unary true/false forms or as eq/neq; the catalog counts each as its own entry.
        Assert.Equal(ParityRequestGenerator.CatalogSize, pairs.Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(424242)]
    [InlineData(-9)]
    public void Other_seeds_also_parse_and_stay_inside_the_limits(long seed)
    {
        foreach (var (json, index) in Requests(seed).Select((json, index) => (json, index)))
        {
            var parsed = Parse(json, index);
            Assert.Null(parsed.Export);
            Assert.True(parsed.Group is null);
        }
    }

    [Fact]
    public void Operands_keep_to_the_register()
    {
        var plain = PlainOperand();
        foreach (var (json, index) in Requests(DefaultSeed).Select((json, index) => (json, index)))
        {
            var parsed = Parse(json, index);
            foreach (var comparison in Comparisons(GridFilterPipeline.Process(
                         parsed.NativeFilter, parsed.CustomFilters, parsed.Page, Schema, new ResourceLimits())))
            {
                var operands = new[] { comparison.Value, comparison.From, comparison.To }.Concat(comparison.Values ?? []).OfType<TypedValue>();
                foreach (var operand in operands)
                {
                    switch (operand)
                    {
                        case StringValue s when comparison.Op is ComparisonOperator.Contains or ComparisonOperator.NotContains
                            or ComparisonOperator.StartsWith or ComparisonOperator.EndsWith:
                            // SEM-7 / MDB-11: letters and digits only, so no engine's escape alphabet is in play.
                            Assert.Matches(plain, s.Value);
                            break;
                        case StringValue s:
                            Assert.NotEqual(string.Empty, s.Value); // SQL-7
                            break;
                        case DatetimeValue d:
                            // SEM-4 (SQLite): millisecond-aligned, i.e. no fraction finer than three digits.
                            Assert.True(
                                DateTimeOffset.Parse(d.Value, CultureInfo.InvariantCulture).Ticks % TimeSpan.TicksPerMillisecond == 0,
                                $"case {index}: {d.Value} is finer than a millisecond");
                            break;
                    }
                }
            }
        }
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private static List<string> Requests(long seed) =>
        [.. Enumerable.Range(0, DefaultCases).Select(index => ParityRequestGenerator.Generate(seed, index))];

    /// <summary>The same construction as <c>Craticula.Dapper.Tests.FixtureRequest</c>: the real parser and filter pipeline, SEC-3's default limits.</summary>
    private static AgGridParsedRequest Parse(string json, int index)
    {
        var parsed = AgGridRequestParser.Parse(json, new AgGridParseOptions { Schema = Schema });

        // Throws on a depth, leaf, in-list or page-size breach, on a field the schema does not allow, or on a type mismatch.
        try
        {
            _ = GridFilterPipeline.Process(
                parsed.NativeFilter, parsed.CustomFilters, parsed.Page, Schema, new ResourceLimits(), parsed.Group, parsed.Aggregations);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException($"Case {index} was rejected: {exception.Message}\n{json}", exception);
        }

        return parsed;
    }

    private static void Walk(
        FilterNode? node,
        Dictionary<ComparisonOperator, int> operators,
        Dictionary<string, int> nodeKinds,
        Dictionary<FieldType, int> filterTypes)
    {
        switch (node)
        {
            case ComparisonNode comparison:
                Increment(operators, comparison.Op);
                Increment(filterTypes, Schema.Fields[comparison.Field].Type);
                break;
            case AndNode and:
                nodeKinds["and"]++;
                and.Nodes.ToList().ForEach(child => Walk(child, operators, nodeKinds, filterTypes));
                break;
            case OrNode or:
                nodeKinds["or"]++;
                or.Nodes.ToList().ForEach(child => Walk(child, operators, nodeKinds, filterTypes));
                break;
            case NotNode not:
                nodeKinds["not"]++;
                Walk(not.Node, operators, nodeKinds, filterTypes);
                break;
        }
    }

    private static IEnumerable<ComparisonNode> Comparisons(FilterNode? node) => node switch
    {
        ComparisonNode comparison => [comparison],
        CompositeFilterNode composite => composite.Nodes.SelectMany(Comparisons),
        NotNode not => Comparisons(not.Node),
        _ => []
    };

    private static void Increment<TKey>(Dictionary<TKey, int> counts, TKey key)
        where TKey : notnull => counts[key] = counts.GetValueOrDefault(key) + 1;

    private static Regex PlainOperand() => PlainOperandPattern();

    [GeneratedRegex("^[A-Za-z0-9]+$")]
    private static partial Regex PlainOperandPattern();
}
