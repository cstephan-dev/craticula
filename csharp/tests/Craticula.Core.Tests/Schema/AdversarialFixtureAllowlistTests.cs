using System.Text.Json;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Exercises <see cref="FieldAllowlistValidator"/> against the real conformance fixtures authored
/// in #1630 (<c>spec/fixtures/adversarial/001-003-unknown-colid-*</c>), each of which pairs a
/// Strict-mode <c>schema.json</c> with an <c>agGridRequest.json</c> referencing a colId absent
/// from that schema's field allowlist — in filterModel, sortModel, and rowGroupCols respectively.
///
/// The colId under test is read from each fixture's own <c>agGridRequest.json</c> (not hardcoded),
/// and the expected rejection stage/reason is cross-checked against <c>expected/rejection.json</c>
/// — so these tests break if a fixture's actual intent ever changes, rather than passing for any
/// arbitrary unlisted string.
/// </summary>
public class AdversarialFixtureAllowlistTests
{
    private static GridSchema LoadSchema(string fixtureDir)
    {
        var path = RepoPaths.Fixture("adversarial", fixtureDir, "schema.json");
        Assert.True(File.Exists(path), $"Fixture schema not found at '{path}'.");
        return GridSchema.Parse(File.ReadAllText(path));
    }

    private static JsonDocument LoadRequest(string fixtureDir)
    {
        var path = RepoPaths.Fixture("adversarial", fixtureDir, "agGridRequest.json");
        Assert.True(File.Exists(path), $"Fixture request not found at '{path}'.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static void AssertRejectedAsUnknownField(string fixtureDir)
    {
        var path = RepoPaths.Fixture("adversarial", fixtureDir, "expected", "rejection.json");
        Assert.True(File.Exists(path), $"Fixture rejection oracle not found at '{path}'.");
        using var rejection = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal("allowlist", rejection.RootElement.GetProperty("rejectedAt").GetString());
        Assert.Equal("unknown-field", rejection.RootElement.GetProperty("reason").GetString());
    }

    /// <summary>#1872's own reason value, distinct from <see cref="AssertRejectedAsUnknownField"/>'s
    /// -- the colId in every fixture that uses this helper IS allowlisted, just declared
    /// filterable:false (via filterModel, setValuesRequest.colId, or, #1907, a consumed groupKeys
    /// drill-down key).</summary>
    private static void AssertRejectedAsFieldNotFilterable(string fixtureDir)
    {
        var path = RepoPaths.Fixture("adversarial", fixtureDir, "expected", "rejection.json");
        Assert.True(File.Exists(path), $"Fixture rejection oracle not found at '{path}'.");
        using var rejection = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal("allowlist", rejection.RootElement.GetProperty("rejectedAt").GetString());
        Assert.Equal("field-not-filterable", rejection.RootElement.GetProperty("reason").GetString());
    }

    [Fact]
    public void Fixture001_UnknownColId_In_FilterModel_Throws_UnknownFieldException()
    {
        const string fixtureDir = "001-unknown-colid-filter-model";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        // filterModel is a JSON object keyed by colId — the fixture's whole request has exactly
        // one entry, and it is the unlisted one.
        var colId = request.RootElement.GetProperty("filterModel").EnumerateObject().Single().Name;

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, [colId]));

        Assert.Contains(colId, ex.ColIds);
        AssertRejectedAsUnknownField(fixtureDir);
    }

