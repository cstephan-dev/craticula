using Craticula.Integration.Tests.Docker;
using Craticula.Integration.Tests.Oracle;

namespace Craticula.Integration.Tests.Containers.Document;

[CollectionDefinition(Name)]
public sealed class OpenSearchCollection : ICollectionFixture<OpenSearchEngineFixture>
{
    public const string Name = "Document engine: OpenSearch";
}

[CollectionDefinition(Name)]
public sealed class ElasticsearchCollection : ICollectionFixture<ElasticsearchEngineFixture>
{
    public const string Name = "Document engine: Elasticsearch";
}

/// <summary>T-1b on OpenSearch 2.x for every fixture that carries <c>expected/search.artifact.json</c> (XEQ-1, XEQ-4).</summary>
[Collection(OpenSearchCollection.Name)]
public sealed class OpenSearchExecutionTests(OpenSearchEngineFixture engine)
{
    public static IEnumerable<object[]> Fixtures() => ExecutionAssertions.FixturesWith("search");

    [DockerTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Executes_To_The_Expected_Rows(string name)
    {
        var fixture = ExecutionFixture.Load(name);
        await ExecutionAssertions.VerifyAsync(fixture, "search", engine.EngineName, () => SearchEngine.RunAsync(engine.Require(), fixture));
    }
}

/// <summary>T-1b on Elasticsearch 8.x for the same fixtures: one artifact, two engines (OQ-7), so one can differ alone (XEQ-4).</summary>
[Collection(ElasticsearchCollection.Name)]
public sealed class ElasticsearchExecutionTests(ElasticsearchEngineFixture engine)
{
    public static IEnumerable<object[]> Fixtures() => ExecutionAssertions.FixturesWith("search");

    [DockerTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Executes_To_The_Expected_Rows(string name)
    {
        var fixture = ExecutionFixture.Load(name);
        await ExecutionAssertions.VerifyAsync(fixture, "search", engine.EngineName, () => SearchEngine.RunAsync(engine.Require(), fixture));
    }
}
