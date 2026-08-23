using System.Text;
using System.Text.Json.Nodes;
using Craticula.Core.Schema;
using OpenSearch.Client;
using OpenSearch.Net;
// OpenSearch.Client (the high-level client's own typed mapping DSL) declares its OWN `FieldType` enum,
// colliding with Craticula.Core.Schema.FieldType the moment both namespaces are imported in one file —
// this alias is what makes every unqualified `FieldType` below mean the schema's own type.
using FieldType = Craticula.Core.Schema.FieldType;

namespace Craticula.OpenSearch.Client.Tests;

/// <summary>
/// One real OpenSearch index, seeded with documents written in the test that needs them, against the
/// instance reachable at <c>CRATICULA_OPENSEARCH_TEST_URI</c> (default <c>http://localhost:9200</c>) —
/// a single-node <c>opensearchproject/opensearch</c> container with its security plugin disabled,
/// managed outside this repo's test run (see <c>docs/feat/1648/context.md</c>).
///
/// <para><b>Own INTERNAL copy of the pattern <c>Craticula.MongoDB.Driver.Tests.MongoFixtureDatabase</c>
/// establishes</b> (each is <see langword="internal"/> to its own assembly), with the identical
/// necessary difference that plan calls out: there is no second compiler to cross-check against, so
/// this type has no <c>ExecuteCompiled</c> raw-reader oracle — the proof is comparing
/// <c>Craticula.OpenSearch.Client</c>'s own output against an INDEPENDENTLY HAND-COMPUTED expected
/// sequence instead.</para>
///
/// <para><b>A fresh, uniquely-named index per instance, not the fixture schema's own declared
/// <see cref="GridSchema.Source"/>.</b> xunit runs test classes in parallel by default, and a real,
/// persistent OpenSearch server has no per-connection isolation the way SQLite's <c>:memory:</c> gives
/// Dapper's harness — a SHARED index name across this suite's tests would make them interfere with each
/// other. <see cref="Schema"/> is the fixture's own schema with <see cref="GridSchema.Source"/>
/// overridden to the fresh name.</para>
///
/// <para><b>The mapping is derived from the schema's own declarations</b> — a string field maps to
/// <c>keyword</c> (or <c>text</c> when <c>text.searchMode</c> is <c>analyzed</c>, since only that opt-in
/// ever issues a tokenized <c>match</c> against the raw field), with a <c>keyword</c>-typed multi-field
/// for a declared <c>physical.search.keywordSubField</c> and a <c>keyword</c>-typed,
/// custom-normalizer-backed multi-field for a declared <c>physical.search.normalizedSubField</c> — every
/// other <see cref="FieldType"/> maps to its natural OpenSearch counterpart. A <c>decimal</c> field maps
/// to <c>double</c> for search/aggregation purposes (PERF-4's own index-guidance obligation, punted to
/// the host by <c>Craticula.Search.SearchJson.Encode</c>'s own doc — "a host needing exact decimal
/// arithmetic maps the field <c>scaled_float</c> or <c>keyword</c>"), which does NOT affect this suite's
/// own decimal-fidelity assertions: <c>_source</c> is the index's verbatim STORED document regardless of
/// how a field is mapped for search, so <see cref="OpenSearchValueReader.Read"/> still reads the exact
/// JSON number token this database seeded.</para>
/// </summary>
internal sealed class OpenSearchFixtureDatabase : IAsyncDisposable
{
    private static readonly string ConnectionUri =
        Environment.GetEnvironmentVariable("CRATICULA_OPENSEARCH_TEST_URI") ?? "http://localhost:9200";

    private readonly string indexName;

    /// <summary>The fixture's own schema, with <see cref="GridSchema.Source"/> overridden to this
    /// instance's freshly-named index — see this type's own remarks.</summary>
    public GridSchema Schema { get; }

    /// <summary>The already-open low-level client <c>Craticula.OpenSearch.Client</c>'s own entry points
    /// execute against — connection ownership stays with the host, and here the host is this test
    /// harness.</summary>
    public IOpenSearchLowLevelClient Client { get; }

    /// <summary>How many documents were seeded — the denominator of the "this case is not a tautology"
    /// checks.</summary>
    public int SeededDocumentCount { get; }

    private OpenSearchFixtureDatabase(IOpenSearchLowLevelClient client, string indexName, GridSchema schema, int seededDocumentCount)
    {
        Client = client;
        this.indexName = indexName;
        Schema = schema;
        SeededDocumentCount = seededDocumentCount;
    }

