using System.Text.Json;
using Craticula.AgGrid;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Integration.Tests.Containers.Sql;
using Craticula.Mongo;
using Craticula.Search;
using Craticula.Sql;
using Xunit.Abstractions;

namespace Craticula.Integration.Tests.Parity;

/// <summary>
/// Offline guards for two PAR-3 facts of the generator: set membership (<c>in</c>, <c>notIn</c>) is produced on all seven field types
/// (decimal, date and datetime since Bug #2304 was fixed), and every request it produces is compiled by all eight compilers (the five SQL dialects,
/// MongoDB, and the one Search compiler both Search engines share) and by the real parser and pipeline without a refusal.
/// It also holds the SEC-3 depth guarantee (<c>FilterDepth</c> of a generated tree is at most 8) over a 100,000-case sweep.
/// </summary>
public sealed class ParitySetOperatorTests(ITestOutputHelper output)
{
    private static readonly GridSchema Schema = ParitySchema.Create();

    private static readonly long[] Seeds = [20261010, 1, 402, 604, 720, 1218, 1366];

    private static readonly ISqlDialect[] Dialects =
        [PostgresDialect.Instance, MssqlDialect.Instance, MySqlDialect.Instance, OracleDialect.Instance, SqliteDialect.Instance];

    [Fact]
    public void In_and_notIn_are_generated_per_field_type_on_all_seven_types()
    {
        var seen = new HashSet<(FieldType Type, ComparisonOperator Op)>();
        foreach (var index in Enumerable.Range(0, 200))
        {
            foreach (var comparison in Comparisons(Pipeline(ParityRequestGenerator.Generate(20261010, index)).Filter))
            {
                seen.Add((Schema.Fields[comparison.Field].Type, comparison.Op));
            }
        }

        foreach (var type in Enum.GetValues<FieldType>())
        {
            Assert.Contains((type, ComparisonOperator.In), seen);
            Assert.Contains((type, ComparisonOperator.NotIn), seen);
        }
    }

    // SQL-4 (Bug #2304): the Postgres artifact casts the array of a decimal, date or datetime set to the column type.
    [Theory]
    [InlineData("price", "decimal", "12.50", "", "::numeric[]")]
    [InlineData("born", "date", "2020-01-02", "", "::date[]")]
    [InlineData("seen", "datetime", "2020-01-02T03:04:05.000Z", ",\"zone\":\"UTC\"", "::timestamptz[]")]
    public void A_postgres_set_on_decimal_date_datetime_carries_the_array_cast(string field, string type, string value, string extra, string cast)
    {
        var json = "{\"startRow\":0,\"endRow\":10,\"rowGroupCols\":[],\"valueCols\":[],\"pivotCols\":[],\"pivotMode\":false,"
            + "\"groupKeys\":[],\"sortModel\":[],\"customFilters\":{\"type\":\"comparison\",\"field\":\"" + field
            + "\",\"op\":\"in\",\"values\":[{\"type\":\"" + type + "\",\"value\":\"" + value + "\"" + extra + "}]}}";
        var query = SqlFixtureCompiler.Compile(json, Schema, PostgresDialect.Instance);

        Assert.Contains(cast, query.Sql);
    }

