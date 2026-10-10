using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Search.Tests;

/// <summary>
/// The datetime bound rendering: padded to three fractional digits when it has no sub-millisecond part (OSE-8),
/// kept exactly as given otherwise (a finer bound is never truncated).
/// </summary>
public class SearchJsonInstantTests
{
    [Theory]
    [InlineData("2026-01-01T00:00:00Z", "2026-01-01T00:00:00.000Z")]
    [InlineData("2026-01-01T00:00:00.001Z", "2026-01-01T00:00:00.001Z")]
    [InlineData("2026-01-01T00:00:00.0005Z", "2026-01-01T00:00:00.0005Z")]
    public void A_Datetime_Bound_Is_Padded_To_Milliseconds_Or_Keeps_Its_Finer_Digits(string given, string expected)
    {
        var schema = SearchTestSchema.Of(
            ("id", SearchTestSchema.Id),
            ("createdAt", SearchTestSchema.Field(FieldType.Datetime, "createdAt")));

        var compiled = SearchFixtureCompiler.CanonicalJson(SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = schema,
            Filter = SearchTestSchema.Compare("createdAt", ComparisonOperator.Lt, new DatetimeValue { Value = given, Zone = "UTC" }),
            Page = SearchTestSchema.FirstPage
        }).Query);

        Assert.Contains($"\"lt\":\"{expected}\"", compiled);
    }
}
