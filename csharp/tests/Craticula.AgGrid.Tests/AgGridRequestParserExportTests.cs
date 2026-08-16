using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// EXP-1's wire-contract extension (#1650): <c>exportFormat</c>/<c>exportScope</c>/
/// <c>exportColumns</c>, the <c>exportScope: "all"</c> → EXP-2 <see cref="NonePage"/> mapping, the
/// group-export carve-out, and EXP-1(b)'s resolved projection.
///
/// <para><b>What is here and what is a fixture.</b> The compiled SHAPES this contract produces are
/// pinned by <c>spec/fixtures/page-export/</c> — 23 Postgres arms and 13 Mongo/Search ones, all
/// running end to end through the three conformance runners, including <c>023</c> which was authored
/// by this story specifically so <c>exportable: false</c> is not a compiler path whose only oracle is
/// a unit test. What is here is what a compiled artifact CANNOT show: the rejections, and the
/// <see cref="ExportColumn.Header"/> resolution, which no artifact in the suite contains by
/// construction (a header is the CSV writer's, EXP-4/#1651).</para>
/// </summary>
public class AgGridRequestParserExportTests
{
    /// <summary>The <c>page-export</c> category's own schema: seven fields, three of them mapping to a
    /// different physical column, one non-exportable, one carrying a label.</summary>
    private static GridSchema Schema() => GridSchema.Parse(
        File.ReadAllText(RepoPaths.Fixture(
            "page-export", "023-export-columns-default-skips-non-exportable-field", "schema.json")));

    private static AgGridParsedRequest Parse(string request) =>
        AgGridRequestParser.Parse(request, new AgGridParseOptions { Schema = Schema() });

    private static string Request(string exportMembers, string startRow = "null", string endRow = "null",
        string rowGroupCols = "[]", string valueCols = "[]", string groupKeys = "[]") =>
        $$"""
        {
          "startRow": {{startRow}}, "endRow": {{endRow}},
          "rowGroupCols": {{rowGroupCols}}, "valueCols": {{valueCols}}, "pivotCols": [],
          "pivotMode": false, "groupKeys": {{groupKeys}},
          "filterModel": null, "sortModel": []
          {{exportMembers}}
        }
        """;

    // ---- the request is, or is not, an export ----------------------------------------------------

    [Fact]
    public void A_Request_With_No_Export_Member_Has_No_ExportSpec_And_Keeps_Its_Range_Page()
    {
        var parsed = Parse(Request("", startRow: "0", endRow: "50"));

        Assert.Null(parsed.Export);

        var page = Assert.IsType<RangePage>(parsed.Page);
        Assert.Equal(0, page.StartRow);
        Assert.Equal(50, page.EndRow);
    }

    [Theory]
    [InlineData("csv", ExportFormat.Csv)]
    [InlineData("tsv", ExportFormat.Tsv)]
    public void ExportFormat_Is_Read_And_Never_Changes_Anything_Else(string wire, ExportFormat expected)
    {
        var parsed = Parse(Request($$""", "exportFormat": "{{wire}}" """));

        Assert.Equal(expected, parsed.Export!.Format);

        // page-export/004 vs /005: the two formats compile to byte-identical artifacts on every
        // backend, which is only true because nothing downstream of this property reads it.
        Assert.Equal(ExportScope.All, parsed.Export.Scope);
    }

