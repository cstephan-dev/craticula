using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Search.Tests;

/// <summary>
/// BM-3/GRP-6, and in particular the one input this backend's request record needs and the other two
/// do not — the raw search prefix, because OSE-12 renders it as the <c>terms</c> aggregation's
/// <c>include</c> rather than as a query clause.
/// </summary>
public class SearchQuerySetValuesTests
{
    private static GridSchema Sales { get; } = SearchTestSchema.Of(
        ("id", SearchTestSchema.Id),
        ("region", SearchTestSchema.Field(FieldType.String, "region", keyword: "region.keyword")),
        ("segment", SearchTestSchema.Field(FieldType.String, "segment", keyword: "segment.keyword")));

    private static SearchQuery Compile(FilterNode? filter, string? prefix, GridSchema? schema = null) =>
        SearchQueryBuilder.BuildSetValues(new SearchSetValuesRequest
        {
            Schema = schema ?? Sales,
            ColId = "region",
            Filter = filter,
            SearchPrefix = prefix,
            RowLimit = 1000
        });

    private static ComparisonNode Prefix(string text) => new()
    {
        Field = "region",
        Op = ComparisonOperator.StartsWith,
        Value = new StringValue { Value = text }
    };

    /// <summary>
    /// OSE-12: the prefix leaves the query entirely and becomes a Lucene regex on the buckets, so the
    /// compiled body carries no <c>query</c> at all when it was the only predicate — which is exactly
    /// what <c>group-aggregation/018</c> pins.
    /// </summary>
    [Fact]
    public void The_Search_Prefix_Becomes_The_Terms_Include_And_Leaves_The_Query_Empty()
    {
        var query = Compile(Prefix("eu"), "eu");

        Assert.Null(query.Query);
        Assert.Equal(
            """{"region":{"terms":{"field":"region.keyword","include":"eu.*","order":{"_key":"asc"},"size":1000}}}""",
            SearchFixtureCompiler.CanonicalJson(query.Aggs));
    }

    /// <summary>
    /// SEC-5/SEM-7: <c>include</c> is a Lucene regex, so a prefix containing regex syntax must not
    /// widen the bucket filter — <c>.</c> and <c>*</c> and alternation are all live there, and the
    /// last of those is the ReDoS surface SEC-5 excludes user-controlled regex to avoid. The
    /// compiler's own <c>.*</c> suffix is appended AFTER the escape, so the user's literal
    /// <c>.</c> cannot merge into it.
    /// </summary>
    [Fact]
    public void The_Search_Prefix_Is_Escaped_Against_Lucene_Regex_Syntax_Before_The_Suffix_Is_Appended()
    {
        var include = Compile(Prefix("a.b*c|d"), "a.b*c|d")
            .Aggs!["region"]!["terms"]!["include"]!.GetValue<string>();

        Assert.Equal(@"a\.b\*c\|d.*", include);
    }

    /// <summary>
    /// GRP-6(b)+(c) together: another column's filter stays in the query (case-folded per SEM-1,
    /// because a filter selects documents and does not decide bucket identity), while the prefix
    /// moves to <c>include</c>. That combination is <c>group-aggregation/022</c>, and the fact that
    /// the query keeps its <c>case_insensitive</c> beside an exact bucket key is GRP-2's per-clause
    /// granularity — the property MongoDB cannot reproduce (its accepted divergence #3).
    /// </summary>
    [Fact]
    public void Another_Columns_Filter_Stays_In_The_Query_While_The_Prefix_Moves_To_Include()
    {
        var query = Compile(
            new AndNode
            {
                Nodes =
                [
                    new ComparisonNode
                    {
                        Field = "segment",
                        Op = ComparisonOperator.Eq,
                        Value = new StringValue { Value = "Enterprise" }
                    },
                    Prefix("eu")
                ]
            },
            "eu");

        Assert.Equal(
            """{"bool":{"filter":[{"term":{"segment.keyword":{"case_insensitive":true,"value":"Enterprise"}}}]}}""",
            SearchFixtureCompiler.CanonicalJson(query.Query));
        Assert.Equal("eu.*", query.Aggs!["region"]!["terms"]!["include"]!.GetValue<string>());
    }

