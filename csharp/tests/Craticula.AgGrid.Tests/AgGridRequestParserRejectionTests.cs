using System.Text.Json;
using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// AG-1/AG-5's Definition of Done: unsupported/out-of-scope shapes are explicitly rejected, never
/// silently ignored. Covers the rejections that don't fit naturally into the per-filterType test
/// files (<c>pivotMode</c>/<c>pivotCols</c>, the <c>setValuesRequest</c> envelope, and the deserializer-
/// fuzz shapes real <c>adversarial/</c> fixtures pin) plus a few generic malformed-root cases.
/// </summary>
public class AgGridRequestParserRejectionTests
{
    // No real fixture anywhere sets pivotMode:true (every one that carries the property at all uses
    // pivotMode:false/pivotCols:[] — see docs/feat/1635/context.md's research notes) — this synthetic
    // request is the only way to exercise the rejection at all. Stated without a count on purpose: the
    // one it used to carry (158) was already stale by #1650, when the suite held 202 fixtures.
    [Fact]
    public void PivotMode_True_Throws_MalformedRequest()
    {
        const string request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": true, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void NonEmpty_PivotCols_Throws_MalformedRequest()
    {
        const string request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [],
              "pivotCols": [ { "id": "region", "displayName": "Region", "field": "region" } ],
              "pivotMode": false, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    /// <summary>
    /// Still a rejection after #1641 built BM-3's compiler, and deliberately so: the envelope is not
    /// an <c>IServerSideGetRowsRequest</c> and this method's output record has a required
    /// <c>Page</c> the envelope never carries. It routes to
    /// <see cref="AgGridRequestParser.ParseSetValues"/> instead — see
    /// <see cref="AgGridRequestParserSetValuesTests"/>.
    /// </summary>
    [Fact]
    public void SetValuesRequest_Envelope_Fixture_016_Throws_MalformedRequest()
    {
        var path = RepoPaths.Fixture("group-aggregation", "016-set-filter-values-no-active-filters", "agGridRequest.json");
        var requestJson = File.ReadAllText(path);

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(requestJson));
        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains("ParseSetValues", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SetValuesRequest_Envelope_Fixture_017_Throws_MalformedRequest()
    {
        var path = RepoPaths.Fixture("group-aggregation", "017-set-filter-values-excludes-own-field-filter", "agGridRequest.json");
        var requestJson = File.ReadAllText(path);

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(requestJson));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Adversarial_021_Duplicate_Json_Keys_Throws_MalformedRequest()
    {
        var path = RepoPaths.Fixture("adversarial", "021-duplicate-json-keys-deserializer-fuzz", "agGridRequest.json");
        var requestJson = File.ReadAllText(path);

        // AllowDuplicateProperties = false on the JsonDocumentOptions this parser uses rejects the
        // duplicated 'filter' key at the JSON-document level; the parser wraps that raw JsonException
        // into the typed AgGridRequestParseException, matching this fixture's own
        // expected/rejection.json ("requestParsing"/"malformed-request") exactly, including the
        // Reason a caller would actually map that oracle against.
        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(requestJson));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void Adversarial_022_Null_RowGroupCols_Element_Throws_MalformedRequest()
    {
        var path = RepoPaths.Fixture("adversarial", "022-null-where-object-required-deserializer-fuzz", "agGridRequest.json");
        var requestJson = File.ReadAllText(path);

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(requestJson));
        Assert.Equal("malformed-request", ex.Reason);
    }

    /// <summary>
    /// #1886's fixture, and the one adversarial case whose payload every backend would have
    /// <em>accepted</em>: two <c>valueCols</c> share one <c>ColumnVO.id</c> over two different
    /// fields, so AGG-2 aliases both <c>total</c> and GRP-4 resolves both to one output name. The
    /// refusal comes from <see cref="AggregationValidator"/> — shared, so all four backends inherit
    /// it — which is why the exception here is Core's rather than this parser's
    /// <see cref="AgGridRequestParseException"/>, and why the fixture's oracle names the stage
    /// (<c>requestParsing</c>) rather than a compiler.
    /// </summary>
    [Fact]
    public void Adversarial_023_Two_ValueCols_Resolving_To_One_Output_Name_Are_Rejected()
    {
        const string fixtureDir = "023-duplicate-aggregation-output-names";
        var requestJson = File.ReadAllText(RepoPaths.Fixture("adversarial", fixtureDir, "agGridRequest.json"));

        var ex = Assert.Throws<AggregationValidationException>(() => AgGridRequestParser.Parse(requestJson));

        // Both offending positions and the colliding name, so the message tells a host WHICH two
        // valueCols to fix.
        Assert.Contains("aggregations[0, 1]", ex.Message, StringComparison.Ordinal);
        Assert.Contains("'total'", ex.Message, StringComparison.Ordinal);

        // Cross-checked against the fixture's own oracle rather than against a literal repeated here:
        // if the case's intent ever changes, this breaks instead of passing for any rejection at all.
        using var rejection = JsonDocument.Parse(
            File.ReadAllText(RepoPaths.Fixture("adversarial", fixtureDir, "expected", "rejection.json")));

        Assert.Equal("requestParsing", rejection.RootElement.GetProperty("rejectedAt").GetString());
        Assert.Equal("duplicate-output-name", rejection.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>
    /// The counter-case that keeps the rule above from being read as "one field, one aggregation":
    /// <c>sum</c> and <c>avg</c> over the SAME field with no alias resolve to <c>totalAmount_sum</c> and
    /// <c>totalAmount_avg</c>, which do not collide — <c>page-export/011</c> is that exact shape
    /// (both <c>valueCols</c> carry <c>id == field == "totalAmount"</c>) and is compiled on Postgres.
    /// <c>group-aggregation/012</c> is NOT this shape: its two <c>valueCols</c> carry distinct ids,
    /// so AGG-2 gives each an explicit alias before either default is ever computed. Asserted here
    /// because a uniqueness check written against <see cref="Aggregation.ColumnId"/> instead of
    /// <see cref="Aggregation.OutputName"/> would pass the fixture above and reject this, and both
    /// are one-line changes.
    /// </summary>
    [Fact]
    public void Two_Functions_Over_One_Unaliased_Field_Are_Accepted_Because_Their_Output_Names_Differ()
    {
        const string request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [ { "id": "region", "displayName": "Region", "field": "region" } ],
              "valueCols": [
                { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "sum" },
                { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "avg" }
              ],
              "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null, "sortModel": []
            }
            """;

        var parsed = AgGridRequestParser.Parse(request);

        Assert.Equal(
            ["amount_sum", "amount_avg"],
            parsed.Aggregations!.Select(aggregation => aggregation.OutputName));
    }

    [Fact]
    public void NonObject_Root_Throws_MalformedRequest()
    {
        const string request = "[1, 2, 3]";

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }

    [Fact]
    public void NonObject_FilterModel_Entry_Throws_MalformedRequest()
    {
        const string request = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": { "status": "active" },
              "sortModel": []
            }
            """;

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(request));
        Assert.Equal("malformed-request", ex.Reason);
    }
}
