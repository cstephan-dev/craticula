using System.Text.Json;
using System.Text.RegularExpressions;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Exercises <see cref="SemanticsResolver.DefaultNullsPosition"/> (SEM-3) and
/// <see cref="SemanticsResolver.AppendStableKeyTiebreaker"/> (SEM-6) against the real conformance
/// fixtures authored for this category, <c>spec/fixtures/sort-paging/001-012</c> — mirroring
/// <see cref="ResourceLimitsAdversarialFixtureTests"/>'s pattern of reading real fixture files via
/// <see cref="RepoPaths"/> rather than hardcoding values.
///
/// No AG Grid wire adapter exists yet in this repo (same gap #1632/#1633 left for
/// <c>customFilters</c>), so each fixture's <c>sortModel</c> is hand-adapted into the equivalent
/// <see cref="SortItem"/> list here — exactly the adapter step SEM-3 describes (map <c>sort</c> to
/// <see cref="SortDirection"/>, then fill in the missing <c>nullsPosition</c> signal via
/// <see cref="SemanticsResolver.DefaultNullsPosition"/>, since <c>sortModel</c> itself carries none)
/// — rather than trying to parse AG Grid wire format generically. The resulting per-fixture final
/// sort array (after <see cref="SemanticsResolver.AppendStableKeyTiebreaker"/>) is cross-checked
/// against each fixture's own <c>expected/postgres.artifact.json</c> <c>ORDER BY</c> clause.
/// </summary>
public class SemanticsResolverSortPagingFixtureTests
{
    private const string Category = "sort-paging";

