namespace Craticula.AgGrid.Tests;

/// <summary>
/// AG-1/AG-5's Definition of Done: unsupported/out-of-scope shapes are explicitly rejected, never
/// silently ignored. Covers the rejections that don't fit naturally into the per-filterType test
/// files (<c>pivotMode</c>/<c>pivotCols</c>, the <c>setValuesRequest</c> envelope, and the deserializer-
/// fuzz shapes real <c>adversarial/</c> fixtures pin) plus a few generic malformed-root cases.
/// </summary>
public class AgGridRequestParserRejectionTests
{
    // No real fixture anywhere sets pivotMode:true (all 158 fixtures use pivotMode:false/pivotCols:[]
    // — see docs/feat/1635/context.md's research notes) — this synthetic request is the only way to
    // exercise the rejection at all.
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

    [Fact]
    public void SetValuesRequest_Envelope_Fixture_016_Throws_MalformedRequest()
    {
        var path = RepoPaths.Fixture("group-aggregation", "016-set-filter-values-no-active-filters", "agGridRequest.json");
        var requestJson = File.ReadAllText(path);

        var ex = Assert.Throws<AgGridRequestParseException>(() => AgGridRequestParser.Parse(requestJson));
        Assert.Equal("malformed-request", ex.Reason);
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
