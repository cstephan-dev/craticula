using System.Text.Json;
using Craticula.Core.Grid;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// #1649's real logic, tested directly against hand-built <see cref="GridPageResult"/> instances rather
/// than through any one adapter — the four adapters' own conversion (<c>ToGridPageResult()</c>) is
/// exercised separately, once per adapter test project, precisely so this file can stay adapter-agnostic
/// and cover every branch of the mapping without needing a database.
/// </summary>
public class LoadSuccessParamsMapperTests
{
    private static GridPageResult Result(
        IReadOnlyList<string>? columns = null,
        IReadOnlyList<IReadOnlyList<object?>>? rows = null,
        long? rowCount = null,
        bool? isLastPage = null,
        bool rowCountIsApproximate = false) => new()
        {
            Columns = columns ?? ["id", "name"],
            Rows = rows ?? [[1L, "Alice"], [2L, "Bob"]],
            RowCount = rowCount,
            IsLastPage = isLastPage,
            RowCountIsApproximate = rowCountIsApproximate
        };

    // ---- lastRow derivation --------------------------------------------------------------------------

    /// <summary>AG-3's Exact strategy: the wire <c>lastRow</c> is the known total directly, with no
    /// arithmetic involved.</summary>
    [Fact]
    public void LastRow_Comes_Directly_From_RowCount_When_Known()
    {
        var mapped = LoadSuccessParamsMapper.Map(Result(rowCount: 42), startRow: 0);

        Assert.Equal(42, mapped.LastRow);
    }

    /// <summary>AG-3's Lazy strategy: when only <see cref="GridPageResult.IsLastPage"/> is known and
    /// <see langword="true"/>, the wire <c>lastRow</c> is derived as <c>startRow + rows.Count</c> — the
    /// arithmetic <c>OpenSearchGridPage</c>'s own doc comment explicitly hands off to this story.</summary>
    [Fact]
    public void LastRow_Is_Derived_From_IsLastPage_And_StartRow_Plus_RowCount_When_True()
    {
        var rows = new IReadOnlyList<object?>[] { [1L, "Alice"], [2L, "Bob"], [3L, "Carol"] };
        var mapped = LoadSuccessParamsMapper.Map(Result(rows: rows, isLastPage: true), startRow: 20);

        Assert.Equal(23, mapped.LastRow);
    }

    /// <summary>A short-of-the-end page tells AG Grid nothing new: the wire <c>lastRow</c> stays
    /// <see langword="null"/>, which is the grid's own "keep asking" signal.</summary>
    [Fact]
    public void LastRow_Stays_Null_When_IsLastPage_Is_False()
    {
        var mapped = LoadSuccessParamsMapper.Map(Result(isLastPage: false), startRow: 0);

        Assert.Null(mapped.LastRow);
    }

    /// <summary>When both signals are present and <c>RowCount</c> is EXACT (not flagged approximate),
    /// it wins over <c>IsLastPage</c> — the more informative answer (the actual total, not just "was
    /// this page short"). This is EF/Dapper/Mongo's own Exact strategy, layered with a hypothetical
    /// (today unreached) exact <c>IsLastPage == true</c> — see
    /// <see cref="Exact_RowCount_Wins_Over_IsLastPage_True_Even_Though_IsLastPage_Would_Also_Be_Exact"/>.</summary>
    [Fact]
    public void RowCount_Wins_Over_IsLastPage_When_Both_Present_And_RowCount_Is_Exact()
    {
        var rows = new IReadOnlyList<object?>[] { [1L, "Alice"] };
        var mapped = LoadSuccessParamsMapper.Map(
            Result(rows: rows, rowCount: 99, isLastPage: true, rowCountIsApproximate: false), startRow: 0);

        Assert.Equal(99, mapped.LastRow);
    }

    /// <summary>Same case restated with a name that states the invariant directly: an exact
    /// <c>RowCount</c> is authoritative regardless of what <c>IsLastPage</c> says, because
    /// <c>IsLastPage</c> can only ever contribute an equally-exact answer — there is nothing for it to
    /// correct.</summary>
    [Fact]
    public void Exact_RowCount_Wins_Over_IsLastPage_True_Even_Though_IsLastPage_Would_Also_Be_Exact()
    {
        var rows = new IReadOnlyList<object?>[] { [1L, "Alice"], [2L, "Bob"], [3L, "Carol"] };
        var mapped = LoadSuccessParamsMapper.Map(
            Result(rows: rows, rowCount: 500, isLastPage: true, rowCountIsApproximate: false), startRow: 20);

        // startRow + rows.Count (23) would also be exact here, but the exact RowCount (500) still wins.
        Assert.Equal(500, mapped.LastRow);
    }

