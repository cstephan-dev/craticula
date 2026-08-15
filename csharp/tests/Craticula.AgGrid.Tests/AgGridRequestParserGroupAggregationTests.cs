using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Hand-built boundary tests for <see cref="AgGridRequestParser.Parse"/>'s <c>rowGroupCols</c>/
/// <c>groupKeys</c>/<c>valueCols</c> dispatch (BM-2/AR-8) — real-fixture-driven coverage lives in
/// <see cref="AgGridRequestParserGroupAggregationFixtureTests"/>.
/// </summary>
public class AgGridRequestParserGroupAggregationTests
{
    private static AgGridParsedRequest Parse(string request) => AgGridRequestParser.Parse(request);

    [Fact]
    public void Empty_RowGroupCols_Produces_Null_Group()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        Assert.Null(Parse(request).Group);
    }

    [Fact]
    public void NonEmpty_RowGroupCols_Builds_GroupSpec_Using_Field_Not_Id()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [
                { "id": "regionColId", "displayName": "Region", "field": "region" },
                { "id": "segmentColId", "displayName": "Segment", "field": "segment" }
              ],
              "valueCols": [], "pivotCols": [], "pivotMode": false,
              "groupKeys": [ { "type": "string", "value": "EMEA" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var group = Parse(request).Group;
        Assert.NotNull(group);
        Assert.Equal(["region", "segment"], group!.GroupCols);
        Assert.Single(group.GroupKeys);
        Assert.Equal("EMEA", Assert.IsType<StringValue>(group.GroupKeys[0]).Value);
    }

    [Fact]
    public void GroupKeys_Deserializes_Mixed_Typed_Values()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [
                { "id": "region", "displayName": "Region", "field": "region" },
                { "id": "fiscalYear", "displayName": "Fiscal Year", "field": "fiscalYear" }
              ],
              "valueCols": [], "pivotCols": [], "pivotMode": false,
              "groupKeys": [ { "type": "string", "value": "EMEA" }, { "type": "int64", "value": 2024 } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var group = Parse(request).Group!;
        Assert.Equal(2, group.GroupKeys.Count);
        Assert.IsType<StringValue>(group.GroupKeys[0]);
        Assert.Equal(2024, Assert.IsType<Int64Value>(group.GroupKeys[1]).Value);
    }

    [Fact]
    public void Empty_ValueCols_Produces_Null_Aggregations()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        Assert.Null(Parse(request).Aggregations);
    }

    [Theory]
    [InlineData("sum", AggregationFunc.Sum)]
    [InlineData("min", AggregationFunc.Min)]
    [InlineData("max", AggregationFunc.Max)]
    [InlineData("avg", AggregationFunc.Avg)]
    [InlineData("count", AggregationFunc.Count)]
    public void ValueCols_AggFunc_Maps_To_Expected_AggregationFunc(string wireFunc, AggregationFunc expected)
    {
        var request = $$"""
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "valueCols": [ { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "{{wireFunc}}" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var aggregations = Parse(request).Aggregations!;
        Assert.Single(aggregations);
        Assert.Equal("amount", aggregations[0].Field);
        Assert.Equal(expected, aggregations[0].Func);
        Assert.Null(aggregations[0].Alias);
    }

    [Fact]
    public void ValueCols_Id_Differing_From_Field_Becomes_Alias()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "valueCols": [ { "id": "totalRevenue", "displayName": "Total Revenue", "field": "amount", "aggFunc": "sum" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var aggregations = Parse(request).Aggregations!;
        Assert.Equal("totalRevenue", aggregations[0].Alias);
    }

    [Fact]
    public void Multiple_ValueCols_On_Same_Field_Are_Distinct_Aggregations()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "valueCols": [
                { "id": "amountSum", "displayName": "Total", "field": "amount", "aggFunc": "sum" },
                { "id": "amountAvg", "displayName": "Average", "field": "amount", "aggFunc": "avg" }
              ],
              "filterModel": null, "sortModel": []
            }
            """;

        var aggregations = Parse(request).Aggregations!;
        Assert.Equal(2, aggregations.Count);
        Assert.Equal(AggregationFunc.Sum, aggregations[0].Func);
        Assert.Equal("amountSum", aggregations[0].Alias);
        Assert.Equal(AggregationFunc.Avg, aggregations[1].Func);
        Assert.Equal("amountAvg", aggregations[1].Alias);
    }

    [Fact]
    public void Unrecognized_AggFunc_Throws_UnknownOperator()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "valueCols": [ { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "first" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(request));
        Assert.Equal("unknown-operator", ex.Reason);
    }

    [Fact]
    public void Missing_AggFunc_Throws_MalformedRequest()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "valueCols": [ { "id": "amount", "displayName": "Amount", "field": "amount" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Null_Element_In_RowGroupCols_Throws_MalformedRequest()
    {
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [ null ], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void NonEmpty_GroupKeys_With_Empty_RowGroupCols_Throws_MalformedRequest_Rather_Than_Silently_Dropping_The_Key()
    {
        // A drill-down key with no group column to drill into would otherwise be silently dropped,
        // which WIDENS the query (BM-2: a group key restricts the result set) — the opposite of
        // AG-1/AG-5's "explicitly rejected, never silently mis-parsed".
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false,
              "groupKeys": [ { "type": "string", "value": "EMEA" } ],
              "filterModel": null, "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void RowGroupCols_Entry_Missing_Field_Falls_Back_To_Id()
    {
        // AG Grid's real ColumnVO declares 'field' optional (a valueGetter-backed column has no
        // plain field path) — 'id' is always present and is what GridSchema's allowlist keys on.
        var request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [ { "id": "region", "displayName": "Region" } ],
              "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        var group = Parse(request).Group;
        Assert.Equal(["region"], group!.GroupCols);
    }

    // ---- AG-6: the auto group column's colId ------------------------------------------------------
    //
    // `ag-Grid-AutoColumn[-<id>]` lives in AG GRID's column space, not the host's, so it matches no
    // GridSchema field and a compiler can only reject it. Resolving it is the adapter's job, and
    // until #1640's review nothing did it — which made the single most common grouped-grid
    // interaction, clicking the group column's header, a hard failure. The shapes below are read
    // from columnUtils.ts / autoColService.ts / sortService.ts (see
    // spec/fixtures/group-aggregation/021's description for the exact citations).

    private const string ThreeLevelGroup =
        """
        "rowGroupCols": [
          { "id": "regionCol", "displayName": "Region", "field": "region" },
          { "id": "segment", "displayName": "Segment", "field": "segment" },
          { "id": "product", "displayName": "Product", "field": "product" }
        ]
        """;

    private static string GroupedRequest(string groupKeys, string sortModel) =>
        $$"""
          {
            "startRow": 0, "endRow": 100,
            {{ThreeLevelGroup}},
            "valueCols": [], "pivotCols": [], "pivotMode": false,
            "groupKeys": [{{groupKeys}}],
            "filterModel": null,
            "sortModel": [{{sortModel}}]
          }
          """;

    [Fact]
    public void The_Bare_Auto_Column_Resolves_To_The_Group_Column_This_Level_Buckets_By()
    {
        // 0 keys consumed -> the level buckets by groupCols[0]; 1 consumed -> groupCols[1]. Same
        // dispatch as GRP-1's, which is the point: the display column shows the whole path, so the
        // only thing this request can order by is the level it is itself producing.
        var top = Parse(GroupedRequest("", """{ "colId": "ag-Grid-AutoColumn", "sort": "asc" }"""));
        Assert.Equal("region", top.Sort![0].Field);

        var second = Parse(GroupedRequest(
            """{ "type": "string", "value": "EMEA" }""",
            """{ "colId": "ag-Grid-AutoColumn", "sort": "desc" }"""));
        Assert.Equal("segment", second.Sort![0].Field);
        Assert.Equal(SortDirection.Desc, second.Sort[0].Direction);
    }

    /// <summary>
    /// The <c>multipleColumns</c> form names its source column, and the suffix is that column's
    /// <c>ColumnVO.id</c> — <c>rowGroupCol.getId()</c> in <c>createOneAutoCol</c>. <c>regionCol</c>
    /// here has id <c>regionCol</c> and field <c>region</c>, so looking the suffix up against the
    /// FIELDS instead would silently fail to resolve.
    /// </summary>
    [Fact]
    public void The_Suffixed_Auto_Column_Resolves_Its_Suffix_As_A_ColumnVO_Id_Not_As_A_Field()
    {
        var parsed = Parse(GroupedRequest("", """{ "colId": "ag-Grid-AutoColumn-regionCol", "sort": "asc" }"""));

        Assert.Equal("region", parsed.Sort![0].Field);
    }

    /// <summary>
    /// AG Grid's sort coupling (on by default) sets the sort on the display column AND on every
    /// source column <c>getSourceColumnsForGroupColumn</c> returns for it — which, for the DEFAULT
    /// single auto column, is every row-group column: its colDef carries
    /// <c>showRowGroup: rowGroupCol?.getColId() ?? true</c> (the literal <c>true</c>) and that
    /// service answers <c>true</c> with <c>rowGroupColsSvc.columns.slice(0)</c>. So one click on the
    /// group header emits 1 + N entries, not a pair, and all N collapse into the one key the display
    /// column resolved to. Keeping them would leave the request sorting on a column pinned to a
    /// single value by the drill-down (<c>region</c>) and on a level this response does not produce
    /// (<c>product</c>), both of which GRP-4 rejects — i.e. the commonest grouped interaction would
    /// still fail. Pinned by <c>spec/fixtures/group-aggregation/021</c>.
    /// </summary>
    [Fact]
    public void The_Whole_Coupled_Row_Group_Set_Collapses_Into_The_Resolved_Key()
    {
        var parsed = Parse(GroupedRequest(
            """{ "type": "string", "value": "EMEA" }""",
            """
            { "colId": "ag-Grid-AutoColumn", "sort": "desc" },
            { "colId": "region", "sort": "desc" },
            { "colId": "segment", "sort": "desc" },
            { "colId": "product", "sort": "desc" }
            """));

        Assert.Single(parsed.Sort!);
        Assert.Equal("segment", parsed.Sort![0].Field);
        Assert.Equal(SortDirection.Desc, parsed.Sort[0].Direction);
    }

    /// <summary>
    /// The <c>multipleColumns</c> form is the other half of the same rule and must stay distinct: its
    /// <c>showRowGroup</c> is one column's id (the string arm of the same service method), so its
    /// coupled set is that ONE column. Collapsing the whole row-group list for it would silently
    /// discard sort keys the client really did ask for.
    /// </summary>
    [Fact]
    public void A_Suffixed_Auto_Column_Collapses_Only_Its_Own_Source_Column()
    {
        var parsed = Parse(GroupedRequest(
            "",
            """
            { "colId": "ag-Grid-AutoColumn-regionCol", "sort": "asc" },
            { "colId": "region", "sort": "asc" },
            { "colId": "segment", "sort": "desc" }
            """));

        Assert.Equal(["region", "segment"], parsed.Sort!.Select(item => item.Field));
    }

    /// <summary>
    /// ...and the collapse is narrow: a duplicate neither side of which came from an auto column is
    /// left exactly as the client sent it, since nothing in this adapter created it.
    /// </summary>
    [Fact]
    public void A_Duplicate_The_Adapter_Did_Not_Create_Is_Left_Alone()
    {
        var parsed = Parse(GroupedRequest(
            "",
            """
            { "colId": "segment", "sort": "asc" },
            { "colId": "segment", "sort": "desc" }
            """));

        Assert.Equal(2, parsed.Sort!.Count);
    }

    /// <summary>
    /// On the drill-down leaf every group column is consumed, so there is no level for the display
    /// column to name — and every row shares the whole group path, making the key a constant that
    /// orders nothing. Dropped rather than mapped to some level's column, which would impose an
    /// ordering the user never asked for. SEM-6's tiebreaker still guarantees a total order.
    /// </summary>
    [Fact]
    public void On_The_Drill_Down_Leaf_The_Auto_Column_Key_Is_Dropped()
    {
        var parsed = Parse(GroupedRequest(
            """
            { "type": "string", "value": "EMEA" },
            { "type": "string", "value": "Enterprise" },
            { "type": "string", "value": "Widget" }
            """,
            """{ "colId": "ag-Grid-AutoColumn", "sort": "asc" }"""));

        Assert.Null(parsed.Sort);
    }

    /// <summary>
    /// A suffix naming no <c>rowGroupCols</c> entry is left UNRESOLVED on purpose and carried through
    /// VERBATIM, so it fails downstream as the unknown colId it is. Mapping it to some other column
    /// would be a silent substitution; DROPPING it — which this adapter did until #1640's second
    /// review — is worse, because the response then comes back in a different order from the one the
    /// grid's own header shows, with no rejection and no log. Any other bogus colId reaches a compiler
    /// and is refused there; the auto-column prefix must not be the one that opens a silent-drop path.
    /// </summary>
    [Fact]
    public void An_Unrecognized_Auto_Column_Suffix_Is_Carried_Through_Verbatim_To_Be_Rejected()
    {
        var parsed = Parse(GroupedRequest("", """{ "colId": "ag-Grid-AutoColumn-nosuch", "sort": "asc" }"""));

        Assert.Equal("ag-Grid-AutoColumn-nosuch", parsed.Sort![0].Field);
    }

    /// <summary>
    /// ...and so is an auto-column colId on a request carrying no group state at all, for the same
    /// reason: it names no level because there are no levels, which is a malformed request rather
    /// than something to quietly discard.
    /// </summary>
    [Fact]
    public void An_Auto_Column_ColId_On_An_Ungrouped_Request_Is_Carried_Through_Verbatim()
    {
        var request =
            """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false,
              "groupKeys": [],
              "filterModel": null,
              "sortModel": [{ "colId": "ag-Grid-AutoColumn", "sort": "asc" }]
            }
            """;

        var parsed = Parse(request);

        Assert.Equal("ag-Grid-AutoColumn", parsed.Sort![0].Field);
    }

    /// <summary>An ordinary colId that merely starts with something else is untouched — the prefix
    /// match is AG Grid's own (<c>isColumnGroupAutoCol</c>), not a fuzzy one.</summary>
    [Fact]
    public void A_Normal_ColId_Is_Not_Rewritten()
    {
        var parsed = Parse(GroupedRequest("", """{ "colId": "product", "sort": "asc" }"""));

        Assert.Equal("product", parsed.Sort![0].Field);
    }
}