    /// <summary>
    /// The security property the by-value removal exists for, and the reason it is NOT
    /// <c>SetValuesFilter.ExcludeOwnField</c>: SC-5's static filter is injected AFTER GRP-6(c)'s
    /// prefix and may legitimately carry its own condition on the target column. Removing "every
    /// condition on <c>region</c>" would drop a host's tenancy predicate — a security regression, and
    /// one no fixture would show, since no fixture declares a static filter on a set-values target.
    /// </summary>
    [Fact]
    public void A_Static_Filter_On_The_Target_Column_Survives_The_Prefix_Removal()
    {
        var tenancy = new ComparisonNode
        {
            Field = "region",
            Op = ComparisonOperator.In,
            Values = [new StringValue { Value = "EMEA" }, new StringValue { Value = "APAC" }]
        };

        var query = Compile(new AndNode { Nodes = [Prefix("eu"), tenancy] }, "eu");

        Assert.Equal(
            """{"bool":{"filter":[{"bool":{"minimum_should_match":1,"should":[{"term":{"region.keyword":{"case_insensitive":true,"value":"EMEA"}}},{"term":{"region.keyword":{"case_insensitive":true,"value":"APAC"}}}]}}]}}""",
            SearchFixtureCompiler.CanonicalJson(query.Query));
    }

    /// <summary>
    /// The refusal that guards the whole arrangement: if the predicate this compiler expects
    /// <see cref="GridFilterPipeline.ProcessSetValues"/> to have merged in is not there at all, its
    /// premise no longer holds, and both recoveries — compiling the prefix twice, or dropping it —
    /// are silently wrong answers.
    /// </summary>
    [Fact]
    public void A_Search_Prefix_Whose_Predicate_Is_Missing_From_The_Filter_Is_Refused()
    {
        var exception = Assert.Throws<SearchCompilationException>(() => Compile(filter: null, prefix: "eu"));

        Assert.Contains("it appeared nowhere", exception.Message);
    }

    /// <summary>
    /// The other side of that guard, and the reason it is "at least once" rather than "exactly once":
    /// a DUPLICATE is benign and must not fail compilation. SC-5's static filter is injected AFTER
    /// GRP-6(c)'s prefix, so a host whose static filter happens to spell the identical
    /// <c>startsWith</c> on the same column produces a tree with two equal nodes. Exactly one is
    /// removed — the prefix's — and the host's own copy stays in the query as the ordinary clause it
    /// is. Refusing here would have turned an inert redundancy into a hard failure on a request the
    /// host configured itself.
    /// </summary>
    [Fact]
    public void A_Static_Filter_Spelling_The_Identical_Prefix_Predicate_Is_Kept_Rather_Than_Refused()
    {
        var query = Compile(new AndNode { Nodes = [Prefix("eu"), Prefix("eu")] }, "eu");

        Assert.Equal("eu.*", query.Aggs!["region"]!["terms"]!["include"]!.GetValue<string>());
        Assert.Equal(
            """{"bool":{"filter":[{"wildcard":{"region.keyword":{"case_insensitive":true,"value":"eu*"}}}]}}""",
            SearchFixtureCompiler.CanonicalJson(query.Query));
    }

    /// <summary>GRP-6(d): the cap arrives already clamped; a non-positive one means the caller skipped
    /// <see cref="ResourceLimitsValidator.ResolveSetValuesLimit"/>, and a <c>terms</c> aggregation
    /// with <c>size: 0</c> is rejected by both engines anyway.</summary>
    [Fact]
    public void A_Non_Positive_Cap_Is_Refused()
    {
        var exception = Assert.Throws<SearchCompilationException>(() =>
            SearchQueryBuilder.BuildSetValues(new SearchSetValuesRequest
            {
                Schema = Sales,
                ColId = "region",
                RowLimit = 0
            }));

        Assert.Contains("ResolveSetValuesLimit", exception.Message);
    }

    [Fact]
    public void A_Non_Text_Target_Column_Is_Refused()
    {
        var exception = Assert.Throws<SearchCompilationException>(() =>
            SearchQueryBuilder.BuildSetValues(new SearchSetValuesRequest
            {
                Schema = Sales,
                ColId = "id",
                RowLimit = 1000
            }));

        Assert.Contains("'Int64'", exception.Message);
    }
}
