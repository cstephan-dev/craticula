using Craticula.Core.Schema;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Craticula.MongoDB.Driver.Tests;

/// <summary>
/// One real MongoDB collection, seeded with documents written in the test that needs them, against the
/// instance reachable at <c>CRATICULA_MONGO_TEST_URI</c> (default <c>mongodb://localhost:27017</c>) — a
/// <c>mongo:7.0</c> container started via WSL2's Docker Engine for this repo's local development:
/// <c>wsl -e bash -c "docker run -d --name craticula-mongo-test -p 27017:27017 mongo:7.0"</c> (or
/// <c>docker start craticula-mongo-test</c> if the container already exists but stopped). A future CI
/// setup needs a deliberate decision here (Testcontainers, a compose file, or a hosted test instance)
/// rather than inheriting this manual setup silently.
///
/// <para><b>Own INTERNAL copy of the pattern <c>Craticula.Dapper.Tests.SqliteFixtureDatabase</c>
/// establishes</b> (each is <see langword="internal"/> to its own assembly), with the one necessary
/// difference the plan calls out: there is no second compiler to cross-check against (SQL has
/// EF-on-SQLite as an independent oracle; Mongo has only <c>Craticula.Mongo</c>'s own pipeline), so this
/// type has no <c>ExecuteCompiled</c> raw-reader oracle — the proof is comparing
/// <c>Craticula.MongoDB.Driver</c>'s own output against an INDEPENDENTLY HAND-COMPUTED expected sequence
/// instead.</para>
///
/// <para><b>A fresh, uniquely-named collection per instance, not the fixture schema's own declared
/// <see cref="GridSchema.Source"/>.</b> SQLite's <c>:memory:</c> gives Dapper's harness a fresh database
/// per connection for free; a real, persistent MongoDB server has no such per-connection isolation, and
/// xunit runs test classes in parallel by default, so a SHARED collection name across this suite's tests
/// would make them interfere with each other. <see cref="Schema"/> is the fixture's own schema with
/// <see cref="GridSchema.Source"/> overridden to the fresh name — every other declaration (fields,
/// stableKey, physical mappings) is untouched.</para>
/// </summary>
internal sealed class MongoFixtureDatabase : IAsyncDisposable
{
    private static readonly string ConnectionString =
        Environment.GetEnvironmentVariable("CRATICULA_MONGO_TEST_URI") ?? "mongodb://localhost:27017";

    private readonly string collectionName;

    /// <summary>The fixture's own schema, with <see cref="GridSchema.Source"/> overridden to this
    /// instance's freshly-named collection — see this type's own remarks.</summary>
    public GridSchema Schema { get; }

    /// <summary>The already-open database handle <c>Craticula.MongoDB.Driver</c>'s own entry points
    /// execute against — connection ownership stays with the host, exactly as the adapter's own doc
    /// states, and here the host is this test harness.</summary>
    public IMongoDatabase Database { get; }

    /// <summary>How many documents were seeded — the denominator of the "this case is not a tautology"
    /// checks (a filter/group that returns all of them, or none, proves nothing).</summary>
    public int SeededDocumentCount { get; }

    private MongoFixtureDatabase(IMongoDatabase database, string collectionName, GridSchema schema, int seededDocumentCount)
    {
        Database = database;
        this.collectionName = collectionName;
        Schema = schema;
        SeededDocumentCount = seededDocumentCount;
    }

    /// <summary>
    /// Connects to the real MongoDB instance and inserts <paramref name="documents"/> into a freshly-named
    /// collection derived from <paramref name="schema"/>'s own declared <see cref="GridSchema.Source"/>.
    /// An empty <paramref name="documents"/> list creates no collection at all — querying a collection
    /// that does not exist is not an error on MongoDB, it returns zero documents, exactly like an empty
    /// one would.
    /// </summary>
    public static async Task<MongoFixtureDatabase> CreateAsync(GridSchema schema, IReadOnlyList<BsonDocument> documents)
    {
        var client = new MongoClient(ConnectionString);
        var database = client.GetDatabase("craticula_mongodb_driver_tests");
        var collectionName = $"{schema.Source}_{Guid.NewGuid():N}";

        if (documents.Count > 0)
        {
            var collection = database.GetCollection<BsonDocument>(collectionName);
            await collection.InsertManyAsync(documents).ConfigureAwait(false);
        }

        return new MongoFixtureDatabase(database, collectionName, schema with { Source = collectionName }, documents.Count);
    }

    public async ValueTask DisposeAsync() =>
        await Database.DropCollectionAsync(collectionName).ConfigureAwait(false);
}
