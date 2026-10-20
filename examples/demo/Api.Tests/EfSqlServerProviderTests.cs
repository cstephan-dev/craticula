using System.Net;
using System.Text;
using System.Text.Json;
using Demo.Api.Seed;

namespace Demo.Api.Tests;

/// <summary>
/// The <c>ef-sqlserver</c> provider, an optional engine switched on by the same <c>DEMO_SQLSERVER</c> as <c>sqlserver</c>:
/// <c>Craticula.EntityFrameworkCore</c> with the semantics the demo declares for SQL Server (<see cref="Demo.Api.Grid.SqlServerOrdersDbContext"/>).
/// The comparisons with <c>postgres</c> run only when the engine answers; the stopped-engine test needs no container. Acceptance ids
/// (docs/feat-breakdown/2494): AC-1 rows, AC-2 groups and drill-down, AC-3 CSV, AC-6 typed 4xx and 503.
/// </summary>
[Collection("api-sqlserver")]
public sealed class EfSqlServerProviderTests(SqlServerApiFixture api)
{
    private const string Engine = DemoSettings.SqlServerEnvVar;
    private const string Provider = "ef-sqlserver";

    private async Task<JsonElement> RowsOk(string provider, string body)
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, provider, "rows", body);
        Assert.True(HttpStatusCode.OK == status, $"{provider}: {status} {json}");
        return json;
    }

    private async Task AssertRowsEqualPostgres(string body)
    {
        var pg = await RowsOk("postgres", body);
        var ef = await RowsOk(Provider, body);

        Assert.Equal(pg.GetProperty("lastRow").GetInt64(), ef.GetProperty("lastRow").GetInt64());
        var pgRows = pg.GetProperty("rows");
        var efRows = ef.GetProperty("rows");
        Assert.Equal(pgRows.GetArrayLength(), efRows.GetArrayLength());
        for (var i = 0; i < pgRows.GetArrayLength(); i++)
        {
            Assert.True(EfParity.Same(pgRows[i], efRows[i]), $"row {i}: {pgRows[i]} vs {efRows[i]}");
        }
    }

    [RequiresEngineFact(Engine)]
    public Task TextNumberFilterWithSort_EqualsPostgres() => AssertRowsEqualPostgres(Requests.TextNumberSort);

    [RequiresEngineFact(Engine)]
    public Task DateRangeFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DateRange);

    [RequiresEngineFact(Engine)]
    public Task DatetimeRangeFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeRange);

    [RequiresEngineFact(Engine)]
    public Task BooleanFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.Boolean);

    [RequiresEngineFact(Engine)]
    public Task WindowPastTheEnd_EqualsPostgres() => AssertRowsEqualPostgres(Requests.PastTheEnd);

    /// <summary>The proof of the context's UTC value converter: without it EF hands back an unspecified-kind value and the JSON has no <c>Z</c> (bug #2798).</summary>
    [RequiresEngineFact(Engine)]
    public async Task Datetimes_AreSentAsUtcInstantsWithTheTrailingZ()
    {
        var page = await RowsOk(Provider, Requests.PageSize(5));

        Assert.All(page.GetProperty("rows").EnumerateArray(), row =>
            Assert.EndsWith("Z", row.GetProperty("orderedAt").GetString(), StringComparison.Ordinal));
    }

    [RequiresEngineTheory(Engine)]
    [InlineData("country")]
    [InlineData("category")]
    public async Task GroupBy_SumAvgCount_EqualPostgres(string column)
    {
        var body = Requests.GroupBy(column);
        Assert.True((await RowsOk("postgres", body)).GetProperty("rows").GetArrayLength() >= 5);

        await AssertRowsEqualPostgres(body);
    }

    [RequiresEngineFact(Engine)]
    public async Task GroupBy_AverageOfAWholeNumberColumn_IsNotTruncatedLikeTheDapperRoute()
    {
        // EF averages a BIGINT column as a float, so unlike the Dapper SQL Server route no truncation rule is needed.
        await AssertRowsEqualPostgres(Requests.GroupByWithQuantityAvg("country"));
    }

    [RequiresEngineFact(Engine)]
    public async Task DrillDownIntoACountry_EqualsPostgres()
    {
        await AssertRowsEqualPostgres(Requests.DrillDownGermany);

        var page = await RowsOk(Provider, Requests.DrillDownGermany);
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
    }

    /// <summary>The customer column has the server's case-insensitive collation; the binary-collated grouping columns are covered by <c>TextFilterOnAnyColumn_…</c>.</summary>
    [RequiresEngineFact(Engine)]
    public async Task ContainsFilter_FoldsCaseLikePostgres_OnTheCaseInsensitiveCustomerColumn()
    {
        await AssertRowsEqualPostgres(Requests.NonAsciiContains);
        await AssertRowsEqualPostgres(Requests.NonAsciiContainsLower);
    }

    [RequiresEngineTheory(Engine)]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetValues_EqualPostgres(bool filtered)
    {
        var body = filtered ? Requests.FilteredCountryValues : Requests.CountryValues;
        var pg = await ApiHostFixture.PostJsonAsync(api.Client, "postgres", "values", body);
        var ef = await ApiHostFixture.PostJsonAsync(api.Client, Provider, "values", body);

        Assert.Equal(HttpStatusCode.OK, ef.Status);
        string[] Canon(JsonElement json) => [.. json.GetProperty("values").EnumerateArray().Select(v => v.GetRawText()).Order(StringComparer.Ordinal)];
        Assert.Equal(Canon(pg.Body), Canon(ef.Body));
    }

    [RequiresEngineFact(Engine)]
    public async Task Csv_StatusExport_EqualsPostgresCellByCell()
    {
        var pg = await ApiHostFixture.PostRawAsync(api.Client, "postgres", "export.csv", Requests.StatusExport);
        var ef = await ApiHostFixture.PostRawAsync(api.Client, Provider, "export.csv", Requests.StatusExport);

        Assert.Equal(HttpStatusCode.OK, ef.Status);
        Assert.True(EfParity.CsvSame(EfParity.ParseCsv(pg.Bytes), EfParity.ParseCsv(ef.Bytes)));
    }

    /// <summary>The whole table through <c>EfCoreExportStream</c> (more than one keyset chunk): the stream selects each key once on SQL Server.</summary>
    [RequiresEngineFact(Engine)]
    public async Task Csv_WholeTableExport_EqualsPostgresOverEveryKeysetChunk()
    {
        var pg = await ApiHostFixture.PostRawAsync(api.Client, "postgres", "export.csv", Requests.FullExport);
        var ef = await ApiHostFixture.PostRawAsync(api.Client, Provider, "export.csv", Requests.FullExport);

        Assert.Equal(HttpStatusCode.OK, ef.Status);
        var rows = EfParity.ParseCsv(ef.Bytes);
        Assert.True(rows.Count > 5000, "more than one chunk");
        Assert.True(EfParity.CsvSame(EfParity.ParseCsv(pg.Bytes), rows));
    }

    [RequiresEngineFact(Engine)]
    public async Task TheQueryView_IsSqlWithTheEfSqlServerDialect()
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, Provider, "query", Requests.TextNumberSort);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("sql", json.GetProperty("kind").GetString());
        Assert.Equal("ef-sqlserver", json.GetProperty("dialect").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("text").GetString()));
    }

    [RequiresEngineFact(Engine)]
    public async Task UnknownColumnFilter_Is400UnknownField()
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, Provider, "rows", Requests.UnknownColumn);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("unknown-field", json.GetProperty("error").GetProperty("kind").GetString());
    }

    /// <summary>A host whose SQL Server points at a port nothing listens on: it needs no container.</summary>
    [Theory]
    [InlineData("rows")]
    [InlineData("values")]
    [InlineData("export.csv")]
    public async Task AnEngineThatIsNotListening_Is503BackendUnavailable(string route)
    {
        var (app, client) = await ApiHostFixture.StartAsync(null, builder => builder.Configuration[DemoSettings.SqlServerEnvVar] =
            $"Server=127.0.0.1,{ApiHostFixture.FreePort()};User Id=sa;Password=unused;Database=demo;TrustServerCertificate=true;Connect Timeout=1");
        await using (app)
        using (client)
        {
            var body = route == "values" ? Requests.CountryValues : route == "export.csv" ? Requests.StatusExport : Requests.PageSize(10);
            using var response = await client.PostAsync($"/api/{Provider}/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("backend-unavailable", json.GetProperty("error").GetProperty("kind").GetString());
        }
    }

    // ---- datetime parameters and the keyset walk (measured: EF's SQL Server mapping is exact here) -------

    [RequiresEngineFact(Engine)]
    public Task DatetimeRangeWithBoundsAtNoon_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeRangeAtNoon);

    [RequiresEngineFact(Engine)]
    public Task DatetimeGreaterThanNoon_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeGreaterThanNoon);

    [RequiresEngineTheory(Engine)]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task Csv_WholeTableSortedByADatetime_HasEveryRowOnce_AndEqualsPostgres(string direction)
    {
        var pg = await ApiHostFixture.PostRawAsync(api.Client, "postgres", "export.csv", Requests.FullExportByOrderedAt(direction));
        var ef = await ApiHostFixture.PostRawAsync(api.Client, Provider, "export.csv", Requests.FullExportByOrderedAt(direction));

        Assert.Equal(HttpStatusCode.OK, ef.Status);
        var rows = EfParity.ParseCsv(ef.Bytes);
        Assert.Equal(EfParity.ParseCsv(pg.Bytes).Count, rows.Count);
        Assert.True(EfParity.CsvSame(EfParity.ParseCsv(pg.Bytes), rows));
    }

    [RequiresEngineTheory(Engine)]
    [InlineData("country", "contains", "germ")]
    [InlineData("country", "startsWith", "GERMANY")]
    [InlineData("status", "contains", "SHIP")]
    public async Task TextFilterOnAnyColumn_FoldsCaseLikePostgres_OnTheBinaryCollatedColumnsToo(string column, string type, string value) =>
        await AssertRowsEqualPostgres(Requests.TextFilter(column, type, value));
}
