using Craticula.Integration.Tests.Docker;
using Craticula.Integration.Tests.Oracle;

namespace Craticula.Integration.Tests.Containers.Document;

[CollectionDefinition(Name)]
public sealed class MongoCollection : ICollectionFixture<MongoEngineFixture>
{
    public const string Name = "Document engine: MongoDB";
}

/// <summary>T-1b on MongoDB 7.0 for every fixture that carries <c>expected/mongo.artifact.json</c> (XEQ-1).</summary>
[Collection(MongoCollection.Name)]
public sealed class MongoExecutionTests(MongoEngineFixture engine)
{
    public static IEnumerable<object[]> Fixtures() => ExecutionAssertions.FixturesWith("mongo");

    [DockerTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Executes_To_The_Expected_Rows(string name)
    {
        var fixture = ExecutionFixture.Load(name);
        await ExecutionAssertions.VerifyAsync(fixture, "mongo", null, () => MongoEngine.RunAsync(engine.Require(), fixture));
    }
}
