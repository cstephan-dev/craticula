using System.Net;
using System.Text;
using System.Text.Json;

namespace Demo.Api.Tests;

/// <summary>
/// The API end to end: real HTTP, real PostgreSQL and MongoDB. Every database test runs the same request through
/// <c>/api/postgres/...</c>, <c>/api/mongo/...</c> and <c>/api/ef/...</c> (the Entity Framework Core adapter on PostgreSQL)
/// and expects the same answer; <c>postgres</c> is the baseline.
/// </summary>
[Collection("api")]
public sealed class ApiTests(ApiHostFixture api)
{
    private static readonly string[] Providers = ["postgres", "mongo", "ef"];

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task<JsonElement> RowsOk(string provider, string body)
    {
        var (status, json) = await api.PostJsonAsync(provider, "rows", body);
        Assert.True(HttpStatusCode.OK == status, $"{provider}: {status} {json}");
        return json;
    }

    private static long[] Ids(JsonElement page) =>
        [.. page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("id").GetInt64())];

    private async Task<(long[] Postgres, long[] Mongo)> IdsOnBoth(string body)
    {
        var pg = await RowsOk("postgres", body);
        var mongo = await RowsOk("mongo", body);
        var ef = await RowsOk("ef", body);
        Assert.Equal(pg.GetProperty("lastRow").GetInt64(), mongo.GetProperty("lastRow").GetInt64());
        Assert.Equal(pg.GetProperty("lastRow").GetInt64(), ef.GetProperty("lastRow").GetInt64());
        Assert.Equal(Ids(pg), Ids(ef));
        return (Ids(pg), Ids(mongo));
    }

    // ---- (a)-(b) filters and sort ------------------------------------------------------------------

    [RequiresDatabasesFact]
    public async Task TextNumberFilterWithSort_ReturnsTheSameIdsInTheSameOrder()
    {
        var (postgres, mongo) = await IdsOnBoth(Requests.TextNumberSort);

        Assert.Equal(50, postgres.Length);
        Assert.Equal(postgres, mongo);
    }

    [RequiresDatabasesFact]
    public async Task DateRangeFilter_GivesEqualIds()
    {
        var (postgres, mongo) = await IdsOnBoth(Requests.DateRange);

        Assert.NotEmpty(postgres);
        Assert.Equal(postgres, mongo);
    }

    [RequiresDatabasesFact]
    public async Task DatetimeRangeFilter_GivesEqualIds()
    {
        var (postgres, mongo) = await IdsOnBoth(Requests.DatetimeRange);

        Assert.NotEmpty(postgres);
        Assert.Equal(postgres, mongo);
    }

    [RequiresDatabasesFact]
    public async Task BooleanFilter_GivesEqualIds()
    {
        var (postgres, mongo) = await IdsOnBoth(Requests.Boolean);

        Assert.Equal(100, postgres.Length);
        Assert.Equal(postgres, mongo);
    }

    // ---- (c) grouping ------------------------------------------------------------------------------

    /// <summary>
    /// SUM and COUNT are exact decimals/integers on both engines. AVG is compared as a double with a relative
    /// tolerance of 1e-6, because spec/semantics.md (the result-comparison rules) says an engine's average carries its
    /// own scale: PostgreSQL's AVG(numeric) keeps about sixteen fractional digits, MongoDB's Decimal128 average
    /// thirty-four, so the two JSON numbers differ in length but not in value. That is the specified behavior,
    /// not a library defect.
    /// </summary>
    [RequiresDatabasesFact]
    public async Task GroupByCountry_SumAndCountAreEqualAndAvgIsWithinTheSpecTolerance()
    {
        var pg = (await RowsOk("postgres", Requests.GroupByCountry)).GetProperty("rows").EnumerateArray().ToArray();
        Assert.True(pg.Length >= 10);

        foreach (var provider in new[] { "mongo", "ef" })
        {
            var other = (await RowsOk(provider, Requests.GroupByCountry)).GetProperty("rows").EnumerateArray().ToArray();
            Assert.Equal(pg.Length, other.Length);

            for (var i = 0; i < pg.Length; i++)
            {
                Assert.Equal(Key(pg[i]), Key(other[i]));
                Assert.Equal(pg[i].GetProperty("amount_sum").GetDecimal(), other[i].GetProperty("amount_sum").GetDecimal());
                Assert.Equal(pg[i].GetProperty("quantity_count").GetInt64(), other[i].GetProperty("quantity_count").GetInt64());

                var a = pg[i].GetProperty("amount_avg").GetDouble();
                var b = other[i].GetProperty("amount_avg").GetDouble();
                Assert.True(Math.Abs(a - b) <= 1e-6 * Math.Max(Math.Abs(a), Math.Abs(b)), $"{provider}: avg {a} vs {b} for {Key(pg[i])}");
            }
        }

        static string? Key(JsonElement row) => row.GetProperty("country").GetString();
    }

    /// <summary>Drill-down into one group (groupKeys): the leaf rows of Germany, equal on all three providers.</summary>
    [RequiresDatabasesFact]
    public async Task DrillDownIntoACountry_GivesEqualRowsOnAllProviders()
    {
        var (postgres, mongo) = await IdsOnBoth(Requests.DrillDownGermany);

        Assert.NotEmpty(postgres);
        Assert.Equal(postgres, mongo);
        var page = await RowsOk("ef", Requests.DrillDownGermany);
        Assert.All(page.GetProperty("rows").EnumerateArray(), row => Assert.Equal("Germany", row.GetProperty("country").GetString()));
    }

    // ---- (d) paging --------------------------------------------------------------------------------

    [RequiresDatabasesFact]
    public async Task WindowPastTheEnd_IsEmptyWithTheRightRowCount()
    {
        var pg = await RowsOk("postgres", Requests.PastTheEnd);
        var mongo = await RowsOk("mongo", Requests.PastTheEnd);
        var ef = await RowsOk("ef", Requests.PastTheEnd);

        Assert.Empty(pg.GetProperty("rows").EnumerateArray());
        Assert.Empty(mongo.GetProperty("rows").EnumerateArray());
        Assert.Empty(ef.GetProperty("rows").EnumerateArray());

        var total = pg.GetProperty("lastRow").GetInt64();
        Assert.True(total > 0);
        Assert.Equal(total, mongo.GetProperty("lastRow").GetInt64());
        Assert.Equal(total, ef.GetProperty("lastRow").GetInt64());

        // The same filter's own first page reports the same total: the count does not depend on the window.
        var first = await RowsOk("postgres", Requests.FirstPage(
            """{ "customer": { "filterType": "text", "type": "contains", "filter": "GmbH" } }"""));
        Assert.Equal(total, first.GetProperty("lastRow").GetInt64());
    }

    // ---- (e) set values ----------------------------------------------------------------------------

    [RequiresDatabasesFact]
    public async Task SetValues_ReturnTheSameDistinctCountries()
    {
        var values = new List<string?[]>();
        foreach (var provider in Providers)
        {
            var (status, json) = await api.PostJsonAsync(provider, "values", Requests.CountryValues);
            Assert.Equal(HttpStatusCode.OK, status);
            values.Add([.. json.GetProperty("values").EnumerateArray().Select(v => v.GetString())]);
        }

        Assert.Equal(values[0], values[1]);
        Assert.Equal(values[0], values[2]);
        Assert.Contains("Germany", values[0]);
        Assert.Equal(values[0].Distinct().Count(), values[0].Length);
    }

    // ---- (f) CSV -----------------------------------------------------------------------------------

    [RequiresDatabasesFact]
    public async Task Csv_HasHeaderAndAsManyRowsAsTheFilterMatches_InSortOrder()
    {
        var expected = (await RowsOk("postgres", Requests.FirstPage(
            """{ "status": { "filterType": "text", "type": "equals", "filter": "shipped" } }"""))).GetProperty("lastRow").GetInt64();
        Assert.True(expected > 500, "the export must be larger than one page to prove paging is ignored");

        foreach (var provider in Providers)
        {
            var (status, contentType, disposition, bytes) = await api.PostRawAsync(provider, "export.csv", Requests.StatusExport);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.StartsWith("text/csv", contentType);
            Assert.Contains("orders.csv", disposition);
            Assert.True(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM");

            var records = ParseCsv(Encoding.UTF8.GetString(bytes[3..]));
            Assert.Equal("Id", records[0][0]);
            Assert.Equal(11, records[0].Count);
            Assert.Equal(expected, records.Count - 1);

            var ids = records.Skip(1).Select(r => long.Parse(r[0])).ToArray();
            Assert.Equal(ids.OrderByDescending(id => id), ids);
            Assert.All(records.Skip(1), r => Assert.Equal("shipped", r[5]));
        }
    }

    [RequiresDatabasesFact]
    public async Task Csv_PrefixesAFormulaCustomerWithAnApostrophe()
    {
        foreach (var provider in Providers)
        {
            var (status, _, _, bytes) = await api.PostRawAsync(provider, "export.csv", Requests.FormulaCustomer);
            Assert.Equal(HttpStatusCode.OK, status);

            var records = ParseCsv(Encoding.UTF8.GetString(bytes[3..]));
            Assert.True(records.Count >= 2, $"{provider}: the =SUM(A1) customer was not exported");
            Assert.All(records.Skip(1), r => Assert.Equal("'=SUM(A1)", r[2]));
        }
    }

    // ---- non-ASCII text filter ---------------------------------------------------------------------

    /// <summary>A contains filter is case-insensitive beyond ASCII: "SÖHNE" matches "Müller, Söhne &amp; Co".</summary>
    [RequiresDatabasesFact]
    public async Task TextContains_IsCaseInsensitiveForNonAsciiLetters_OnPostgresAndEf()
    {
        var pg = await RowsOk("postgres", Requests.NonAsciiContains);
        var ef = await RowsOk("ef", Requests.NonAsciiContains);

        Assert.Contains(pg.GetProperty("rows").EnumerateArray(), row => row.GetProperty("customer").GetString() == "Müller, Söhne & Co");
        Assert.Equal(pg.GetProperty("lastRow").GetInt64(), ef.GetProperty("lastRow").GetInt64());
        Assert.Equal(Ids(pg), Ids(ef));
    }

    // ---- (g) compile only --------------------------------------------------------------------------

    [Fact]
    public async Task Query_Postgres_ReturnsParameterizedSqlAndNeverTheLiteralValue()
    {
        var (status, json) = await api.PostJsonAsync("postgres", "query", Requests.NeedleFilter);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("sql", json.GetProperty("kind").GetString());
        var sql = json.GetProperty("text").GetString()!;
        Assert.Contains("$1", sql);
        Assert.DoesNotContain(Requests.UnlikelyValue, sql);
        Assert.Contains(json.GetProperty("parameters").EnumerateArray(), p => p.ValueKind == JsonValueKind.String && p.GetString() == Requests.UnlikelyValue);
    }

    /// <summary>The EF adapter composes the query itself: the text is <c>ToQueryString()</c>, which shows parameter values as
    /// comment lines, so unlike the Dapper route this view is not parameterized text and the parameters list is empty.</summary>
    [Fact]
    public async Task Query_Ef_ReturnsTheEfDialectText()
    {
        var (status, json) = await api.PostJsonAsync("ef", "query", Requests.NeedleFilter);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("sql", json.GetProperty("kind").GetString());
        Assert.Equal("ef-npgsql", json.GetProperty("dialect").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("text").GetString()));
        Assert.Empty(json.GetProperty("parameters").EnumerateArray());
    }

    [Fact]
    public async Task Query_Mongo_ReturnsAPipeline()
    {
        var (status, json) = await api.PostJsonAsync("mongo", "query", Requests.NeedleFilter);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("mongo", json.GetProperty("kind").GetString());
        using var pipeline = JsonDocument.Parse(json.GetProperty("text").GetString()!);
        Assert.Equal(JsonValueKind.Array, pipeline.RootElement.ValueKind);
        Assert.Contains("$match", json.GetProperty("text").GetString());
        Assert.Empty(json.GetProperty("parameters").EnumerateArray());
    }

    // ---- (h) hostile input and errors --------------------------------------------------------------

    [Theory]
    [InlineData("postgres")]
    [InlineData("mongo")]
    [InlineData("ef")]
    public async Task UnknownColumnFilter_Is400WithTheKind(string provider)
    {
        var (status, json) = await api.PostJsonAsync(provider, "rows", Requests.UnknownColumn);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("unknown-field", json.GetProperty("error").GetProperty("kind").GetString());
        Assert.DoesNotContain("   at ", json.ToString());
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("mongo")]
    [InlineData("ef")]
    public async Task PageSizeOver500_Is400WithTheLimitsKind(string provider)
    {
        var (status, json) = await api.PostJsonAsync(provider, "rows", Requests.PageSize(100_000));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("resource-limit-exceeded", json.GetProperty("error").GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("values")]
    [InlineData("export.csv")]
    [InlineData("query")]
    public async Task MalformedJson_Is400MalformedRequest(string route)
    {
        var (status, json) = await api.PostJsonAsync("postgres", route, "{ not json");

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("malformed-request", json.GetProperty("error").GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("values")]
    [InlineData("export.csv")]
    [InlineData("query")]
    public async Task DuplicatePropertyName_Is400MalformedRequest(string route)
    {
        const string body = """{ "startRow": 0, "endRow": 10, "endRow": 20, "filterModel": null }""";

        var (status, json) = await api.PostJsonAsync("postgres", route, body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("malformed-request", json.GetProperty("error").GetProperty("kind").GetString());
        Assert.DoesNotContain("   at ", json.ToString());
    }

    [Fact]
    public async Task BodyOverOneMiB_Is413WithATypedKindAndNoStackText()
    {
        var body = $$"""{ "startRow": 0, "endRow": 10, "pad": "{{new string('x', 1_100_000)}}" }""";

        var (status, json) = await api.PostJsonAsync("postgres", "rows", body);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, status);
        Assert.Equal("request-too-large", json.GetProperty("error").GetProperty("kind").GetString());
        Assert.DoesNotContain("   at ", json.ToString());
        Assert.DoesNotContain("Kestrel", json.ToString());
    }

    [Fact]
    public async Task RowsRequestWithoutAWindow_Is400()
    {
        var (status, json) = await api.PostJsonAsync("mongo", "rows", Requests.StatusExport);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("malformed-request", json.GetProperty("error").GetProperty("kind").GetString());
    }

    [Fact]
    public async Task UnknownProvider_Is404()
    {
        var (status, json) = await api.PostJsonAsync("oracle", "rows", Requests.PageSize(10));

        Assert.Equal(HttpStatusCode.NotFound, status);
        Assert.Equal("unknown-provider", json.GetProperty("error").GetProperty("kind").GetString());
    }

    [RequiresDatabasesFact]
    public async Task HostileFilterValue_IsDataNotSql()
    {
        const string hostile = """{ "customer": { "filterType": "text", "type": "equals", "filter": "x'; DROP TABLE orders; --" } }""";

        foreach (var provider in Providers)
        {
            var page = await RowsOk(provider, Requests.FirstPage(hostile));
            Assert.Empty(page.GetProperty("rows").EnumerateArray());
            Assert.Equal(0, page.GetProperty("lastRow").GetInt64());
        }

        // The table is still there.
        Assert.True((await RowsOk("postgres", Requests.PageSize(5))).GetProperty("lastRow").GetInt64() > 0);
    }

    [Fact]
    public async Task Health_ListsTheProviders()
    {
        using var response = await api.Client.GetAsync("/api/health");
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(new[] { "ef", "ef-sqlite", "mongo", "postgres", "sqlite" }, json.GetProperty("providers").EnumerateArray().Select(p => p.GetString()!).ToArray());
    }

    [Theory]
    [InlineData("http://localhost:5173", true)]
    [InlineData("http://127.0.0.1:5173", true)]
    [InlineData("http://evil.example", false)]
    public async Task Cors_AllowsOnlyTheViteDevOrigin(string origin, bool allowed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/postgres/rows");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type");

        using var response = await api.Client.SendAsync(request);

        Assert.Equal(allowed, response.Headers.TryGetValues("Access-Control-Allow-Origin", out var values) && values.Single() == origin);
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("values")]
    [InlineData("export.csv")]
    public async Task DatabaseDown_Is503BackendUnavailable(string route)
    {
        var (app, client) = await ApiHostFixture.StartWithClosedPostgresAsync();
        await using (app)
        using (client)
        {
            var body = route == "values" ? Requests.CountryValues : route == "export.csv" ? Requests.StatusExport : Requests.PageSize(10);
            using var response = await client.PostAsync($"/api/postgres/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("backend-unavailable", json.GetProperty("error").GetProperty("kind").GetString());
        }
    }

    // ---- minimal RFC 4180 reader for the CSV assertions --------------------------------------------

    private static List<List<string>> ParseCsv(string text)
    {
        var records = new List<List<string>>();
        var record = new List<string>();
        var field = new StringBuilder();
        var quoted = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                else if (c == '"') { quoted = false; }
                else { field.Append(c); }
            }
            else if (c == '"') { quoted = true; }
            else if (c == ',') { record.Add(field.ToString()); field.Clear(); }
            else if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                record.Add(field.ToString()); field.Clear(); records.Add(record); record = []; i++;
            }
            else { field.Append(c); }
        }

        if (field.Length > 0 || record.Count > 0)
        {
            record.Add(field.ToString());
            records.Add(record);
        }

        return records;
    }
}