    [Fact]
    public void Every_generated_request_with_a_set_operator_compiles_on_all_eight_compilers()
    {
        var compiled = 0;
        var perType = new Dictionary<FieldType, int>();
        foreach (var seed in Seeds)
        {
            foreach (var index in Enumerable.Range(0, 300))
            {
                var json = ParityRequestGenerator.Generate(seed, index);
                var (request, filter) = Pipeline(json);
                var sets = Comparisons(filter).Where(c => c.Op is ComparisonOperator.In or ComparisonOperator.NotIn).ToList();
                if (sets.Count == 0)
                {
                    continue;
                }

                try
                {
                    CompileEverywhere(json, request, filter);
                }
                catch (Exception exception)
                {
                    throw new InvalidOperationException($"seed {seed} case {index} was refused: {exception.Message}\n{json}", exception);
                }

                compiled++;
                foreach (var type in sets.Select(c => Schema.Fields[c.Field].Type).Distinct())
                {
                    perType[type] = perType.GetValueOrDefault(type) + 1;
                }
            }
        }

        output.WriteLine($"{compiled} set-operator requests compiled on 4 SQL dialects, MongoDB and Search; per type: " +
            string.Join(", ", perType.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}")));
        Assert.True(compiled >= 300);
        foreach (var type in Enum.GetValues<FieldType>())
        {
            Assert.True(perType.GetValueOrDefault(type) >= 10, $"too few {type} set-operator requests compiled");
        }
    }

    [Fact]
    public void A_filter_tree_never_exceeds_SEC3_maximum_depth_over_a_100000_case_sweep()
    {
        const int cases = 100_000;
        const int maxDepth = 8; // ResourceLimits default (SEC-3)
        var deepest = 0;
        foreach (var index in Enumerable.Range(0, cases))
        {
            var seed = Seeds[index % Seeds.Length];
            using var document = JsonDocument.Parse(ParityRequestGenerator.Generate(seed, index / Seeds.Length));
            var depth = FilterDepth(document.RootElement);
            deepest = Math.Max(deepest, depth);
            Assert.True(depth <= maxDepth, $"seed {seed} index {index / Seeds.Length}: filter depth {depth} > {maxDepth}");
        }

        output.WriteLine($"{cases} cases, deepest filter {deepest} (limit {maxDepth})");
        Assert.True(deepest >= 7, "the sweep never reached the worst case, so it proves nothing");
    }

    // The cases that, before the NOT was counted in the tree depth, produced a depth-9 tree that every engine refused.
    [Theory]
    [InlineData(402, 984)]
    [InlineData(604, 522)]
    [InlineData(720, 804)]
    [InlineData(1218, 647)]
    [InlineData(1366, 867)]
    public void The_cases_that_once_exceeded_the_depth_limit_pass_the_real_pipeline(long seed, int index)
    {
        var json = ParityRequestGenerator.Generate(seed, index);
        _ = Pipeline(json); // throws on a depth, leaf, in-list or page-size breach
        using var document = JsonDocument.Parse(json);
        Assert.True(FilterDepth(document.RootElement) <= 8);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// The depth of the generated filter as SEC-3 counts it, from the wire JSON: a customFilters tree counts every node
    /// (a leaf is 1, a NOT, AND or OR adds 1); a native model is at most an AND of columns over an OR of conditions
    /// (3 levels); both together sit under one more AND.
    /// </summary>
    private static int FilterDepth(JsonElement request)
    {
        var tree = request.TryGetProperty("customFilters", out var custom) && custom.ValueKind == JsonValueKind.Object ? NodeDepth(custom) : 0;
        var native = request.TryGetProperty("filterModel", out var model) && model.ValueKind == JsonValueKind.Object ? 3 : 0;
        return tree > 0 && native > 0 ? Math.Max(tree, native) + 1 : Math.Max(tree, native);
    }

    private static int NodeDepth(JsonElement node) =>
        node.GetProperty("type").GetString() switch
        {
            "and" or "or" => 1 + node.GetProperty("nodes").EnumerateArray().Max(NodeDepth),
            "not" => 1 + NodeDepth(node.GetProperty("node")),
            _ => 1
        };

    private static (AgGridParsedRequest Request, FilterNode? Filter) Pipeline(string json)
    {
        var parsed = AgGridRequestParser.Parse(json, new AgGridParseOptions { Schema = Schema });
        var filter = GridFilterPipeline.Process(
            parsed.NativeFilter, parsed.CustomFilters, parsed.Page, Schema, new ResourceLimits(), parsed.Group, parsed.Aggregations);
        return (parsed, filter);
    }

    private static void CompileEverywhere(string json, AgGridParsedRequest parsed, FilterNode? filter)
    {
        foreach (var dialect in Dialects)
        {
            _ = SqlFixtureCompiler.Compile(json, Schema, dialect);
        }

        _ = MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = Schema,
            Filter = filter,
            Sort = parsed.Sort,
            Page = parsed.Page,
            Projection = Schema.FieldOrder
        });

        _ = SearchQueryBuilder.Build(new SearchQueryRequest { Schema = Schema, Filter = filter, Sort = parsed.Sort, Page = parsed.Page });
    }

    private static IEnumerable<ComparisonNode> Comparisons(FilterNode? node) => node switch
    {
        ComparisonNode comparison => [comparison],
        CompositeFilterNode composite => composite.Nodes.SelectMany(Comparisons),
        NotNode not => Comparisons(not.Node),
        _ => []
    };
}
