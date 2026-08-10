using System.Text.Json;
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
    public void Fixture003_UnknownColId_In_RowGroupCols_Throws_UnknownFieldException()
    {
        const string fixtureDir = "003-unknown-colid-row-group";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var colId = request.RootElement.GetProperty("rowGroupCols").EnumerateArray().Single()
            .GetProperty("id").GetString()!;

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, [colId]));

        Assert.Contains(colId, ex.ColIds);
        AssertRejectedAsUnknownField(fixtureDir);
    }
}