    /// <summary>THE BUG CASE (both reviewers, independently): OpenSearch's grouped <c>cardinality</c>
    /// path can report an APPROXIMATE <c>RowCount</c> at the same time <c>IsLastPage == true</c> supplies
    /// an EXACT derived total (<c>startRow + rows.Count</c>, from the AG-3 Lazy fetch-one-extra-and-trim
    /// probe, which is always exact). The exact derived value must win — reporting the HyperLogLog++
    /// approximation as the wire <c>lastRow</c> here would silently under/over-report the true total.</summary>
    [Fact]
    public void Exact_Derived_Value_Wins_Over_Approximate_RowCount_When_IsLastPage_True()
    {
        var rows = new IReadOnlyList<object?>[] { [1L, "Alice"], [2L, "Bob"], [3L, "Carol"] };
        var mapped = LoadSuccessParamsMapper.Map(
            Result(rows: rows, rowCount: 2_900, isLastPage: true, rowCountIsApproximate: true), startRow: 20);

        Assert.Equal(23, mapped.LastRow);
    }

    /// <summary>The bug-case fix must also flip <c>groupLevelInfo</c>: once the exact derived value wins
    /// over the approximate <c>RowCount</c>, the wire <c>lastRow</c> IS exact, so reporting
    /// <c>rowCountExact: false</c> for it would be self-contradictory.</summary>
    [Fact]
    public void GroupLevelInfo_Reports_RowCountExact_True_When_The_Exact_Derived_Value_Won()
    {
        var rows = new IReadOnlyList<object?>[] { [1L, "Alice"] };
        var mapped = LoadSuccessParamsMapper.Map(
            Result(rows: rows, rowCount: 2_900, isLastPage: true, rowCountIsApproximate: true), startRow: 20);

        Assert.NotNull(mapped.GroupLevelInfo);
        Assert.True(mapped.GroupLevelInfo!.RowCountExact);
    }

    /// <summary>THE SHRUNK-LEVEL CASE (verification-review finding): a grouped level that shrank between
    /// requests can walk out of buckets before ever reaching a stale <c>startRow</c>, returning
    /// <c>(Rows: [], IsLastPage: true)</c> with <c>startRow &gt; 0</c>. The derived arithmetic
    /// (<c>startRow + 0</c>) is NOT grounded in real data here, so it must not be trusted to override the
    /// approximate <c>RowCount</c> — doing so would emit a spuriously "exact" <c>lastRow</c> that is
    /// actually smaller than the true total, which is worse than the honest approximate answer it would
    /// have replaced.</summary>
    [Fact]
    public void Approximate_RowCount_Is_Used_When_IsLastPage_True_But_Rows_Empty_And_StartRow_Positive()
    {
        var mapped = LoadSuccessParamsMapper.Map(
            Result(rows: [], rowCount: 2_900, isLastPage: true, rowCountIsApproximate: true), startRow: 20);

        Assert.Equal(2_900, mapped.LastRow);
    }

    /// <summary>The shrunk-level fallback case must also report itself as inexact — the approximate
    /// <c>RowCount</c> won, not a derived exact value, so <c>rowCountExact: false</c> is the honest
    /// answer here too.</summary>
    [Fact]
    public void GroupLevelInfo_Reports_RowCountExact_False_When_IsLastPage_True_But_Rows_Empty_And_StartRow_Positive()
    {
        var mapped = LoadSuccessParamsMapper.Map(
            Result(rows: [], rowCount: 2_900, isLastPage: true, rowCountIsApproximate: true), startRow: 20);

        Assert.NotNull(mapped.GroupLevelInfo);
        Assert.False(mapped.GroupLevelInfo!.RowCountExact);
    }

    /// <summary>The shrunk-level guard's counterpart: an empty result at <c>startRow == 0</c> genuinely
    /// does mean "zero total, done" — the derived exact value stays trusted here, since there is no
    /// stale offset it could have walked past.</summary>
    [Fact]
    public void Exact_Derived_Value_Wins_Over_Approximate_RowCount_When_Rows_Empty_But_StartRow_Zero()
    {
        var mapped = LoadSuccessParamsMapper.Map(
            Result(rows: [], rowCount: 2_900, isLastPage: true, rowCountIsApproximate: true), startRow: 0);

        Assert.Equal(0, mapped.LastRow);
        Assert.NotNull(mapped.GroupLevelInfo);
        Assert.True(mapped.GroupLevelInfo!.RowCountExact);
    }

