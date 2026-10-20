using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Demo.Api.Seed;

namespace Demo.Api.Tests;

/// <summary>
/// The <c>elasticsearch</c> provider, an optional engine: the OpenSearch client and compiler on Elasticsearch 8, with the point-in-time calls
/// translated at the HTTP level (<see cref="Demo.Api.Grid.ElasticsearchPitConnection"/>; the translation itself is proven offline in
/// <see cref="ElasticsearchPitTranslationTests"/>). The comparisons with <c>postgres</c> run only when <c>DEMO_ELASTICSEARCH</c> is set and the
/// engine answers; the stopped-engine and stub-server tests need no cluster. The documented differences are the OpenSearch ones, measured in
/// #2556 and #2557 on Elasticsearch 8.17.4: the row count above 10,000 is unknown (<c>lastRow</c> null), the null group sits at the other end,
/// the distinct values leave out null, and two grouped shapes are unsupported. Everything else is exact, including the deep pages past 10,000
/// rows and the CSV export, which are the paths that need the translation. Acceptance ids (docs/feat-breakdown/2494): AC-1 rows, AC-2 groups,
/// AC-3 CSV, AC-6 typed 4xx and 503.
/// </summary>
[Collection("api-elasticsearch")]
public sealed class ElasticsearchProviderTests(ElasticsearchApiFixture api)
{
    private const string Engine = DemoSettings.ElasticsearchEnvVar;
    private const string Provider = "elasticsearch";

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task<JsonElement> RowsOk(string provider, string body)
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, provider, "rows", body);
        Assert.True(HttpStatusCode.OK == status, $"{provider}: {status} {json}");
        return json;
    }

    private static string SortedBy(string colId, string direction, int start = 0, int end = 100) => $$"""
        {
          "startRow": {{start}}, "endRow": {{end}},
          "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": null,
          "sortModel": [ { "colId": "{{colId}}", "sort": "{{direction}}" } ]
        }
        """;

    private static bool Same(JsonElement baseline, JsonElement other, string key = "")
    {
        if (baseline.ValueKind == JsonValueKind.Object && other.ValueKind == JsonValueKind.Object)
        {
            var left = baseline.EnumerateObject().ToArray();
            return left.Length == other.EnumerateObject().Count()
                && left.All(property => other.TryGetProperty(property.Name, out var value) && Same(property.Value, value, property.Name));
        }

        if (baseline.ValueKind == JsonValueKind.Array && other.ValueKind == JsonValueKind.Array)
        {
            return baseline.GetArrayLength() == other.GetArrayLength()
                && baseline.EnumerateArray().Zip(other.EnumerateArray()).All(pair => Same(pair.First, pair.Second, key));
        }

        if (baseline.ValueKind == JsonValueKind.Number && other.ValueKind == JsonValueKind.Number)
        {
            var x = baseline.GetDouble();
            var y = other.GetDouble();
            return key.EndsWith("_avg", StringComparison.Ordinal)
                ? Math.Abs(x - y) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)))
                : baseline.GetDecimal() == other.GetDecimal();
        }

        return baseline.ValueKind == other.ValueKind && baseline.GetRawText() == other.GetRawText();
    }

    /// <summary>The rows equal PostgreSQL's one by one; <c>lastRow</c> too, except that a count above 10,000 is unknown on the search engines (PERF-3).</summary>
    private async Task AssertRowsEqualPostgres(string body)
    {
        var pg = await RowsOk("postgres", body);
        var es = await RowsOk(Provider, body);

        var expected = pg.GetProperty("lastRow");
        var actual = es.GetProperty("lastRow");
        if (!(expected.ValueKind == JsonValueKind.Number && expected.GetInt64() > 10_000 && actual.ValueKind == JsonValueKind.Null))
        {
            Assert.Equal(expected.GetRawText(), actual.GetRawText());
        }

        var pgRows = pg.GetProperty("rows");
        var esRows = es.GetProperty("rows");
        Assert.Equal(pgRows.GetArrayLength(), esRows.GetArrayLength());
        for (var i = 0; i < pgRows.GetArrayLength(); i++)
        {
            Assert.True(Same(pgRows[i], esRows[i]), $"row {i}: {pgRows[i]} vs {esRows[i]}");
        }
    }

    private static (List<JsonElement> Nulls, List<JsonElement> Others) SplitNullGroup(JsonElement rows)
    {
        var all = rows.EnumerateArray().ToList();
        var isNull = (JsonElement row) => row.EnumerateObject().First().Value.ValueKind == JsonValueKind.Null;
        return ([.. all.Where(isNull)], [.. all.Where(row => !isNull(row))]);
    }

    private static List<List<string>> ParseCsv(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes[3..]).Replace("\r\n", "\n");
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(',').ToList())];
    }

    private static async Task<JsonElement> ErrorBody(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error");

    // ---- rows: filters, sort, paging ----------------------------------------------------------------

    [RequiresEngineFact(Engine)]
    public Task TextNumberFilterWithSort_EqualsPostgres() => AssertRowsEqualPostgres(Requests.TextNumberSort);

    [RequiresEngineFact(Engine)]
    public Task DateRangeFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DateRange);

    [RequiresEngineFact(Engine)]
    public Task DatetimeRangeFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeRange);

    [RequiresEngineFact(Engine)]
    public Task DatetimeRangeWithBoundsAtNoon_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeRangeAtNoon);

    [RequiresEngineFact(Engine)]
    public Task BooleanFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.Boolean);

    [RequiresEngineTheory(Engine)]
    [InlineData("customer", "asc")]
    [InlineData("amount", "desc")]
    [InlineData("orderedAt", "desc")]
    public Task Sort_FirstPage_EqualsPostgres(string colId, string direction) => AssertRowsEqualPostgres(SortedBy(colId, direction));

    /// <summary>A window that ends at row 10,000 is the first one the client serves with a point in time (the window cap is 10,000): the call that
    /// Elasticsearch answered with HTTP 400 before the translation (#2556).</summary>
    [RequiresEngineTheory(Engine)]
    [InlineData("customer", "asc")]
    [InlineData("amount", "desc")]
    public Task Sort_APageNearTheEnd_EqualsPostgres(string colId, string direction) => AssertRowsEqualPostgres(SortedBy(colId, direction, 9900, 10000));

    [RequiresEngineFact(Engine)]
    public async Task DeepPaging_PastTenThousandRows_EqualsPostgres()
    {
        await AssertRowsEqualPostgres(SortedBy("id", "asc", 10_000, 10_100));
        await AssertRowsEqualPostgres(SortedBy("id", "asc", 10_100, 10_200));
        await AssertRowsEqualPostgres(SortedBy("amount", "desc", 15_000, 15_100));
        await AssertRowsEqualPostgres(SortedBy("id", "asc", 19_950, 20_050));
    }

    [RequiresEngineFact(Engine)]
    public Task WindowPastTheEnd_EqualsPostgres() => AssertRowsEqualPostgres(Requests.PastTheEnd);

    [RequiresEngineFact(Engine)]
    public async Task RowCount_IsExactBelowTenThousand_AndUnknownAbove()
    {
        var filtered = await RowsOk(Provider, Requests.TextNumberSort);
        Assert.Equal(
            (await RowsOk("postgres", Requests.TextNumberSort)).GetProperty("lastRow").GetInt64(),
            filtered.GetProperty("lastRow").GetInt64());

        var all = await RowsOk(Provider, Requests.PageSize(50));
        Assert.Equal(JsonValueKind.Null, all.GetProperty("lastRow").ValueKind);
        Assert.Equal(50, all.GetProperty("rows").GetArrayLength());
    }

    [RequiresEngineFact(Engine)]
    public async Task Datetimes_AreSentAsUtcInstantsWithTheTrailingZ()
    {
        var page = await RowsOk(Provider, Requests.PageSize(5));

        Assert.All(page.GetProperty("rows").EnumerateArray(), row =>
            Assert.EndsWith("Z", row.GetProperty("orderedAt").GetString(), StringComparison.Ordinal));
    }

    // ---- grouping, drill-down and the distinct values ------------------------------------------------

    [RequiresEngineTheory(Engine)]
    [InlineData("country")]
    [InlineData("category")]
    public async Task GroupBy_SumAvgCount_EqualPostgres_ExceptWhereTheNullGroupSits(string column)
    {
        var body = Requests.GroupBy(column);
        var pg = await RowsOk("postgres", body);
        var es = await RowsOk(Provider, body);

        Assert.Equal(pg.GetProperty("lastRow").GetRawText(), es.GetProperty("lastRow").GetRawText());
        var (pgNull, pgRest) = SplitNullGroup(pg.GetProperty("rows"));
        var (esNull, esRest) = SplitNullGroup(es.GetProperty("rows"));
        Assert.True(pgRest.Count >= 5);
        Assert.Equal(pgNull.Count, esNull.Count);
        Assert.Equal(pgRest.Count, esRest.Count);
        for (var i = 0; i < pgRest.Count; i++)
        {
            Assert.True(Same(pgRest[i], esRest[i]), $"group {i}: {pgRest[i]} vs {esRest[i]}");
        }
    }

    [RequiresEngineFact(Engine)]
    public async Task DrillDownIntoACountry_EqualsPostgres() => await AssertRowsEqualPostgres(Requests.DrillDownGermany);

    [RequiresEngineTheory(Engine)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetValues_EqualPostgres_ExceptThatNullIsLeftOut(bool filtered)
    {
        var body = filtered ? Requests.FilteredCountryValues : Requests.CountryValues;
        var pg = await ApiHostFixture.PostJsonAsync(api.Client, "postgres", "values", body);
        var es = await ApiHostFixture.PostJsonAsync(api.Client, Provider, "values", body);

        Assert.Equal(HttpStatusCode.OK, es.Status);
        string[] Canon(JsonElement json) => [.. json.GetProperty("values").EnumerateArray().Where(v => v.ValueKind != JsonValueKind.Null).Select(v => v.GetRawText()).Order(StringComparer.Ordinal)];
        Assert.Equal(Canon(pg.Body), Canon(es.Body));
    }

    /// <summary>The two grouped shapes the search engines cannot answer like PostgreSQL stay a typed 400, and the message names this engine.</summary>
    [RequiresEngineFact(Engine)]
    public async Task AGroupOrderedByAnAggregate_IsAnUnsupportedShape_NamingElasticsearch()
    {
        const string body = """
            {
              "startRow": 0, "endRow": 100,
              "rowGroupCols": [ { "id": "country", "displayName": "Country", "field": "country" } ],
              "valueCols": [
                { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "sum" },
                { "id": "quantity", "displayName": "Quantity", "field": "quantity", "aggFunc": "count" }
              ],
              "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": null,
              "sortModel": [ { "colId": "amount", "sort": "desc" } ]
            }
            """;
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, Provider, "rows", body);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("unsupported-shape", json.GetProperty("error").GetProperty("kind").GetString());
        Assert.Contains("Elasticsearch cannot order groups by an aggregate", json.GetProperty("error").GetProperty("message").GetString());
    }

    // ---- CSV: the export is a point in time walked by search_after --------------------------------------

    [RequiresEngineFact(Engine)]
    public async Task Csv_StatusExport_EqualsPostgresCellByCell()
    {
        var pg = await ApiHostFixture.PostRawAsync(api.Client, "postgres", "export.csv", Requests.StatusExport);
        var es = await ApiHostFixture.PostRawAsync(api.Client, Provider, "export.csv", Requests.StatusExport);

        Assert.Equal(HttpStatusCode.OK, es.Status);
        Assert.True(EfParity.CsvSame(EfParity.ParseCsv(pg.Bytes), EfParity.ParseCsv(es.Bytes)));
    }

    [RequiresEngineFact(Engine)]
    public async Task Csv_WholeTableExport_HasEveryRowOnceInKeyOrder()
    {
        var pg = await ApiHostFixture.PostRawAsync(api.Client, "postgres", "export.csv", Requests.FullExport);
        var es = await ApiHostFixture.PostRawAsync(api.Client, Provider, "export.csv", Requests.FullExport);

        Assert.Equal(HttpStatusCode.OK, es.Status);
        var rows = ParseCsv(es.Bytes);
        Assert.True(rows.Count > 5000, "more than one chunk");
        Assert.True(EfParity.CsvSame(EfParity.ParseCsv(pg.Bytes), EfParity.ParseCsv(es.Bytes)));
    }

    /// <summary>The close of the point-in-time is best-effort and its failure is swallowed, so a broken translation of it would be invisible:
    /// the cluster's count of open search contexts must be back where it was after an export and after a deep page.</summary>
    [RequiresEngineFact(Engine)]
    public async Task AnExportAndADeepPage_LeaveNoPointInTimeOpen()
    {
        var url = Environment.GetEnvironmentVariable(Engine)!.TrimEnd('/');
        using var http = new HttpClient();
        async Task<long> OpenContextsAsync()
        {
            using var stats = JsonDocument.Parse(await http.GetStringAsync($"{url}/_nodes/stats/indices/search"));
            return stats.RootElement.GetProperty("nodes").EnumerateObject()
                .Sum(node => node.Value.GetProperty("indices").GetProperty("search").GetProperty("open_contexts").GetInt64());
        }

        var before = await OpenContextsAsync();
        var export = await ApiHostFixture.PostRawAsync(api.Client, Provider, "export.csv", Requests.FullExport);
        var deep = await RowsOk(Provider, SortedBy("id", "asc", 15_000, 15_100));

        Assert.Equal(HttpStatusCode.OK, export.Status);
        Assert.Equal(100, deep.GetProperty("rows").GetArrayLength());
        // The deep page's cursor lives in the client's cache and its point-in-time is closed when it is superseded or lapses (5 minutes),
        // so the export, which closes its own at the end, is what must leave nothing behind.
        var afterExport = await ApiHostFixture.PostRawAsync(api.Client, Provider, "export.csv", Requests.StatusExport);
        Assert.Equal(HttpStatusCode.OK, afterExport.Status);
        Assert.True(await OpenContextsAsync() <= before + 1, "an export left a point-in-time open");
    }

    [RequiresEngineFact(Engine)]
    public async Task Csv_PrefixesAFormulaCustomerWithAnApostrophe()
    {
        var es = await ApiHostFixture.PostRawAsync(api.Client, Provider, "export.csv", Requests.FormulaCustomer);

        Assert.Equal(HttpStatusCode.OK, es.Status);
        Assert.Contains("'=SUM(A1)", Encoding.UTF8.GetString(es.Bytes));
    }

    // ---- the query view and the typed 4xx -----------------------------------------------------------

    [RequiresEngineFact(Engine)]
    public async Task TheQueryView_IsJsonWithTheElasticsearchDialect()
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, Provider, "query", Requests.TextNumberSort);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("json", json.GetProperty("kind").GetString());
        Assert.Equal("elasticsearch", json.GetProperty("dialect").GetString());
        using var body = JsonDocument.Parse(json.GetProperty("text").GetString()!);
        Assert.Equal(JsonValueKind.Object, body.RootElement.ValueKind);
    }

    [RequiresEngineFact(Engine)]
    public async Task UnknownColumnFilter_Is400UnknownField()
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, Provider, "rows", Requests.UnknownColumn);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("unknown-field", json.GetProperty("error").GetProperty("kind").GetString());
    }

    // ---- no cluster needed ----------------------------------------------------------------------------

    [Theory]
    [InlineData("rows")]
    [InlineData("values")]
    [InlineData("export.csv")]
    public async Task AnEngineThatIsNotListening_Is503BackendUnavailable(string route)
    {
        var (app, client) = await ApiHostFixture.StartAsync(null, builder =>
            builder.Configuration[DemoSettings.ElasticsearchEnvVar] = $"http://127.0.0.1:{ApiHostFixture.FreePort()}");
        await using (app)
        using (client)
        {
            var body = route == "values" ? Requests.CountryValues : route == "export.csv" ? Requests.StatusExport : Requests.PageSize(10);
            using var response = await client.PostAsync($"/api/{Provider}/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
            var error = await ErrorBody(response);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("backend-unavailable", error.GetProperty("kind").GetString());
        }
    }

    /// <summary>An overloaded cluster answers HTTP 503, on the point-in-time create of the export too (after the translation): a 503 on every route.</summary>
    [Theory]
    [InlineData("rows")]
    [InlineData("values")]
    [InlineData("export.csv")]
    public async Task AClusterThatAnswersHttp503_Is503BackendUnavailable_OnEveryRoute(string route)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var stop = new CancellationTokenSource();
        var serving = Task.Run(async () =>
        {
            const string answer = "HTTP/1.1 503 Service Unavailable\r\nContent-Type: application/json\r\nContent-Length: 2\r\nConnection: close\r\n\r\n{}";
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    using var tcp = await listener.AcceptTcpClientAsync(stop.Token);
                    var stream = tcp.GetStream();
                    var buffer = new byte[8192];
                    var seen = new StringBuilder();
                    while (!seen.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                    {
                        var read = await stream.ReadAsync(buffer, stop.Token);
                        if (read == 0)
                        {
                            break;
                        }

                        seen.Append(Encoding.ASCII.GetString(buffer, 0, read));
                    }

                    await stream.WriteAsync(Encoding.ASCII.GetBytes(answer), stop.Token);
                }
                catch (Exception ex) when (ex is OperationCanceledException or IOException or SocketException or ObjectDisposedException)
                {
                    // The test ended, or the client hung up: nothing to serve.
                }
            }
        });
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var (app, client) = await ApiHostFixture.StartAsync(null, builder =>
                builder.Configuration[DemoSettings.ElasticsearchEnvVar] = $"http://127.0.0.1:{port}");
            await using (app)
            using (client)
            {
                var body = route == "values" ? Requests.CountryValues : route == "export.csv" ? Requests.StatusExport : Requests.PageSize(10);
                using var response = await client.PostAsync($"/api/{Provider}/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
                var error = await ErrorBody(response);

                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
                Assert.Equal("backend-unavailable", error.GetProperty("kind").GetString());
            }
        }
        finally
        {
            await stop.CancelAsync();
            listener.Stop();
            await serving;
        }
    }

    /// <summary>The unsupported shapes are refused before any call to the engine, so this needs no cluster: expanding the null group answers a typed 400 that names Elasticsearch.</summary>
    [Fact]
    public async Task ExpandingTheNullGroup_IsAnUnsupportedShape_NamingElasticsearch_WithoutACluster()
    {
        var (app, client) = await ApiHostFixture.StartAsync(null, builder =>
            builder.Configuration[DemoSettings.ElasticsearchEnvVar] = $"http://127.0.0.1:{ApiHostFixture.FreePort()}");
        await using (app)
        using (client)
        {
            const string body = """
                {
                  "startRow": 0, "endRow": 50,
                  "rowGroupCols": [ { "id": "country", "displayName": "Country", "field": "country" }, { "id": "category", "displayName": "Category", "field": "category" } ],
                  "valueCols": [ { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "sum" } ],
                  "pivotCols": [], "pivotMode": false, "groupKeys": [ { "type": "null" } ],
                  "filterModel": null, "sortModel": []
                }
                """;
            using var response = await client.PostAsync($"/api/{Provider}/rows", new StringContent(body, Encoding.UTF8, "application/json"));
            var error = await ErrorBody(response);

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Equal("unsupported-shape", error.GetProperty("kind").GetString());
            Assert.StartsWith("Elasticsearch cannot expand the group whose 'country' value is empty", error.GetProperty("message").GetString());
        }
    }

    [Fact]
    public void TheElasticsearchSeedTarget_IsNotADefault_AndNamesItsSettingAndProfile()
    {
        var target = SeedTargets.All.Single(t => t.Name == Provider);

        Assert.False(target.Default);
        Assert.Contains("DEMO_ELASTICSEARCH", target.Hint);
        Assert.Contains("--profile elasticsearch", target.Hint);
    }
}