    /// <summary>
    /// Connects to the real OpenSearch instance, creates a freshly-named index mapped from
    /// <paramref name="schema"/>, bulk-indexes <paramref name="documents"/>, and force-refreshes the
    /// index so a fresh write is immediately visible to a query — avoiding the eventual-consistency
    /// flakiness a real search engine's own near-real-time refresh interval would otherwise introduce
    /// into this suite.
    /// </summary>
    public static async Task<OpenSearchFixtureDatabase> CreateAsync(GridSchema schema, IReadOnlyList<JsonObject> documents)
    {
        var client = new OpenSearchClient(new ConnectionSettings(new Uri(ConnectionUri))).LowLevel;
        var indexName = $"{schema.Source}_{Guid.NewGuid():N}";

        var createResponse = await client
            .Indices.CreateAsync<StringResponse>(indexName, PostData.String(BuildMapping(schema).ToJsonString()))
            .ConfigureAwait(false);
        RequireSuccess(createResponse, "create index");

        if (documents.Count > 0)
        {
            var bulk = new StringBuilder();
            foreach (var document in documents)
            {
                bulk.Append("{\"index\":{}}\n").Append(document.ToJsonString()).Append('\n');
            }

            var bulkResponse = await client.BulkAsync<StringResponse>(indexName, PostData.String(bulk.ToString())).ConfigureAwait(false);
            RequireSuccess(bulkResponse, "bulk index");

            if (bulkResponse.Body.Contains("\"errors\":true", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Bulk indexing into '{indexName}' reported per-item errors: {bulkResponse.Body}");
            }
        }

        var refreshResponse = await client.Indices.RefreshAsync<StringResponse>(indexName).ConfigureAwait(false);
        RequireSuccess(refreshResponse, "refresh index");

        return new OpenSearchFixtureDatabase(client, indexName, schema with { Source = indexName }, documents.Count);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Client.Indices.DeleteAsync<StringResponse>(indexName).ConfigureAwait(false);
        }
        catch
        {
            // Best-effort teardown -- a leaked test index costs disk on the local dev container, not
            // correctness of any other test (each uses its own uniquely-named index).
        }
    }

    /// <summary>Checks the raw HTTP status rather than <see cref="StringResponse.Success"/> — see
    /// <c>Craticula.OpenSearch.Client.OpenSearchGridQuery.RequireSuccess</c>'s own remarks for why
    /// <c>Success</c> alone is not trustworthy (a non-2xx status the client whitelists per endpoint
    /// still reads back as <see langword="true"/>).</summary>
    private static void RequireSuccess(StringResponse response, string action)
    {
        if (response.HttpStatusCode is >= 200 and < 300)
        {
            return;
        }

        throw new InvalidOperationException($"OpenSearch '{action}' failed (HTTP {response.HttpStatusCode}): {response.DebugInformation}");
    }

    private static JsonObject BuildMapping(GridSchema schema)
    {
        var properties = new JsonObject();
        var normalizers = new JsonObject();

        foreach (var field in schema.Fields.Values)
        {
            var path = field.Physical?.Search?.Field ?? throw new InvalidOperationException(
                "Every field in a fixture this suite uses must declare physical.search.field.");
            properties[path] = BuildFieldMapping(field, normalizers);
        }

        var mapping = new JsonObject { ["mappings"] = new JsonObject { ["properties"] = properties } };

        if (normalizers.Count > 0)
        {
            mapping["settings"] = new JsonObject { ["analysis"] = new JsonObject { ["normalizer"] = normalizers } };
        }

        return mapping;
    }

    private static JsonObject BuildFieldMapping(FieldConfig field, JsonObject normalizers) => field.Type switch
    {
        FieldType.String => BuildStringMapping(field, normalizers),
        FieldType.Int64 => new JsonObject { ["type"] = "long" },
        FieldType.Decimal => new JsonObject { ["type"] = "double" },
        FieldType.Double => new JsonObject { ["type"] = "double" },
        FieldType.Boolean => new JsonObject { ["type"] = "boolean" },
        FieldType.Date => new JsonObject { ["type"] = "date", ["format"] = "strict_date" },
        FieldType.Datetime => new JsonObject { ["type"] = "date", ["format"] = "strict_date_optional_time" },
        _ => throw new InvalidOperationException($"Unmapped FieldType '{field.Type}'.")
    };

    private static JsonObject BuildStringMapping(FieldConfig field, JsonObject normalizers)
    {
        var analyzed = field.Text?.SearchMode == SearchMode.Analyzed;
        var mapping = new JsonObject { ["type"] = analyzed ? "text" : "keyword" };

        var search = field.Physical?.Search;
        var subFields = new JsonObject();

        if (search?.KeywordSubField is { } keywordSubField)
        {
            subFields[LocalName(keywordSubField)] = new JsonObject { ["type"] = "keyword" };
        }

        if (search?.NormalizedSubField is { } normalizedSubField)
        {
            normalizers["lowercase_normalizer"] = new JsonObject { ["type"] = "custom", ["filter"] = new JsonArray("lowercase") };
            subFields[LocalName(normalizedSubField)] = new JsonObject { ["type"] = "keyword", ["normalizer"] = "lowercase_normalizer" };
        }

        if (subFields.Count > 0)
        {
            mapping["fields"] = subFields;
        }

        return mapping;
    }

    /// <summary>A multi-field's OWN local name — <c>"region.keyword"</c>'s mapping is declared as a
    /// <c>"keyword"</c> entry under <c>"region"</c>'s own <c>"fields"</c>, addressed by the segment
    /// AFTER the dot, never the full dotted path.</summary>
    private static string LocalName(string subField) => subField.Split('.')[^1];
}
