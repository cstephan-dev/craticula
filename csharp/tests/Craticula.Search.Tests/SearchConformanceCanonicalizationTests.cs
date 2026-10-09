using System.Text.Json.Nodes;

namespace Craticula.Search.Tests;

/// <summary>
/// Proves what the conformance comparison treats as significant (OSE-0, spec/conformance-runner.md §4):
/// object key order is not, array order is. <see cref="SearchFixtureCompiler.CanonicalJson(string)"/> is the
/// comparison <see cref="SearchConformanceFixtureTests"/> uses for every artifact, and the language-neutral
/// runner applies the same rule to the <c>search</c> backend, so the two must not drift apart.
/// </summary>
public class SearchConformanceCanonicalizationTests
{
    private const string Artifact =
        """{"query":{"bool":{"must":[{"term":{"a":1,"b":2}},{"range":{"c":{"gte":"x","lt":"y"}}}]}},"sort":[{"p":{"order":"asc"}},{"q":{"order":"desc"}}],"size":10,"from":0}""";

    [Fact]
    public void Reordering_Object_Keys_At_Any_Depth_Does_Not_Change_The_Comparison()
    {
        const string reordered =
            """{"from":0,"size":10,"sort":[{"p":{"order":"asc"}},{"q":{"order":"desc"}}],"query":{"bool":{"must":[{"term":{"b":2,"a":1}},{"range":{"c":{"lt":"y","gte":"x"}}}]}}}""";

        Assert.Equal(
            SearchFixtureCompiler.CanonicalJson(Artifact),
            SearchFixtureCompiler.CanonicalJson(reordered));
    }

    [Fact]
    public void Reordering_Array_Elements_Changes_The_Comparison()
    {
        var swapped = JsonNode.Parse(Artifact)!;
        var sort = swapped["sort"]!.AsArray();
        var first = sort[0]!.DeepClone();
        var second = sort[1]!.DeepClone();
        sort[0] = second;
        sort[1] = first;

        Assert.NotEqual(
            SearchFixtureCompiler.CanonicalJson(Artifact),
            SearchFixtureCompiler.CanonicalJson(swapped));
    }
}