    [Fact]
    public void Fixture002_UnknownColId_In_SortModel_Throws_UnknownFieldException()
    {
        const string fixtureDir = "002-unknown-colid-sort-model";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var colId = request.RootElement.GetProperty("sortModel").EnumerateArray().Single()
            .GetProperty("colId").GetString()!;

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, [colId]));

        Assert.Contains(colId, ex.ColIds);
        AssertRejectedAsUnknownField(fixtureDir);
    }

    [Fact]
    public void Fixture028_UnknownColId_In_SortModel_After_The_StableKey_Throws_UnknownFieldException()
    {
        const string fixtureDir = "028-unknown-colid-sort-model-after-stable-key";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        // sortModel is [id, internalRank]: the stable key first, the undeclared colId second. SEM-6's reduction would drop the second key
        // unseen; the allowlist check over every colId of the request still names it.
        var colIds = request.RootElement.GetProperty("sortModel").EnumerateArray()
            .Select(item => item.GetProperty("colId").GetString()!).ToArray();

        var ex = Assert.Throws<UnknownFieldException>(() => FieldAllowlistValidator.Validate(schema, colIds));

        Assert.Equal(["internalRank"], ex.ColIds);
        AssertRejectedAsUnknownField(fixtureDir);
    }

    [Fact]
    public void Fixture003_UnknownColId_In_RowGroupCols_Throws_UnknownFieldException()
    {
        const string fixtureDir = "003-unknown-colid-row-group";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var colId = request.RootElement.GetProperty("rowGroupCols").EnumerateArray().Single()
            .GetProperty("id").GetString()!;

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.ValidateStructuralColumns(
                schema, new GroupSpec { GroupCols = [colId], GroupKeys = [] }, null));

        Assert.Contains(ex.ColIds, rejected => rejected.StartsWith(colId, StringComparison.Ordinal));
        AssertRejectedAsUnknownField(fixtureDir);
    }

    /// <summary>
    /// The assertion above shows the check exists; this one shows a host actually reaches it. #1640's
    /// review found <c>rowGroupCols</c> was the one client-controlled colId class that never entered
    /// <see cref="GridFilterPipeline"/> at all — the fixture's own oracle was satisfied only by a test
    /// hand-feeding the colId to the validator. Driving the fixture through the pipeline instead is
    /// what makes <c>rejectedAt: "allowlist"</c> true of the real path.
    /// </summary>
    [Fact]
    public void Fixture003_Is_Rejected_By_The_Pipeline_Itself_Not_Only_By_A_Direct_Validator_Call()
    {
        const string fixtureDir = "003-unknown-colid-row-group";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var colId = request.RootElement.GetProperty("rowGroupCols").EnumerateArray().Single()
            .GetProperty("id").GetString()!;

        var ex = Assert.Throws<UnknownFieldException>(() => GridFilterPipeline.Process(
            nativeFilter: null,
            customFilters: null,
            page: new RangePage { StartRow = 0, EndRow = 100 },
            schema: schema,
            limits: new ResourceLimits(),
            group: new GroupSpec { GroupCols = [colId], GroupKeys = [] }));

        Assert.Contains(ex.ColIds, rejected => rejected.StartsWith(colId, StringComparison.Ordinal));
        AssertRejectedAsUnknownField(fixtureDir);
    }

    /// <summary>#1872: a colId present in the schema's allowlist but declared filterable:false,
    /// referenced by an ordinary filterModel condition. Distinct from 001-003's genuinely unknown
    /// colId -- this one IS allowlisted, so <see cref="FieldAllowlistValidator.Validate"/>'s own
    /// capability check is what rejects it.</summary>
    [Fact]
    public void Fixture024_NonFilterableColId_In_FilterModel_Throws_UnknownFieldException()
    {
        const string fixtureDir = "024-filterable-false-in-filter-model";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var colId = request.RootElement.GetProperty("filterModel").EnumerateObject().Single().Name;
        Assert.False(schema.Fields[colId].Filterable, $"Fixture {fixtureDir}'s own colId '{colId}' must be declared filterable:false for this case to test anything.");

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, [colId]));

        Assert.Equal("field-not-filterable", Assert.Single(ex.Violations).Reason);
        AssertRejectedAsFieldNotFilterable(fixtureDir);
    }

    /// <summary>The pipeline itself refuses it, not only a direct validator call -- mirrors
    /// <see cref="Fixture003_Is_Rejected_By_The_Pipeline_Itself_Not_Only_By_A_Direct_Validator_Call"/>.</summary>
    [Fact]
    public void Fixture024_Is_Rejected_By_The_Pipeline_Itself_Not_Only_By_A_Direct_Validator_Call()
    {
        const string fixtureDir = "024-filterable-false-in-filter-model";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var colId = request.RootElement.GetProperty("filterModel").EnumerateObject().Single().Name;
        var filter = new ComparisonNode
        {
            Field = colId,
            Op = ComparisonOperator.Gt,
            Value = new DecimalValue { Value = "100" }
        };

        var ex = Assert.Throws<UnknownFieldException>(() => GridFilterPipeline.Process(
            nativeFilter: filter,
            customFilters: null,
            page: new RangePage { StartRow = 0, EndRow = 100 },
            schema: schema,
            limits: new ResourceLimits()));

        // Reason, not just colId — the pipeline must reject this AS non-filterable, not merely reject
        // it (an "unknown-field" verdict on the same colId would pass a colId-only assertion too).
        Assert.Equal("field-not-filterable", Assert.Single(ex.Violations).Reason);
        AssertRejectedAsFieldNotFilterable(fixtureDir);
    }

    /// <summary>#1872's set-values counterpart to <see cref="Fixture024_NonFilterableColId_In_FilterModel_Throws_UnknownFieldException"/>
    /// -- the same colId, this time as <c>setValuesRequest.colId</c>, which
    /// <see cref="FieldAllowlistValidator.ValidateStructuralColumns"/> rejects rather than
    /// <see cref="FieldAllowlistValidator.Validate"/>.</summary>
    [Fact]
    public void Fixture025_NonFilterableColId_As_SetValuesTarget_Throws_UnknownFieldException()
    {
        const string fixtureDir = "025-filterable-false-as-set-values-target";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var colId = request.RootElement.GetProperty("setValuesRequest").GetProperty("colId").GetString()!;
        Assert.False(schema.Fields[colId].Filterable, $"Fixture {fixtureDir}'s own colId '{colId}' must be declared filterable:false for this case to test anything.");

        var ex = Assert.Throws<UnknownFieldException>(() => FieldAllowlistValidator.ValidateStructuralColumns(
            schema, group: null, aggregations: null, new SetValuesSpec { ColId = colId }));

        Assert.Equal("field-not-filterable", Assert.Single(ex.Violations).Reason);
        AssertRejectedAsFieldNotFilterable(fixtureDir);
    }

    /// <summary>The pipeline itself refuses it, not only a direct validator call -- via
    /// <see cref="GridFilterPipeline.ProcessSetValues"/>, GRP-6's own entry point.</summary>
    [Fact]
    public void Fixture025_Is_Rejected_By_The_Pipeline_Itself_Not_Only_By_A_Direct_Validator_Call()
    {
        const string fixtureDir = "025-filterable-false-as-set-values-target";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var colId = request.RootElement.GetProperty("setValuesRequest").GetProperty("colId").GetString()!;

        var ex = Assert.Throws<UnknownFieldException>(() => GridFilterPipeline.ProcessSetValues(
            nativeFilter: null,
            customFilters: null,
            setValues: new SetValuesSpec { ColId = colId },
            schema: schema,
            limits: new ResourceLimits()));

        Assert.Equal("field-not-filterable", Assert.Single(ex.Violations).Reason);
        AssertRejectedAsFieldNotFilterable(fixtureDir);
    }

    /// <summary>#1907's drill-down counterpart to <see cref="Fixture024_NonFilterableColId_In_FilterModel_Throws_UnknownFieldException"/>
    /// -- the same rejection reason, this time the colId never appears in a <c>filterModel</c> at all:
    /// it is consumed as a <c>groupKeys</c> drill-down key, which
    /// <see cref="Craticula.Core.Ir.DrillDownFilter.Synthesize"/> turns into a literal filter
    /// predicate. Found by #1872's own code review as an out-of-scope gap in that story.</summary>
    [Fact]
    public void Fixture026_NonFilterableColId_ConsumedAsDrillDownKey_Throws_UnknownFieldException()
    {
        const string fixtureDir = "026-filterable-false-as-drilldown-key";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var colId = request.RootElement.GetProperty("rowGroupCols").EnumerateArray().Single()
            .GetProperty("id").GetString()!;
        var keyValue = request.RootElement.GetProperty("groupKeys").EnumerateArray().Single()
            .GetProperty("value").GetString()!;
        Assert.False(schema.Fields[colId].Filterable, $"Fixture {fixtureDir}'s own colId '{colId}' must be declared filterable:false for this case to test anything.");

        var group = new GroupSpec { GroupCols = [colId], GroupKeys = [new StringValue { Value = keyValue }] };

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.ValidateStructuralColumns(schema, group, aggregations: null));

        Assert.Equal("field-not-filterable", Assert.Single(ex.Violations).Reason);
        AssertRejectedAsFieldNotFilterable(fixtureDir);
    }

    /// <summary>The pipeline itself refuses it, not only a direct validator call -- via
    /// <see cref="GridFilterPipeline.Process"/>, mirroring <see cref="Fixture003_Is_Rejected_By_The_Pipeline_Itself_Not_Only_By_A_Direct_Validator_Call"/>.</summary>
    [Fact]
    public void Fixture026_Is_Rejected_By_The_Pipeline_Itself_Not_Only_By_A_Direct_Validator_Call()
    {
        const string fixtureDir = "026-filterable-false-as-drilldown-key";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var colId = request.RootElement.GetProperty("rowGroupCols").EnumerateArray().Single()
            .GetProperty("id").GetString()!;
        var keyValue = request.RootElement.GetProperty("groupKeys").EnumerateArray().Single()
            .GetProperty("value").GetString()!;

        var group = new GroupSpec { GroupCols = [colId], GroupKeys = [new StringValue { Value = keyValue }] };

        var ex = Assert.Throws<UnknownFieldException>(() => GridFilterPipeline.Process(
            nativeFilter: null,
            customFilters: null,
            page: new RangePage { StartRow = 0, EndRow = 100 },
            schema: schema,
            limits: new ResourceLimits(),
            group: group));

        Assert.Equal("field-not-filterable", Assert.Single(ex.Violations).Reason);
        AssertRejectedAsFieldNotFilterable(fixtureDir);
    }
}
