using System.Text.Json;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// Exercises <see cref="FilterTreeMerge.CustomFilterMerge"/> against the real conformance fixtures
/// authored in #1630 (<c>spec/fixtures/filter-tree/007-015</c>).
///
/// <c>agGridRequest.json</c>'s <c>filterModel</c> is AG Grid's own wire format, which this repo has
/// no adapter for yet (see <c>spec/fixtures/filter-tree/README.md</c>'s own "why customFilters is
/// load-bearing here" section) — only <c>customFilters</c> is already IR-shaped <see cref="FilterNode"/>
/// JSON. So:
/// <list type="bullet">
/// <item>Fixtures <c>010</c>-<c>013</c> (<c>filterModel: null</c>, per the README's own "Cases" table
/// and the depth-boundary table) give a real, fully fixture-driven merge check: with no native side,
/// <see cref="FilterTreeMerge.CustomFilterMerge"/> must degenerate to the <c>customFilters</c>
/// fragment itself, unwrapped — exactly the README's second merge rule.</item>
/// <item>Fixtures <c>007</c>-<c>009</c>/<c>014</c>-<c>015</c> pair a real native <c>filterModel</c>
/// with <c>customFilters</c> — proving the "both present" merge rule end-to-end would require
/// building the native side from <c>filterModel</c>, which is exactly the not-yet-built frontend
/// adapter's job (out of this story's scope, same precedent as
/// <c>ResourceLimitsAdversarialFixtureTests</c> skipping fixture <c>adversarial/008</c>). These are
/// only used here to confirm each fixture's own <c>customFilters</c> fragment is well-formed IR
/// (deserializes and passes <see cref="FilterNodeValidator"/>) — the "both present, opaque nesting"
/// rule itself is covered by <see cref="FilterTreeMergeTests"/>'s hand-built trees instead.
/// </list>
/// </summary>
public class FilterTreeMergeFixtureTests
{
    private static FilterNode ParseCustomFilters(string fixtureDir)
    {
        var path = RepoPaths.Fixture("filter-tree", fixtureDir, "agGridRequest.json");
        Assert.True(File.Exists(path), $"Fixture request not found at '{path}'.");
        using var request = JsonDocument.Parse(File.ReadAllText(path));

        return JsonSerializer.Deserialize<FilterNode>(
            request.RootElement.GetProperty("customFilters").GetRawText(), GridSchemaJson.Options)
            ?? throw new JsonException("customFilters JSON deserialized to null.");
    }

    [Theory]
    [InlineData("010-customfilters-only-not-no-native-filter")]
    [InlineData("011-customfilters-depth-3")]
    [InlineData("012-customfilters-depth-5")]
    [InlineData("013-customfilters-depth-8-boundary")]
    public void Merge_Degenerates_To_The_CustomFilters_Fragment_When_FilterModel_Is_Null(string fixtureDir)
    {
        var customFilters = ParseCustomFilters(fixtureDir);

        var merged = FilterTreeMerge.CustomFilterMerge(null, customFilters);

        Assert.Same(customFilters, merged);
    }

    [Theory]
    [InlineData("007-customfilters-not-wraps-leaf-merged")]
    [InlineData("008-customfilters-not-wraps-and-merged")]
    [InlineData("009-customfilters-not-nested-in-or-merged")]
    [InlineData("014-native-multicolumn-and-plus-customfilters-merge")]
    [InlineData("015-kitchen-sink-and-or-not-merge")]
    public void CustomFilters_Fragment_Is_Well_Formed_IR_On_Fixtures_Pairing_It_With_A_Native_FilterModel(string fixtureDir)
    {
        // These fixtures also carry a native filterModel this repo can't yet parse into a FilterNode
        // (see class doc comment) — this only proves the customFilters half is real, valid IR that
        // FilterNodeValidator (run by CustomFilterMerge as defense in depth) accepts.
        var customFilters = ParseCustomFilters(fixtureDir);

        FilterNodeValidator.Validate(customFilters);
    }
}
