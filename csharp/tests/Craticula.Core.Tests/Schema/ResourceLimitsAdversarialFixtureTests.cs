using System.Text.Json;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Exercises <see cref="ResourceLimitsValidator"/> against the real conformance fixtures authored
/// in #1630 (<c>spec/fixtures/adversarial/004-011</c>) and the accepted depth-8 boundary counter-case
/// in <c>spec/fixtures/filter-tree/013</c> — mirroring
/// <see cref="AdversarialFixtureAllowlistTests"/>'s pattern of reading real fixture files via
/// <see cref="RepoPaths"/> rather than hardcoding values, cross-checked against each fixture's own
/// <c>expected/rejection.json</c> <c>reason</c>.
///
/// Fixture 008 (<c>set-filter-1001-values</c>) is deliberately NOT exercised here: per its own
/// description.md, it uses AG Grid's native <c>set</c> filterModel type (a plain string values
/// array), not <c>customFilters</c> — turning that into an IR <see cref="FilterNode"/> is the
/// not-yet-built frontend adapter's job (out of #1632's scope), not something this validator's
/// fixture-driven tests can load directly. The same 1001-values-over-the-max-of-1000 boundary this
/// fixture proves is already covered directly in <see cref="ResourceLimitsValidatorTests"/>'s
/// hand-built <c>InValues_One_Beyond_Max_Throws_Citing_MaxInValuesExceeded</c>.
/// </summary>
public class ResourceLimitsAdversarialFixtureTests
{
    private static readonly ResourceLimits DefaultLimits = new();

    private static JsonDocument LoadRequest(string category, string fixtureDir)
    {
        var path = RepoPaths.Fixture(category, fixtureDir, "agGridRequest.json");
        Assert.True(File.Exists(path), $"Fixture request not found at '{path}'.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string ExpectedRejectionReason(string category, string fixtureDir)
    {
        var path = RepoPaths.Fixture(category, fixtureDir, "expected", "rejection.json");
        Assert.True(File.Exists(path), $"Fixture rejection oracle not found at '{path}'.");
        using var rejection = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal("resourceLimits", rejection.RootElement.GetProperty("rejectedAt").GetString());
        return rejection.RootElement.GetProperty("reason").GetString()!;
    }

    private static FilterNode ParseCustomFilters(JsonDocument request) =>
        JsonSerializer.Deserialize<FilterNode>(
            request.RootElement.GetProperty("customFilters").GetRawText(), GridSchemaJson.Options)
        ?? throw new JsonException("customFilters JSON deserialized to null.");

    private static PageSpec ParsePage(JsonDocument request) => new RangePage
    {
        StartRow = request.RootElement.GetProperty("startRow").GetInt32(),
        EndRow = request.RootElement.GetProperty("endRow").GetInt32(),
    };

    private static void AssertThrowsWithReason(FilterNode? filter, PageSpec page, string expectedReason)
    {
        var ex = Assert.Throws<ResourceLimitExceededException>(
            () => ResourceLimitsValidator.Validate(filter, page, DefaultLimits));

        Assert.Contains(ex.Violations, v => v.LimitName == expectedReason);
    }

    [Theory]
    [InlineData("004-custom-filter-depth-9-one-beyond-boundary")]
    [InlineData("005-custom-filter-depth-20-deep-bomb")]
    public void CustomFilters_Depth_Bomb_Fixtures_Throw_MaxDepthExceeded(string fixtureDir)
    {
        using var request = LoadRequest("adversarial", fixtureDir);
        var filter = ParseCustomFilters(request);
        var page = ParsePage(request);
        var expectedReason = ExpectedRejectionReason("adversarial", fixtureDir);

        Assert.Equal("max-depth-exceeded", expectedReason);
        AssertThrowsWithReason(filter, page, expectedReason);
    }

    [Theory]
    [InlineData("006-and-node-65-leaves-single-node")]
    [InlineData("007-nested-tree-wide-70-leaves-budget")]
    public void CustomFilters_Leaf_Bomb_Fixtures_Throw_MaxLeavesExceeded(string fixtureDir)
    {
        using var request = LoadRequest("adversarial", fixtureDir);
        var filter = ParseCustomFilters(request);
        var page = ParsePage(request);
        var expectedReason = ExpectedRejectionReason("adversarial", fixtureDir);

        Assert.Equal("max-leaves-exceeded", expectedReason);
        AssertThrowsWithReason(filter, page, expectedReason);
    }

    [Fact]
    public void Fixture009_CustomFilters_NotIn_5000_Values_Throws_MaxInValuesExceeded()
    {
        const string fixtureDir = "009-custom-not-in-5000-values-deep-bomb";
        using var request = LoadRequest("adversarial", fixtureDir);
        var filter = ParseCustomFilters(request);
        var page = ParsePage(request);
        var expectedReason = ExpectedRejectionReason("adversarial", fixtureDir);

        Assert.Equal("max-in-values-exceeded", expectedReason);
        AssertThrowsWithReason(filter, page, expectedReason);

        var comparison = Assert.IsType<ComparisonNode>(filter);
        Assert.Equal(5000, comparison.Values!.Count);
    }

    [Theory]
    [InlineData("010-page-size-1001-rows-one-beyond-boundary")]
    [InlineData("011-page-size-1000000-rows-deep-bomb")]
    public void Page_Size_Bomb_Fixtures_Throw_MaxPageSizeExceeded(string fixtureDir)
    {
        using var request = LoadRequest("adversarial", fixtureDir);
        var page = ParsePage(request);
        var expectedReason = ExpectedRejectionReason("adversarial", fixtureDir);

        Assert.Equal("max-page-size-exceeded", expectedReason);
        AssertThrowsWithReason(null, page, expectedReason);
    }

    [Fact]
    public void FilterTree013_CustomFilters_Depth_8_Boundary_Does_Not_Throw()
    {
        const string fixtureDir = "013-customfilters-depth-8-boundary";
        using var request = LoadRequest("filter-tree", fixtureDir);
        var filter = ParseCustomFilters(request);
        var page = ParsePage(request);

        ResourceLimitsValidator.Validate(filter, page, DefaultLimits);
    }
}