    private static JsonDocument LoadRequest(string fixtureDir)
    {
        var path = RepoPaths.Fixture(Category, fixtureDir, "agGridRequest.json");
        Assert.True(File.Exists(path), $"Fixture request not found at '{path}'.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static GridSchema LoadSchema(string fixtureDir)
    {
        var path = RepoPaths.Fixture(Category, fixtureDir, "schema.json");
        Assert.True(File.Exists(path), $"Fixture schema not found at '{path}'.");
        return GridSchema.Parse(File.ReadAllText(path));
    }

    /// <summary>One key of a real, parsed <c>ORDER BY</c> clause — the physical column actually
    /// compiled (never the logical field name), whether it carries <c>COLLATE craticula_ci</c>,
    /// and its direction/nulls-placement keywords.</summary>
    private sealed record PostgresOrderByKey(string Column, bool HasCollation, string Direction, string NullsPosition);

    private static readonly Regex OrderByClausePattern = new(@"ORDER BY (.+?)(?: LIMIT| OFFSET|$)");

    /// <summary>The double quotes around the column are <b>required</b>, not tolerated: SQL-1 in
    /// <c>spec/semantics.md</c>'s canonical-rendering addendum makes identifier quoting mandatory in
    /// every Postgres artifact, so a fixture that drops them should fail this parse rather than be
    /// silently accepted.</summary>
    private static readonly Regex OrderByKeyPattern = new(@"^""([^""]+)""(?: COLLATE craticula_ci)? (ASC|DESC) NULLS (LAST|FIRST)$");

    /// <summary>
    /// The real oracle this class's own doc comment claims: parses <c>expected/postgres.artifact.json</c>'s
    /// actual <c>ORDER BY</c> clause (never a hand-copied literal), so
    /// <see cref="Fixture_SortModel_Adapts_And_Tiebreaks_Into_The_Expected_Final_Sort_Array"/> binds
    /// <see cref="SemanticsResolver"/>'s real output to something the fixture author actually
    /// authored as the compiled artifact — closing the gap a prior review found (a version of this
    /// method existed that only compared the fixture's own <c>sortModel</c> back to itself, and would
    /// have stayed green even if case-sensitivity's COLLATE decision were wrong for every non-string
    /// field, which it was until that same review caught it in <see cref="SemanticsResolver"/> itself).
    /// </summary>
    private static IReadOnlyList<PostgresOrderByKey> ParsePostgresOrderBy(string fixtureDir)
    {
        var path = RepoPaths.Fixture(Category, fixtureDir, "expected", "postgres.artifact.json");
        Assert.True(File.Exists(path), $"Fixture postgres artifact not found at '{path}'.");
        using var artifact = JsonDocument.Parse(File.ReadAllText(path));
        var sql = artifact.RootElement.GetProperty("sql").GetString()!;

        var orderByMatch = OrderByClausePattern.Match(sql);
        Assert.True(orderByMatch.Success, $"Could not find an ORDER BY clause in '{sql}'.");

        var keys = new List<PostgresOrderByKey>();
        foreach (var clause in orderByMatch.Groups[1].Value.Split(", "))
        {
            var keyMatch = OrderByKeyPattern.Match(clause.Trim());
            Assert.True(keyMatch.Success, $"Could not parse ORDER BY key '{clause}'.");

            keys.Add(new PostgresOrderByKey(
                keyMatch.Groups[1].Value,
                clause.Contains("COLLATE craticula_ci"),
                keyMatch.Groups[2].Value,
                keyMatch.Groups[3].Value));
        }

        return keys;
    }

    /// <summary>The physical SQL column a logical field compiles to — falls back to the field name
    /// itself when no <c>physical.sql.column</c> mapping is declared (this category's fixtures always
    /// declare one, but the fallback keeps this helper honest about what it's doing).</summary>
    private static string PhysicalColumn(GridSchema schema, string field) =>
        schema.Fields[field].Physical?.Sql?.Column ?? field;

    /// <summary>
    /// Hand-adapts <c>sortModel</c>'s <c>{ colId, sort }</c> pairs into <see cref="SortItem"/>s —
    /// the frontend adapter's own job per SEM-3, since <c>sortModel</c> gives no null-placement
    /// signal at all and <see cref="SemanticsResolver.DefaultNullsPosition"/> is exactly the pinned
    /// rule that fills the gap.
    /// </summary>
    private static IReadOnlyList<SortItem> AdaptSortModel(JsonDocument request)
    {
        var sortModel = request.RootElement.GetProperty("sortModel");
        var items = new List<SortItem>();
        foreach (var entry in sortModel.EnumerateArray())
        {
            var direction = entry.GetProperty("sort").GetString() switch
            {
                "asc" => SortDirection.Asc,
                "desc" => SortDirection.Desc,
                var other => throw new JsonException($"Unrecognized sortModel 'sort' value '{other}'.")
            };

            items.Add(new SortItem
            {
                Field = entry.GetProperty("colId").GetString()!,
                Direction = direction,
                NullsPosition = SemanticsResolver.DefaultNullsPosition(direction)
            });
        }

        return items;
    }

    public sealed record ExpectedSortKey(string Field, SortDirection Direction, NullsPosition NullsPosition);

    public static IEnumerable<object[]> Cases()
    {
        yield return
        [
            "001-single-key-string-asc-nulls-last-default",
            new[]
            {
                new ExpectedSortKey("lastName", SortDirection.Asc, NullsPosition.Last),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "002-single-key-int64-asc-nulls-last-default",
            new[]
            {
                new ExpectedSortKey("age", SortDirection.Asc, NullsPosition.Last),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "003-single-key-date-asc-nulls-last-default",
            new[]
            {
                new ExpectedSortKey("hireDate", SortDirection.Asc, NullsPosition.Last),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "004-single-key-string-desc-nulls-first-default",
            new[]
            {
                new ExpectedSortKey("lastName", SortDirection.Desc, NullsPosition.First),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "005-single-key-int64-desc-nulls-first-default",
            new[]
            {
                new ExpectedSortKey("age", SortDirection.Desc, NullsPosition.First),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "006-multi-key-two-fields-order-preserved",
            new[]
            {
                new ExpectedSortKey("department", SortDirection.Asc, NullsPosition.Last),
                new ExpectedSortKey("salary", SortDirection.Desc, NullsPosition.First),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "007-multi-key-three-fields-order-preserved",
            new[]
            {
                new ExpectedSortKey("department", SortDirection.Asc, NullsPosition.Last),
                new ExpectedSortKey("age", SortDirection.Desc, NullsPosition.First),
                new ExpectedSortKey("hireDate", SortDirection.Asc, NullsPosition.Last),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "008-stablekey-tiebreaker-appended-decimal-primary",
            new[]
            {
                new ExpectedSortKey("salary", SortDirection.Asc, NullsPosition.Last),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "009-stablekey-tiebreaker-appended-boolean-primary",
            new[]
            {
                new ExpectedSortKey("isActive", SortDirection.Desc, NullsPosition.First),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "010-stablekey-tiebreaker-not-appended-sort-ends-in-key",
            new[]
            {
                new ExpectedSortKey("lastName", SortDirection.Asc, NullsPosition.Last),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "011-page-range-with-tiebreaker",
            new[]
            {
                new ExpectedSortKey("lastName", SortDirection.Asc, NullsPosition.Last),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
        yield return
        [
            "012-page-none-unpaginated-export",
            new[]
            {
                new ExpectedSortKey("createdAt", SortDirection.Desc, NullsPosition.First),
                new ExpectedSortKey("id", SortDirection.Asc, NullsPosition.Last)
            }
        ];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void Fixture_SortModel_Adapts_And_Tiebreaks_Into_The_Expected_Final_Sort_Array(
        string fixtureDir, ExpectedSortKey[] expected)
    {
        using var request = LoadRequest(fixtureDir);
        var schema = LoadSchema(fixtureDir);
        var adapted = AdaptSortModel(request);

        var result = SemanticsResolver.AppendStableKeyTiebreaker(adapted, schema.StableKey);

        Assert.Equal(expected.Length, result.Count);
        for (var i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Field, result[i].Field);
            Assert.Equal(expected[i].Direction, result[i].Direction);
            Assert.Equal(expected[i].NullsPosition, result[i].NullsPosition);
        }

        // The real oracle: parse the fixture's own compiled ORDER BY clause and bind every key —
        // physical column, direction, nulls placement, AND whether COLLATE is present — to what
        // SemanticsResolver actually decided, not to a second hand-copied literal.
        var orderByKeys = ParsePostgresOrderBy(fixtureDir);
        Assert.Equal(orderByKeys.Count, result.Count);
        for (var i = 0; i < result.Count; i++)
        {
            var sortItem = result[i];
            var orderByKey = orderByKeys[i];

            Assert.Equal(PhysicalColumn(schema, sortItem.Field), orderByKey.Column);
            Assert.Equal(sortItem.Direction == SortDirection.Asc ? "ASC" : "DESC", orderByKey.Direction);
            Assert.Equal(sortItem.NullsPosition == NullsPosition.Last ? "LAST" : "FIRST", orderByKey.NullsPosition);

            var expectedCollation = SemanticsResolver
                .ResolveSortOrGroupKeyCaseSensitivity(schema.Fields[sortItem.Field])
                .Mechanism == SemanticsResolver.CaseSensitivityMechanism.Collation;
            Assert.Equal(expectedCollation, orderByKey.HasCollation);
        }
    }

    [Fact]
    public void Fixture010_SortModel_Already_Ends_In_StableKey_Is_Not_Duplicated()
    {
        using var request = LoadRequest("010-stablekey-tiebreaker-not-appended-sort-ends-in-key");
        var schema = LoadSchema("010-stablekey-tiebreaker-not-appended-sort-ends-in-key");
        var adapted = AdaptSortModel(request);

        var result = SemanticsResolver.AppendStableKeyTiebreaker(adapted, schema.StableKey);

        Assert.Equal(2, result.Count);
        Assert.Same(adapted, result);
    }
}
