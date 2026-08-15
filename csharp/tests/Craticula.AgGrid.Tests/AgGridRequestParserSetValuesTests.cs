using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// BM-3's <c>setValuesRequest</c> envelope through <see cref="AgGridRequestParser.ParseSetValues"/>,
/// against the real <c>spec/fixtures/group-aggregation/016</c>-<c>019</c> requests plus the malformed
/// shapes no fixture carries.
///
/// <para>The fixture assertions here are deliberately about what the parser <em>keeps</em>: most
/// importantly that <c>017</c>'s <c>filterModel</c> reaches the IR with BOTH its columns, since the
/// exclusion that removes one of them is the pipeline's and a fixture whose data never reaches the
/// rule it is named for proves nothing (which is exactly what <c>017</c> was before #1641).</para>
/// </summary>
public class AgGridRequestParserSetValuesTests
{
    private static AgGridParsedSetValuesRequest Parse(string fixtureDir) =>
        AgGridRequestParser.ParseSetValues(
            File.ReadAllText(RepoPaths.Fixture("group-aggregation", fixtureDir, "agGridRequest.json")));

    [Fact]
    public void Fixture016_ColId_Only_With_An_Empty_Filter_Model()
    {
        var parsed = Parse("016-set-filter-values-no-active-filters");

        Assert.Equal("region", parsed.SetValues.ColId);
        Assert.Null(parsed.SetValues.SearchPrefix);
        Assert.Null(parsed.SetValues.Limit);
        Assert.Null(parsed.NativeFilter);
        Assert.Null(parsed.CustomFilters);
    }

    /// <summary>
    /// The assertion that makes <c>017</c> a real test of the own-field exclusion: its request really
    /// does carry a <c>region</c> filter beside the <c>segment</c> one, so a compiler that forgot to
    /// drop it would emit an extra predicate and fail that fixture's own (unchanged) artifacts.
    /// </summary>
    [Fact]
    public void Fixture017_Carries_The_Targets_Own_Filter_Which_The_Parser_Does_Not_Drop()
    {
        var parsed = Parse("017-set-filter-values-excludes-own-field-filter");

        Assert.Equal("region", parsed.SetValues.ColId);

        var model = Assert.IsType<AndNode>(parsed.NativeFilter);
        Assert.Equal(
            ["region", "segment"],
            model.Nodes.Select(node => Assert.IsType<ComparisonNode>(node).Field));
    }

    [Fact]
    public void Fixture018_Carries_The_Search_Prefix_Verbatim()
    {
        var parsed = Parse("018-set-filter-values-search-prefix");

        Assert.Equal("eu", parsed.SetValues.SearchPrefix);
        Assert.Null(parsed.NativeFilter);
    }

    [Fact]
    public void Fixture019_Carries_The_Clients_Requested_Limit_Unclamped()
    {
        // The clamp is SEC-3's, in ResourceLimitsValidator — the adapter reports what was asked for.
        Assert.Equal(50_000, Parse("019-set-filter-values-server-side-cap").SetValues.Limit);
    }

    /// <summary>
    /// <c>022</c> is the only fixture carrying both halves of GRP-6's <c>WHERE</c> clause — a filter
    /// model (on the target column AND another one) and a <c>searchPrefix</c>. The adapter's job is to
    /// hand all three to the pipeline untouched: it neither drops the target's own entry (the
    /// exclusion is <see cref="Craticula.Core.Schema.GridFilterPipeline.ProcessSetValues"/>'s) nor
    /// folds the prefix into the model (it is injected after the exclusion, precisely so it survives
    /// it).
    /// </summary>
    [Fact]
    public void Fixture022_Carries_The_Whole_Filter_Model_And_The_Prefix_Side_By_Side()
    {
        var parsed = Parse("022-set-filter-values-prefix-with-active-filter");

        Assert.Equal("region", parsed.SetValues.ColId);
        Assert.Equal("eu", parsed.SetValues.SearchPrefix);

        var model = Assert.IsType<AndNode>(parsed.NativeFilter);
        Assert.Equal(
            ["region", "segment"],
            model.Nodes.Select(node => Assert.IsType<ComparisonNode>(node).Field));
    }

    // ---- rejections -------------------------------------------------------------------------------

