using System.Text.Json;
using System.Text.Json.Nodes;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Search.Tests;

/// <summary>
/// OSE-0: object key order is not part of the artifact contract, so the conformance comparison
/// canonicalises it — which would leave the compiler's OWN order unpinned if nothing else asserted
/// it. This class is that something else, and it is the one place in the Search suite that compares
/// raw text.
/// </summary>
public class SearchQueryShapeTests
{
    private static GridSchema Schema { get; } = SearchTestSchema.Of(
        ("id", SearchTestSchema.Id),
        ("region", SearchTestSchema.Field(FieldType.String, "region", keyword: "region.keyword")),
        ("amount", SearchTestSchema.Field(
            FieldType.Decimal, "amount", aggregatable: [AggregationFunc.Sum])));

    private static string Text(JsonNode node) => node.ToJsonString(new JsonSerializerOptions { WriteIndented = false });

    /// <summary>
    /// A hit-returning body: <c>query</c>, <c>sort</c>, <c>from</c>, <c>size</c>. Determinism here is
    /// what makes compiler output diffable against a fixture by eye even though the comparison itself
    /// ignores the order — and it is a G4 property in its own right, since four language ports
    /// serialising the same members in four orders would produce four different files from one IR.
    /// </summary>
    [Fact]
    public void A_Hit_Request_Emits_Query_Sort_From_Size_In_That_Order()
    {
        var query = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = Schema,
            Filter = new ComparisonNode
            {
                Field = "region",
                Op = ComparisonOperator.Eq,
                Value = new StringValue { Value = "EMEA" }
            },
            Page = new RangePage { StartRow = 20, EndRow = 40 }
        });

        Assert.Equal(
            """{"query":{"term":{"region.keyword":{"value":"EMEA","case_insensitive":true}}},"sort":[{"id":{"order":"asc","missing":"_last"}}],"from":20,"size":20}""",
            Text(query.ToArtifact()));
    }

    /// <summary>
    /// A bucket-returning body: <c>query</c> (when there is one), <c>size</c>, <c>aggs</c> — the same
    /// order with the members that do not apply simply absent, rather than a second layout rule.
    /// </summary>
    [Fact]
    public void A_Bucket_Request_Emits_Size_And_Aggs_And_No_Sort_Or_From()
    {
        var query = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = Schema,
            Page = SearchTestSchema.FirstPage,
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] },
            Aggregations = [new Aggregation { Field = "amount", Func = AggregationFunc.Sum }]
        });

        Assert.Equal(
            """{"size":0,"aggs":{"region":{"composite":{"size":100,"sources":[{"region":{"terms":{"field":"region.keyword","missing_bucket":true}}}]},"aggs":{"amount_sum":{"sum":{"field":"amount"}}}}}}""",
            Text(query.ToArtifact()));
    }

    /// <summary>
    /// OSE-3: an ordinary row query with no predicate carries an explicit <c>match_all</c> rather
    /// than omitting the member — the form 13 in-scope artifacts pin — while a bucket request with no
    /// predicate omits <c>query</c> entirely, which 6 others pin. Both spellings mean the same thing
    /// to the engine; the point is that only one of them is emitted for each request shape.
    /// </summary>
    [Fact]
    public void An_Empty_Filter_Is_Match_All_On_A_Hit_Request_And_Absent_On_A_Bucket_Request()
    {
        var hits = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = Schema,
            Page = SearchTestSchema.FirstPage
        });

        Assert.Equal("""{"match_all":{}}""", Text(hits.Query!));

        var buckets = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = Schema,
            Page = SearchTestSchema.FirstPage,
            Group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] }
        });

        Assert.Null(buckets.Query);
    }

    /// <summary>
    /// <see cref="JsonNode"/> is single-parented, so handing out the live nodes would make the second
    /// caller's assignment throw. <c>MongoQuery.ToArtifact</c> has the identical trap and the
    /// identical answer; asserted rather than trusted because the failure is a
    /// <see cref="InvalidOperationException"/> from deep inside System.Text.Json, far from its cause.
    /// </summary>
    [Fact]
    public void ToArtifact_Returns_A_Fresh_Clone_Every_Time()
    {
        var query = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = Schema,
            Page = SearchTestSchema.FirstPage
        });

        var first = query.ToArtifact();
        var second = query.ToArtifact();

        Assert.NotSame(first, second);
        Assert.Equal(Text(first), Text(second));

        // Mutating one must not disturb the other, nor the query's own nodes.
        first["size"] = 7;
        Assert.Equal(100, second["size"]!.GetValue<int>());
        Assert.Equal(100, query.Size);
    }

    /// <summary>SQL-0: the index comes from <c>GridSchema.Source</c> and is carried beside the
    /// artifact rather than inside it — a host needs it to build the URL, and no request may supply
    /// one.</summary>
    [Fact]
    public void The_Index_Comes_From_The_Schema_And_Is_Not_Part_Of_The_Artifact()
    {
        var query = SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = Schema,
            Page = SearchTestSchema.FirstPage
        });

        Assert.Equal("employees", query.Index);
        Assert.DoesNotContain("employees", Text(query.ToArtifact()), StringComparison.Ordinal);
    }
}
