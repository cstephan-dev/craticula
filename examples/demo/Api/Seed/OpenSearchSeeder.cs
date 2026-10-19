using System.Globalization;
using System.Text;
using System.Text.Json;
using Craticula.Core.Schema;
using Demo.Api.Grid;
using OpenSearch.Net;

namespace Demo.Api.Seed;

/// <summary>
/// Deletes and recreates index <c>orders</c>, then bulk-loads the rows in NDJSON batches with <c>refresh=true</c>. The mapping is the
/// library's (the Docker tier's <c>SearchEngine.BuildMapping</c>, SC-3): every string field is a <c>keyword</c> with the sub-fields
/// <c>keyword</c> (exact, what term, wildcard and the group key address) and <c>normalized</c> (a lowercase normalizer, what case-insensitive sort
/// and range address, SEM-1: a schema without it makes the compiler refuse the sort); <c>int64</c> is <c>long</c>, <c>decimal</c> is <c>double</c>
/// (OpenSearch computes every sum and average in double precision anyway), a datetime is a <c>date</c> in <c>strict_date_optional_time</c>
/// and a date a <c>date</c> in <c>strict_date</c>. The document id is the order id. The mapping's field names and sub-fields come from
/// <c>orders.schema.json</c> (<c>physical.search</c>), so the mapping and the compiler cannot drift; the property names of the documents
/// (<c>BulkBody</c>) are written out and must match them. One shard and no replica: a single node
/// is then green.
/// </summary>
public static class OpenSearchSeeder
{
    /// <summary>The index the schema's <c>source</c> names.</summary>
    public const string Index = "orders";

    private const int BatchRows = 2_000;