    [Fact]
    public void A_Row_Fetch_Request_Is_Not_A_Set_Values_Request()
    {
        var requestJson = File.ReadAllText(
            RepoPaths.Fixture("group-aggregation", "001-group-top-level-no-keys-consumed", "agGridRequest.json"));

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.ParseSetValues(requestJson));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void A_Missing_ColId_Is_Rejected()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => AgGridRequestParser.ParseSetValues("""{"setValuesRequest": {"filterModel": {}}}"""));

        Assert.Equal("malformed-request", ex.Reason);
    }

    /// <summary>
    /// <c>filterModel</c> is documented as required-may-be-empty. Accepting its absence as <c>{}</c>
    /// would make that contract untrue and leave a client unable to tell a dropped property from an
    /// empty one.
    /// </summary>
    [Fact]
    public void A_Missing_FilterModel_Is_Rejected_Rather_Than_Read_As_Empty()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(
            () => AgGridRequestParser.ParseSetValues("""{"setValuesRequest": {"colId": "region"}}"""));

        Assert.Contains("filterModel", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Non_String_SearchPrefix_Is_Rejected()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.ParseSetValues(
            """{"setValuesRequest": {"colId": "region", "filterModel": {}, "searchPrefix": 7}}"""));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void A_Non_Integer_Limit_Is_Rejected()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.ParseSetValues(
            """{"setValuesRequest": {"colId": "region", "filterModel": {}, "limit": "lots"}}"""));

        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void A_NonPositive_Limit_Is_Rejected_By_The_Structural_Validator()
    {
        Assert.Throws<SetValuesSpecValidationException>(() => AgGridRequestParser.ParseSetValues(
            """{"setValuesRequest": {"colId": "region", "filterModel": {}, "limit": 0}}"""));
    }

    [Fact]
    public void An_Explicitly_Null_SearchPrefix_Or_Limit_Reads_As_Absent()
    {
        var parsed = AgGridRequestParser.ParseSetValues(
            """{"setValuesRequest": {"colId": "region", "filterModel": null, "searchPrefix": null, "limit": null}}""");

        Assert.Null(parsed.SetValues.SearchPrefix);
        Assert.Null(parsed.SetValues.Limit);
        Assert.Null(parsed.NativeFilter);
    }

    // ---- shared filterModel grammar ---------------------------------------------------------------

    /// <summary>
    /// The envelope's <c>filterModel</c> is parsed by the identical machinery the row-fetch path uses,
    /// so every AG-1 condition shape works here without a second implementation. A combined form is
    /// the cheapest proof that this is genuinely the same dispatch and not a reduced copy.
    /// </summary>
    [Fact]
    public void The_Envelopes_FilterModel_Uses_The_Same_Condition_Grammar_As_A_Row_Fetch()
    {
        var parsed = AgGridRequestParser.ParseSetValues(
            """
            {
              "setValuesRequest": {
                "colId": "region",
                "filterModel": {
                  "segment": {
                    "filterType": "text",
                    "operator": "OR",
                    "conditions": [
                      { "filterType": "text", "type": "equals", "filter": "Enterprise" },
                      { "filterType": "text", "type": "startsWith", "filter": "SMB" }
                    ]
                  }
                }
              }
            }
            """);

        var model = Assert.IsType<OrNode>(parsed.NativeFilter);
        Assert.Equal(2, model.Nodes.Count);
    }

    /// <summary>
    /// OQ-11's <c>customFilters</c> extension applies to this envelope too: a set filter's value list
    /// must honour the same conditions the grid's rows do, or it offers values no visible row has.
    /// </summary>
    [Fact]
    public void The_Envelope_Accepts_A_CustomFilters_Fragment()
    {
        var parsed = AgGridRequestParser.ParseSetValues(
            """
            {
              "setValuesRequest": {
                "colId": "region",
                "filterModel": {},
                "customFilters": {
                  "type": "comparison",
                  "field": "fiscalYear",
                  "op": "eq",
                  "value": { "type": "int64", "value": 2024 }
                }
              }
            }
            """);

        Assert.Equal("fiscalYear", Assert.IsType<ComparisonNode>(parsed.CustomFilters).Field);
    }
}
