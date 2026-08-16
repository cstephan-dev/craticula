using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Search.Tests;

/// <summary>
/// Hand-built <see cref="GridSchema"/>s for the rendering paths no fixture reaches — which on this
/// backend is a real list rather than a formality, because SC-4's three search names
/// (<c>field</c>/<c>keywordSubField</c>/<c>normalizedSubField</c>) can be present in eight
/// combinations and the suite only ever declares four of them.
/// </summary>
internal static class SearchTestSchema
{
    public static GridSchema Of(params (string Name, FieldConfig Config)[] fields) => new()
    {
        Source = "employees",
        StableKey = "id",
        Fields = fields.ToDictionary(field => field.Name, field => field.Config, StringComparer.Ordinal)
    };

    public static FieldConfig Id { get; } = Field(FieldType.Int64);

    public static FieldConfig Field(
        FieldType type,
        string? searchField = null,
        string? keyword = null,
        string? normalized = null,
        CaseSensitivity? caseSensitivity = null,
        SearchMode? searchMode = null,
        IReadOnlyList<AggregationFunc>? aggregatable = null) => new()
        {
            Type = type,
            Groupable = true,
            Aggregatable = aggregatable ?? [],
            Physical = searchField is null && keyword is null && normalized is null
                ? null
                : new PhysicalMapping
                {
                    Search = new SearchMapping
                    {
                        Field = searchField ?? "unused",
                        KeywordSubField = keyword,
                        NormalizedSubField = normalized
                    }
                },
            Text = caseSensitivity is null && searchMode is null
                ? null
                : new TextOptions
                {
                    CaseSensitivity = caseSensitivity ?? CaseSensitivity.Insensitive,
                    SearchMode = searchMode ?? SearchMode.Substring
                }
        };

    public static RangePage FirstPage { get; } = new() { StartRow = 0, EndRow = 100 };

    public static ComparisonNode Compare(string field, ComparisonOperator op, TypedValue? value = null) =>
        new() { Field = field, Op = op, Value = value };

    public static SortItem Ascending(string field) =>
        new() { Field = field, Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last };
}