    /// <summary>The demo's grid schema, the file the API loads (copied next to the app).</summary>
    public static GridSchema LoadSchema() =>
        GridSchema.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, DemoApp.SchemaFile)));

    public static async Task<long> SeedAsync(string url, IReadOnlyList<OrderRow> rows)
    {
        var schema = LoadSchema();
        var target = ConnectionStringGuard.Build(
            url,
            value => OpenSearchGridService.ParseUrl(value),
            () => new SeedSettingException(ConnectionStringGuard.Message(DemoSettings.OpenSearchEnvVar, "OpenSearch", "URL")));
        var client = new OpenSearchLowLevelClient(new ConnectionConfiguration(target)
            .DisablePing()
            .RequestTimeout(TimeSpan.FromMinutes(2)));

        // 404 is fine here: the index does not exist yet. Any other failure is reported.
        var drop = await client.Indices.DeleteAsync<StringResponse>(Index);
        if (drop.HttpStatusCode != 404)
        {
            OpenSearchCalls.Require(drop, "Deleting the index");
        }

        OpenSearchCalls.Require(
            await client.Indices.CreateAsync<StringResponse>(Index, PostData.String(Mapping(schema))), "Creating the index");

        for (var start = 0; start < rows.Count; start += BatchRows)
        {
            var bulk = BulkBody(rows, start, Math.Min(BatchRows, rows.Count - start));
            var response = await client.BulkAsync<StringResponse>(
                Index, PostData.String(bulk), new BulkRequestParameters { Refresh = Refresh.True });
            OpenSearchCalls.Require(response, "Bulk indexing");
            using var result = JsonDocument.Parse(response.Body);
            if (result.RootElement.GetProperty("errors").GetBoolean())
            {
                throw new InvalidOperationException("Bulk indexing reported per-item errors (the first response is not echoed).");
            }
        }

        var count = await client.CountAsync<StringResponse>(Index, PostData.String("{}"));
        OpenSearchCalls.Require(count, "Counting the index");
        using var counted = JsonDocument.Parse(count.Body);
        return counted.RootElement.GetProperty("count").GetInt64();
    }

    /// <summary>The index settings and mapping as JSON text, derived from the schema.</summary>
    public static string Mapping(GridSchema schema)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream))
        {
            json.WriteStartObject();
            json.WriteStartObject("settings");
            json.WriteNumber("number_of_shards", 1);
            json.WriteNumber("number_of_replicas", 0);
            json.WriteStartObject("analysis");
            json.WriteStartObject("normalizer");
            json.WriteStartObject("lowercase_normalizer");
            json.WriteString("type", "custom");
            json.WriteStartArray("filter");
            json.WriteStringValue("lowercase");
            json.WriteEndArray();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteStartObject("mappings");
            json.WriteStartObject("properties");
            foreach (var (colId, field) in schema.Fields)
            {
                json.WriteStartObject(field.Physical?.Search?.Field ?? colId);
                WriteFieldType(json, field);
                json.WriteEndObject();
            }

            json.WriteEndObject();
            json.WriteEndObject();
            json.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteFieldType(Utf8JsonWriter json, FieldConfig field)
    {
        switch (field.Type)
        {
            case FieldType.String:
                json.WriteString("type", "keyword");
                var search = field.Physical?.Search;
                json.WriteStartObject("fields");
                if (search?.KeywordSubField is { } keyword)
                {
                    json.WriteStartObject(keyword.Split('.')[^1]);
                    json.WriteString("type", "keyword");
                    json.WriteEndObject();
                }

                if (search?.NormalizedSubField is { } normalized)
                {
                    json.WriteStartObject(normalized.Split('.')[^1]);
                    json.WriteString("type", "keyword");
                    json.WriteString("normalizer", "lowercase_normalizer");
                    json.WriteEndObject();
                }

                json.WriteEndObject();
                break;
            case FieldType.Int64:
                json.WriteString("type", "long");
                break;
            case FieldType.Decimal or FieldType.Double:
                json.WriteString("type", "double");
                break;
            case FieldType.Boolean:
                json.WriteString("type", "boolean");
                break;
            case FieldType.Date:
                json.WriteString("type", "date");
                json.WriteString("format", "strict_date");
                break;
            case FieldType.Datetime:
                json.WriteString("type", "date");
                json.WriteString("format", "strict_date_optional_time");
                break;
            default:
                throw new InvalidOperationException($"Unmapped FieldType '{field.Type}'.");
        }
    }

    /// <summary>One NDJSON body: an action line (the order id is the document id) and a document line per row. A decimal stays a JSON
    /// number, an instant is UTC with three fraction digits and a Z, a date is <c>yyyy-MM-dd</c>, an absent value is an explicit null.</summary>
    private static string BulkBody(IReadOnlyList<OrderRow> rows, int start, int count)
    {
        using var stream = new MemoryStream();
        using (var json = new Utf8JsonWriter(stream, new JsonWriterOptions { SkipValidation = true }))
        {
            for (var i = start; i < start + count; i++)
            {
                var row = rows[i];
                json.WriteStartObject();
                json.WriteStartObject("index");
                json.WriteString("_id", row.Id.ToString(CultureInfo.InvariantCulture));
                json.WriteEndObject();
                json.WriteEndObject();
                json.Flush();
                stream.WriteByte((byte)'\n');
                json.Reset();

                json.WriteStartObject();
                json.WriteNumber("id", row.Id);
                json.WriteString("orderNo", row.OrderNo);
                WriteText(json, "customer", row.Customer);
                WriteText(json, "country", row.Country);
                WriteText(json, "category", row.Category);
                json.WriteString("status", row.Status);
                if (row.Amount is { } amount)
                {
                    json.WriteNumber("amount", amount);
                }
                else
                {
                    json.WriteNull("amount");
                }

                if (row.Quantity is { } quantity)
                {
                    json.WriteNumber("quantity", quantity);
                }
                else
                {
                    json.WriteNull("quantity");
                }

                json.WriteString("orderedAt", DateTime.SpecifyKind(row.OrderedAt, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture));
                if (row.ShippedOn is { } shipped)
                {
                    json.WriteString("shippedOn", shipped.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                }
                else
                {
                    json.WriteNull("shippedOn");
                }

                json.WriteBoolean("isPriority", row.IsPriority);
                json.WriteEndObject();
                json.Flush();
                stream.WriteByte((byte)'\n');
                json.Reset();
            }
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteText(Utf8JsonWriter json, string name, string? value)
    {
        if (value is null)
        {
            json.WriteNull(name);
        }
        else
        {
            json.WriteString(name, value);
        }
    }
}