    /// <summary>When <c>RowCount</c> is approximate and <c>IsLastPage</c> is <see langword="false"/>, no
    /// better signal exists — the approximate <c>RowCount</c> must still be used rather than regressing
    /// to <see langword="null"/>: an approximate "keep going" answer beats no answer at all.</summary>
    [Fact]
    public void Approximate_RowCount_Is_Still_Used_When_IsLastPage_Is_False()
    {
        var mapped = LoadSuccessParamsMapper.Map(
            Result(rowCount: 2_900, isLastPage: false, rowCountIsApproximate: true), startRow: 0);

        Assert.Equal(2_900, mapped.LastRow);
    }

    /// <summary>Same as above, restated for <c>IsLastPage</c> left <see langword="null"/> (unknown)
    /// rather than explicitly <see langword="false"/> — both mean "no exact derived alternative".</summary>
    [Fact]
    public void Approximate_RowCount_Is_Still_Used_When_IsLastPage_Is_Unknown()
    {
        var mapped = LoadSuccessParamsMapper.Map(
            Result(rowCount: 2_900, isLastPage: null, rowCountIsApproximate: true), startRow: 0);

        Assert.Equal(2_900, mapped.LastRow);
    }

    /// <summary>The approximate-RowCount-used case must still report itself as inexact — this is the one
    /// case where <c>rowCountExact: false</c> is the correct, honest answer.</summary>
    [Fact]
    public void GroupLevelInfo_Reports_RowCountExact_False_When_The_Approximate_RowCount_Was_Used()
    {
        var mapped = LoadSuccessParamsMapper.Map(
            Result(rowCount: 2_900, isLastPage: false, rowCountIsApproximate: true), startRow: 0);

        Assert.NotNull(mapped.GroupLevelInfo);
        Assert.False(mapped.GroupLevelInfo!.RowCountExact);
    }

    /// <summary><c>RowCount</c> unknown but <c>IsLastPage == true</c>: the exact derived value is used,
    /// same as the ordinary Lazy case above — restated here to keep the precedence table's "RowCount
    /// null" row explicit and separately tested from the "RowCount known" rows.</summary>
    [Fact]
    public void Exact_Derived_Value_Is_Used_When_RowCount_Is_Null_And_IsLastPage_True()
    {
        var rows = new IReadOnlyList<object?>[] { [1L, "Alice"], [2L, "Bob"], [3L, "Carol"] };
        var mapped = LoadSuccessParamsMapper.Map(Result(rows: rows, isLastPage: true), startRow: 20);

        Assert.Equal(23, mapped.LastRow);
    }

    /// <summary>The shrunk-level guard is specific to overriding an APPROXIMATE <c>RowCount</c> — it does
    /// not apply here, since there is no approximate fallback to protect when <c>RowCount</c> is null
    /// entirely. The derived value remains the only stop signal available, even for this same empty-page,
    /// positive-<c>startRow</c> shape.</summary>
    [Fact]
    public void Exact_Derived_Value_Is_Used_When_RowCount_Is_Null_Even_With_Empty_Rows_And_Positive_StartRow()
    {
        var mapped = LoadSuccessParamsMapper.Map(Result(rows: [], isLastPage: true), startRow: 20);

        Assert.Equal(20, mapped.LastRow);
    }

    /// <summary>Neither strategy answered anything (an unrequested count on an adapter with no Lazy
    /// probe, e.g. EF Core/Dapper/MongoDB with <c>includeRowCount: false</c>): the wire <c>lastRow</c>
    /// stays <see langword="null"/> rather than defaulting to some guessed value.</summary>
    [Fact]
    public void LastRow_Stays_Null_When_Neither_Signal_Is_Known()
    {
        var mapped = LoadSuccessParamsMapper.Map(Result(), startRow: 0);

        Assert.Null(mapped.LastRow);
    }

    // ---- row transposition ----------------------------------------------------------------------------

    /// <summary>The actual OQ-8 work: positional cells become plain, colId-keyed objects — AG Grid's real
    /// wire row shape, not the internal shape every adapter's own page type uses.</summary>
    [Fact]
    public void Rows_Are_Transposed_From_Positional_Cells_Into_ColId_Keyed_Objects()
    {
        var columns = new[] { "id", "name" };
        var rows = new IReadOnlyList<object?>[] { [1L, "Alice"], [2L, "Bob"] };

        var mapped = LoadSuccessParamsMapper.Map(Result(columns: columns, rows: rows), startRow: 0);

        Assert.Equal(2, mapped.Rows.Count);
        Assert.Equal(1L, mapped.Rows[0]["id"]);
        Assert.Equal("Alice", mapped.Rows[0]["name"]);
        Assert.Equal(2L, mapped.Rows[1]["id"]);
        Assert.Equal("Bob", mapped.Rows[1]["name"]);
    }

