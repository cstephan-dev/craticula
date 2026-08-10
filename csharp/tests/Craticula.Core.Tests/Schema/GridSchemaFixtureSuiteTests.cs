using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Every <c>schema.json</c> across the entire #1630 conformance fixture suite must deserialize and
/// pass <see cref="GridSchema.Parse"/>'s invariant checks cleanly — these are the real, hand-authored
/// GridSchema documents the fixture suite was built around, so a hardening change to
/// <see cref="GridSchemaJson.Options"/> or a new <see cref="GridSchema.Parse"/> invariant that broke
/// any of them would mean the C# model no longer matches what <c>spec/grid-schema.schema.json</c>
/// actually allows. Deliberately NOT a generic fixture-suite runner (that is Feature #1624 / #1654's
/// job) — this only exercises the one shape (schema.json) this story owns.
/// </summary>
public class GridSchemaFixtureSuiteTests
{
    public static IEnumerable<object[]> AllFixtureSchemaFiles() =>
        Directory.EnumerateFiles(Path.Combine(RepoPaths.Root, "spec", "fixtures"), "schema.json", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal)
            .Select(p => new object[] { p });

    [Theory]
    [MemberData(nameof(AllFixtureSchemaFiles))]
    public void Every_Fixture_GridSchema_Parses_Cleanly(string schemaPath)
    {
        var json = File.ReadAllText(schemaPath);

        var exception = Record.Exception(() => GridSchema.Parse(json));

        Assert.Null(exception);
    }

    [Fact]
    public void At_Least_150_Fixture_Schemas_Were_Exercised()
    {
        // A sanity floor, not a hardcoded exact count (the suite grows over time) — guards against
        // this test silently exercising zero fixtures if spec/fixtures/ is ever moved/renamed.
        Assert.True(AllFixtureSchemaFiles().Count() >= 150);
    }
}
