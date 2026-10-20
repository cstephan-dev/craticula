using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Search.Tests;

/// <summary>
/// SEM-3/SEM-6/AG-3 on the paths the fixture suite does not reach — a case-sensitive string sort key,
/// a case-insensitive one with no normalizer sub-field, a repeated key, and the one excluded fixture
/// whose builder half is asserted here instead.
/// </summary>
public class SearchQuerySortAndPagingTests
{
    private static SearchQuery Compile(GridSchema schema, IReadOnlyList<SortItem>? sort, PageSpec? page = null) =>
        SearchQueryBuilder.Build(new SearchQueryRequest
        {
            Schema = schema,
            Sort = sort,
            Page = page ?? SearchTestSchema.FirstPage
        });

    private static GridSchema Names { get; } = SearchTestSchema.Of(
        ("id", SearchTestSchema.Id),
        ("lastName", SearchTestSchema.Field(
            FieldType.String, "lastName", keyword: "lastName.keyword", normalized: "lastName.normalized")),
        ("employeeCode", SearchTestSchema.Field(
            FieldType.String, "employeeCode", keyword: "employeeCode.keyword",
            caseSensitivity: CaseSensitivity.Sensitive)),
        ("bareText", SearchTestSchema.Field(FieldType.String, "bareText", keyword: "bareText.keyword")));

    /// <summary>
    /// SEM-1's "same class of mechanism in every context on that field", read across the two
    /// directions: a case-insensitive string sorts on the index-time-folded sub-field (the same one
    /// its <c>range</c> comparison uses), a case-sensitive one on the exact-match sub-field (the same
    /// one its <c>term</c> uses), and a non-text key on the field itself. Only the first is pinned by
    /// fixtures (<c>sort-paging/001</c>, <c>004</c>, <c>006</c>, <c>007</c>, <c>010</c>).
    /// </summary>
    [Fact]
    public void A_Sort_Key_Takes_The_Same_Sub_Field_Its_Own_Comparisons_Would()
    {
        var sort = Compile(Names, [
            SearchTestSchema.Ascending("lastName"),
            SearchTestSchema.Ascending("employeeCode")
        ]).Sort;

        Assert.Equal(
            """[{"lastName.normalized":{"missing":"_last","order":"asc"}},{"employeeCode.keyword":{"missing":"_last","order":"asc"}},{"id":{"missing":"_last","order":"asc"}}]""",
            SearchFixtureCompiler.CanonicalJson(sort));
    }

    [Fact]
    public void A_Case_Insensitive_String_Sort_Key_With_No_Normalizer_Sub_Field_Is_Refused()
    {
        var exception = Assert.Throws<SearchCompilationException>(
            () => Compile(Names, [SearchTestSchema.Ascending("bareText")]));

        Assert.Contains("normalizedSubField", exception.Message);
        Assert.Contains("sort key", exception.Message);
    }

    /// <summary>
    /// A colId the grid sent twice never reaches the compiler as two keys: SEM-6 (Bug #2565) drops the later occurrence, and with it
    /// MDB-10's first collision, so nothing is rejected here either. The DSL's <c>sort</c> is an ARRAY, where a repeat would only
    /// have been redundant; the reduction makes all three arms agree and keeps the first occurrence, the user's primary key.
    /// </summary>
    [Fact]
    public void A_Repeated_Sort_Key_Is_Dropped_And_The_First_Occurrence_Stays()
    {
        var sort = Compile(Names, [
            new SortItem { Field = "id", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last },
            new SortItem { Field = "id", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First }
        ]).Sort;

        Assert.Equal(
            """[{"id":{"missing":"_last","order":"asc"}}]""",
            SearchFixtureCompiler.CanonicalJson(sort));
    }

    [Fact]
    public void A_Key_After_The_StableKey_Is_Dropped()
    {
        var sort = Compile(Names, [
            new SortItem { Field = "id", Direction = SortDirection.Desc, NullsPosition = NullsPosition.First },
            new SortItem { Field = "lastName", Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }
        ]).Sort;

        Assert.Equal(
            """[{"id":{"missing":"_first","order":"desc"}}]""",
            SearchFixtureCompiler.CanonicalJson(sort));
    }

    [Fact]
    public void A_Range_Page_Emits_From_And_Size_Including_The_Zero_Offset()
    {
        var query = Compile(Names, null, new RangePage { StartRow = 20, EndRow = 40 });

        Assert.Equal(20, query.From);
        Assert.Equal(20, query.Size);

        var first = Compile(Names, null, new RangePage { StartRow = 0, EndRow = 50 });

        Assert.Equal(0, first.From);
        Assert.Equal(50, first.Size);
    }
}