    /// <summary>EXP-1 declares <c>exportFormat</c> non-optional. A stray <c>exportScope</c> is
    /// therefore a malformed request rather than an ignored property — ignoring it would return one
    /// page where the client asked for the whole result set.</summary>
    [Theory]
    [InlineData(""", "exportScope": "all" """)]
    [InlineData(""", "exportColumns": [ { "colId": "status" } ] """)]
    public void An_Export_Member_Without_ExportFormat_Is_Rejected(string exportMembers)
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(Request(exportMembers)));

        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains("exportFormat", ex.Message);
    }

    [Theory]
    [InlineData(""", "exportFormat": "xlsx" """)]
    [InlineData(""", "exportFormat": "csv", "exportScope": "everything" """)]
    [InlineData(""", "exportFormat": 1 """)]
    public void An_Unrecognized_Export_Format_Or_Scope_Is_Rejected(string exportMembers)
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(Request(exportMembers)));

        Assert.Equal("malformed-request", ex.Reason);
    }

    /// <summary>The same treatment an AG-2 date preset gets: the adapter must RESOLVE something
    /// (there, a type; here, a projection) and cannot honestly guess it without the schema.</summary>
    [Fact]
    public void An_Export_Parsed_Without_A_Schema_Is_Rejected_Rather_Than_Silently_Unprojected()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => AgGridRequestParser.Parse(Request(""", "exportFormat": "csv" """)));

        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains("GridSchema", ex.Message);
    }

    // ---- EXP-2: exportScope -> page --------------------------------------------------------------

    /// <summary>page-export/006: <c>startRow: 200, endRow: 250</c> beside <c>exportScope: "all"</c>
    /// compiles with no row-range clause at all.</summary>
    [Fact]
    public void ExportScope_All_Discards_The_Requests_Own_StartRow_And_EndRow()
    {
        var parsed = Parse(Request(""", "exportFormat": "csv", "exportScope": "all" """, "200", "250"));

        Assert.IsType<NonePage>(parsed.Page);
    }

    /// <summary>...and page-export/007 sends the identical pair under <c>"page"</c> and keeps it.</summary>
    [Fact]
    public void ExportScope_Page_Honors_The_Requests_Own_Range()
    {
        var parsed = Parse(Request(""", "exportFormat": "csv", "exportScope": "page" """, "200", "250"));

        var page = Assert.IsType<RangePage>(parsed.Page);
        Assert.Equal(200, page.StartRow);
        Assert.Equal(250, page.EndRow);
    }

    [Fact]
    public void ExportScope_Defaults_To_All_When_The_Wire_Omits_It()
    {
        var parsed = Parse(Request(""", "exportFormat": "csv" """, "200", "250"));

        Assert.Equal(ExportScope.All, parsed.Export!.Scope);
        Assert.IsType<NonePage>(parsed.Page);
    }

    /// <summary>The <c>startRow</c>/<c>endRow</c> pair is shape-checked before the scope overrides it,
    /// so a half-supplied range is malformed whether or not this scope would have used it.</summary>
    [Fact]
    public void A_Half_Supplied_Range_Is_Still_Rejected_Under_ExportScope_All()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => Parse(Request(""", "exportFormat": "csv", "exportScope": "all" """, "200", "null")));

        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains("startRow", ex.Message);
    }

    // ---- EXP-1(b): the resolved projection ----------------------------------------------------------

    [Fact]
    public void An_Absent_ExportColumns_Projects_Every_Exportable_Field_In_Declaration_Order()
    {
        var parsed = Parse(Request(""", "exportFormat": "csv" """));

        // 'notes' is declared exportable: false in this schema and is the only one missing.
        Assert.Equal(
            ["id", "orderDate", "customerName", "totalAmount", "status", "region"],
            parsed.Export!.ProjectionColumns);
    }

    [Fact]
    public void An_Explicit_ExportColumns_Is_Taken_In_Request_Order_Not_Schema_Order()
    {
        var parsed = Parse(Request("""
            , "exportFormat": "csv", "exportColumns": [
                { "colId": "customerName", "header": "Customer" },
                { "colId": "totalAmount", "header": "Total" },
                { "colId": "orderDate", "header": "Order Date" }
              ]
            """));

        Assert.Equal(["customerName", "totalAmount", "orderDate"], parsed.Export!.ProjectionColumns);
        Assert.Equal(["Customer", "Total", "Order Date"], parsed.Export.Columns.Select(column => column.Header));
    }

    /// <summary>EXP-1's "headers from schema labels", and the fallback chain the CSV writer will read:
    /// the wire's own <c>header</c>, then <see cref="FieldConfig.Label"/>, then the colId. Untestable
    /// against a fixture — no compiled artifact contains a header string.</summary>
    [Fact]
    public void A_Header_Falls_Back_To_The_Schema_Label_Then_To_The_ColId()
    {
        var parsed = Parse(Request("""
            , "exportFormat": "csv", "exportColumns": [
                { "colId": "customerName" },
                { "colId": "status" },
                { "colId": "region", "header": "Sales region" }
              ]
            """));

        Assert.Equal(
            [("customerName", "Customer name"), ("status", "status"), ("region", "Sales region")],
            parsed.Export!.Columns.Select(column => (column.ColId, column.Header)));
    }

    [Fact]
    public void The_Default_Projection_Takes_Its_Headers_From_The_Schema_Labels_Too()
    {
        var parsed = Parse(Request(""", "exportFormat": "csv" """));

        var customerName = parsed.Export!.Columns.Single(column => column.ColId == "customerName");

        Assert.Equal("Customer name", customerName.Header);
        Assert.Equal("status", parsed.Export.Columns.Single(column => column.ColId == "status").Header);
    }

    [Theory]
    // A column the schema does not declare: there is no physical column to project it from.
    [InlineData(""", "exportFormat": "csv", "exportColumns": [ { "colId": "nope" } ] """, "does not declare")]
    // A column the schema declares exportable: false. Hard-fails in EVERY schema mode — a dropped
    // export column hands back a file missing something the client explicitly asked for.
    [InlineData(""", "exportFormat": "csv", "exportColumns": [ { "colId": "notes" } ] """, "exportable")]
    // A repeat: on Mongo the second $project entry would simply overwrite the first (MDB-10).
    [InlineData(""", "exportFormat": "csv", "exportColumns": [ { "colId": "status" }, { "colId": "status" } ] """, "more than once")]
    // An empty list is not "the default projection" — absence is.
    [InlineData(""", "exportFormat": "csv", "exportColumns": [] """, "empty")]
    public void An_Unresolvable_ExportColumns_Is_Rejected(string exportMembers, string expectedInMessage)
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => Parse(Request(exportMembers)));

        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains(expectedInMessage, ex.Message);
    }

    /// <summary>Lenient mode changes nothing here: its contract is that an unknown filter CONDITION
    /// vanishes and the result merely widens, which is not what a missing export column does. Same
    /// reasoning that keeps #1640's group columns and aggregations hard failures in Lenient mode.</summary>
    [Fact]
    public void An_Unknown_Export_Column_Is_Rejected_In_Lenient_Mode_Too()
    {
        var lenient = Schema() with { Mode = GridSchemaMode.Lenient };

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(
            Request(""", "exportFormat": "csv", "exportColumns": [ { "colId": "nope" } ] """),
            new AgGridParseOptions { Schema = lenient }));

        Assert.Equal("malformed-request", ex.Reason);
    }

    // ---- EXP-1's group-export carve-out ----------------------------------------------------------

    /// <summary>page-export/010 and /011: a fully populated group state plus an export compiles flat.
    /// The IR carries no group and no aggregations, so the GRP-1 dispatch every compiler runs is never
    /// entered — the carve-out is deliberate, not incidental.</summary>
    [Fact]
    public void An_Export_Discards_RowGroupCols_And_ValueCols_Entirely()
    {
        var parsed = Parse(Request(
            """, "exportFormat": "csv", "exportScope": "all" """,
            rowGroupCols: """[ { "id": "region", "field": "region" }, { "id": "status", "field": "status" } ]""",
            valueCols: """[ { "id": "totalAmount", "field": "totalAmount", "aggFunc": "sum" } ]"""));

        Assert.Null(parsed.Group);
        Assert.Null(parsed.Aggregations);
        Assert.NotNull(parsed.Export);
    }

    [Fact]
    public void The_Same_Request_Without_The_Export_Members_Keeps_Its_Group_State()
    {
        var parsed = Parse(Request(
            "",
            rowGroupCols: """[ { "id": "region", "field": "region" } ]""",
            valueCols: """[ { "id": "totalAmount", "field": "totalAmount", "aggFunc": "sum" } ]"""));

        Assert.NotNull(parsed.Group);
        Assert.NotNull(parsed.Aggregations);
    }

    /// <summary>The carve-out applies to an <c>exportScope: "page"</c> export too: the output is a flat
    /// delimited row stream either way, and the scope decides only how many rows go into it.</summary>
    [Fact]
    public void The_Carve_Out_Applies_Under_ExportScope_Page_As_Well()
    {
        var parsed = Parse(Request(
            """, "exportFormat": "csv", "exportScope": "page" """, "0", "100",
            rowGroupCols: """[ { "id": "region", "field": "region" } ]"""));

        Assert.Null(parsed.Group);
        Assert.IsType<RangePage>(parsed.Page);
    }

    /// <summary><c>groupKeys</c> is discarded like the other two group members, not refused: a host
    /// that builds its export request by copying the grid's last <c>getRows</c> payload sends whatever
    /// drill-down path the user had expanded, and refusing it would hard-fail the obvious integration.
    /// The compiled consequence — leaf rows, no drill-down predicate, no grouped level — is pinned end
    /// to end by <c>page-export/024</c>; what is asserted here is only that the parser reaches it.</summary>
    [Fact]
    public void An_Export_Carrying_A_Drill_Down_Key_Discards_It_Like_Any_Other_Group_State()
    {
        var parsed = Parse(Request(
            """, "exportFormat": "csv" """,
            rowGroupCols: """[ { "id": "region", "field": "region" }, { "id": "status", "field": "status" } ]""",
            valueCols: """[ { "id": "totalAmount", "field": "totalAmount", "aggFunc": "sum" } ]""",
            groupKeys: """[ { "type": "string", "value": "EMEA" } ]"""));

        Assert.Null(parsed.Group);
        Assert.Null(parsed.Aggregations);
        Assert.NotNull(parsed.Export);
    }

    /// <summary>AG-6: a grouped grid whose user hits "export" still addresses its sort through the
    /// row-group DISPLAY column, so the sort model is resolved against the group state the wire sent
    /// — before the carve-out discards it. Resolving it afterwards would leave a sort key naming no
    /// schema field at all.</summary>
    [Fact]
    public void An_Auto_Group_Column_Sort_Key_Still_Resolves_On_An_Export()
    {
        const string request = """
            {
              "startRow": null, "endRow": null,
              "rowGroupCols": [ { "id": "region", "field": "region" } ],
              "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null,
              "sortModel": [ { "colId": "ag-Grid-AutoColumn", "sort": "asc" } ],
              "exportFormat": "csv"
            }
            """;

        var parsed = AgGridRequestParser.Parse(request, new AgGridParseOptions { Schema = Schema() });

        Assert.Null(parsed.Group);
        Assert.Equal("region", Assert.Single(parsed.Sort!).Field);
    }
}