    /// <summary>GRP-3's grouped-level column shape (<c>[groupColumn, ...aggregation outputs]</c>)
    /// transposes exactly the same way as an ordinary field list — the mapper has no grouping-specific
    /// branch, by design.</summary>
    [Fact]
    public void A_Grouped_Levels_Column_List_Transposes_Like_Any_Other()
    {
        var columns = new[] { "region", "totalAmount", "orderCount" };
        var rows = new IReadOnlyList<object?>[] { ["EMEA", 150.50m, 3L], ["APAC", 50m, 1L] };

        var mapped = LoadSuccessParamsMapper.Map(Result(columns: columns, rows: rows), startRow: 0);

        Assert.Equal("EMEA", mapped.Rows[0]["region"]);
        Assert.Equal(150.50m, mapped.Rows[0]["totalAmount"]);
        Assert.Equal(3L, mapped.Rows[0]["orderCount"]);
        Assert.Equal("APAC", mapped.Rows[1]["region"]);
    }

    /// <summary>Zero rows is an ordinary page, not an edge case the mapper special-cases — an empty
    /// array, never <see langword="null"/>.</summary>
    [Fact]
    public void Zero_Rows_Maps_To_An_Empty_Array_Not_Null()
    {
        var mapped = LoadSuccessParamsMapper.Map(Result(rows: []), startRow: 0);

        Assert.NotNull(mapped.Rows);
        Assert.Empty(mapped.Rows);
    }

    // ---- groupLevelInfo ---------------------------------------------------------------------------------

    /// <summary>The overwhelming majority of responses: no approximate count, so no
    /// <c>groupLevelInfo</c> at all — matching AG Grid's own expectation that the member is usually
    /// absent.</summary>
    [Fact]
    public void GroupLevelInfo_Is_Null_When_RowCountIsApproximate_Is_False()
    {
        var mapped = LoadSuccessParamsMapper.Map(Result(rowCountIsApproximate: false), startRow: 0);

        Assert.Null(mapped.GroupLevelInfo);
    }

    /// <summary>OpenSearch's grouped <c>cardinality</c> path, surfaced: <c>groupLevelInfo</c> appears with
    /// exactly <c>{ rowCountExact: false }</c>, the one shape <see cref="LoadSuccessParamsMapper"/> ever
    /// produces.</summary>
    [Fact]
    public void GroupLevelInfo_Is_Populated_With_RowCountExact_False_When_RowCountIsApproximate_Is_True()
    {
        var mapped = LoadSuccessParamsMapper.Map(Result(rowCountIsApproximate: true), startRow: 0);

        Assert.NotNull(mapped.GroupLevelInfo);
        Assert.False(mapped.GroupLevelInfo!.RowCountExact);
    }

    // ---- serialization contract -------------------------------------------------------------------------

    /// <summary>
    /// The specific regression this design exists to prevent: <see cref="LoadSuccessParams"/> is the
    /// first type in the repo serialized by a HOST's own <see cref="JsonSerializerOptions"/> rather than
    /// one this project fully controls, so a host's global <c>PropertyNamingPolicy</c> must not be able to
    /// silently rewrite AG Grid's exact camelCase wire contract. Proven directly against a deliberately
    /// non-default naming policy rather than trusting the <c>[JsonPropertyName]</c> attributes are simply
    /// present.
    /// </summary>
    [Fact]
    public void Serialization_Keeps_Exact_Wire_Casing_Regardless_Of_A_Hosts_Naming_Policy()
    {
        var mapped = LoadSuccessParamsMapper.Map(Result(rowCountIsApproximate: true, rowCount: 2), startRow: 0);
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower };

        var json = JsonSerializer.Serialize(mapped, options);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;

        Assert.True(root.TryGetProperty("rows", out _));
        Assert.True(root.TryGetProperty("lastRow", out _));
        Assert.True(root.TryGetProperty("groupLevelInfo", out var groupLevelInfo));
        Assert.True(groupLevelInfo.TryGetProperty("rowCountExact", out _));

        // None of the naming-policy-rewritten forms leaked through.
        Assert.False(root.TryGetProperty("last_row", out _));
        Assert.False(root.TryGetProperty("group_level_info", out _));
        Assert.False(groupLevelInfo.TryGetProperty("row_count_exact", out _));
    }
}
