using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Mongo.Tests;

/// <summary>
/// BM-3/GRP-6 on MongoDB: the distinct-values pipeline a set filter's value list compiles to.
/// </summary>
public class MongoPipelineSetValuesTests
{
    private static readonly GridSchema Schema = new()
    {
        Source = "sales_fact",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.String },
            ["region"] = new() { Type = FieldType.String },
            ["segment"] = new() { Type = FieldType.String }
        }
    };

    private static MongoQuery Compile(FilterNode? filter = null, int rowLimit = 1000) =>
        MongoPipelineBuilder.BuildSetValues(new MongoSetValuesRequest
        {
            Schema = Schema,
            ColId = "region",
            Filter = filter,
            RowLimit = rowLimit
        });

    private static string Json(MongoQuery query) => MongoFixtureCompiler.CanonicalJson(query.ToArtifact());

    [Fact]
    public void The_Whole_Pipeline_Is_Group_Then_Sort_Then_Limit_With_No_Skip()
    {
        // GRP-6(e): the cap is a ceiling, not a window — the list is fetched once, so there is no
        // $skip. And no SEM-6 tiebreaker: the distinct key is unique across the result documents by
        // construction, and the schema's stableKey is not in the output at all.
        Assert.Equal(
            """[{"$group":{"_id":"$region"}},{"$addFields":{"__groupNullBucket":{"$cond":[{"$eq":["$_id",null]},1,0]}}},{"$sort":{"__groupNullBucket":1,"_id":1}},{"$unset":"__groupNullBucket"},{"$limit":1000}]""",
            Json(Compile()));
    }

    [Fact]
    public void The_Distinct_Key_Is_Exact_Even_On_A_Case_Insensitive_String()
    {
        // GRP-6(a): a distinct set is a set of buckets with no aggregate attached, and its entries go
        // straight back to the grid as an `in` value or a groupKey that GRP-2 then matches exactly. A
        // folded entry would have an unspecified label and would select neither original document.
        //
        // Note this is the case that survived three stories: 017's and 022's Mongo arms carried a
        // query-level collation beside a $group on _id, because a request with no groupSpec did not
        // look "grouped". The test is whether the pipeline computes a distinct SET, not whether the
        // request carries group state.
        var query = Compile();

        Assert.Null(query.Collation);

        // ...and with no user filter there was nothing that WANTED one, so this is genuine absence
        // rather than suppression. The next test is the one that exercises the conflict.
        Assert.False(query.CollationSuppressedByBucketIdentity);
    }

    [Fact]
    public void A_Filter_On_Another_Column_Still_Compiles_To_A_Match_And_Still_Suppresses_The_Collation()
    {
        // Accepted divergence #3's THIRD shape, the one that is easiest to miss because the
        // conflicting pair sits on two different fields: SEM-1 wants the collation for `segment` and
        // GRP-6(a) forbids it for `region`, and MongoDB has one switch for both.
        var query = Compile(new ComparisonNode
        {
            Field = "segment",
            Op = ComparisonOperator.Eq,
            Value = new StringValue { Value = "Enterprise" }
        });

        Assert.StartsWith("""[{"$match":{"segment":{"$eq":"Enterprise"}}},{"$group":{"_id":"$region"}}""", Json(query));
        Assert.Null(query.Collation);
        Assert.True(query.CollationSuppressedByBucketIdentity);
    }

    [Fact]
    public void The_Row_Limit_Is_Emitted_Verbatim_Because_It_Arrives_Already_Clamped()
    {
        // GRP-6(d): the cap is applied, never enforced — a client asking for more values than the
        // host will serve has not malformed anything, so ResourceLimitsValidator clamps upstream and
        // this compiler renders whatever number it produced.
        Assert.EndsWith("""{"$limit":10000}]""", Json(Compile(rowLimit: 10000)));
    }

    [Fact]
    public void A_Non_Positive_Cap_Is_Refused_Rather_Than_Rendered()
    {
        var failure = Assert.Throws<MongoCompilationException>(() => Compile(rowLimit: 0));

        Assert.Contains("ResolveSetValuesLimit", failure.Message);
    }

    [Fact]
    public void An_Unknown_Target_Column_Is_A_Caller_Precondition_Failure()
    {
        var failure = Assert.Throws<MongoCompilationException>(() => MongoPipelineBuilder.BuildSetValues(
            new MongoSetValuesRequest { Schema = Schema, ColId = "nope", RowLimit = 1000 }));

        Assert.Contains("not declared in the GridSchema", failure.Message);
    }
}
