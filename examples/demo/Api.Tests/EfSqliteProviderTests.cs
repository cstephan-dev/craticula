using System.Net;
using System.Text;
using System.Text.Json;
using Demo.Api.Seed;

namespace Demo.Api.Tests;

/// <summary>
/// The <c>ef-sqlite</c> provider: <c>Craticula.EntityFrameworkCore</c> over the same SQLite file as <c>sqlite</c>, with the adapter's own SQLite
/// semantics preset. It needs only PostgreSQL (the baseline) to be up, like the Dapper SQLite tests. Acceptance ids
/// (docs/feat-breakdown/2494): AC-1 rows, AC-2 groups and drill-down, AC-3 CSV, AC-6 typed 4xx.
/// </summary>
[Collection("api")]
public sealed class EfSqliteProviderTests(ApiHostFixture api)
{
    private const string Provider = "ef-sqlite";

    private async Task<JsonElement> RowsOk(string provider, string body)
    {
        var (status, json) = await api.PostJsonAsync(provider, "rows", body);
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

    [RequiresDatabasesFact]
    public Task TextNumberFilterWithSort_EqualsPostgres() => AssertRowsEqualPostgres(Requests.TextNumberSort);

    [RequiresDatabasesFact]
    public Task DateRangeFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DateRange);

    [RequiresDatabasesFact]
    public Task DatetimeRangeFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeRange);

    [RequiresDatabasesFact]
    public Task BooleanFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.Boolean);

    [RequiresDatabasesFact]
    public Task WindowPastTheEnd_EqualsPostgres() => AssertRowsEqualPostgres(Requests.PastTheEnd);

    [RequiresDatabasesFact]
    public async Task Datetimes_AreSentAsUtcInstantsWithTheTrailingZ()
    {
        var page = await RowsOk(Provider, Requests.PageSize(5));

        Assert.All(page.GetProperty("rows").EnumerateArray(), row =>
            Assert.EndsWith("Z", row.GetProperty("orderedAt").GetString(), StringComparison.Ordinal));
    }

    [RequiresDatabasesTheory]
    [InlineData("country")]
    [InlineData("category")]
    public async Task GroupBy_SumAvgCount_EqualPostgres(string column)
    {
        var body = Requests.GroupBy(column);
        Assert.True((await RowsOk("postgres", body)).GetProperty("rows").GetArrayLength() >= 5);

        await AssertRowsEqualPostgres(body);
    }

    [RequiresDatabasesFact]
    public async Task DrillDownIntoACountry_EqualsPostgres()
    {
        await AssertRowsEqualPostgres(Requests.DrillDownGermany);

        var page = await RowsOk(Provider, Requests.DrillDownGermany);
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
        Assert.All(page.GetProperty("rows").EnumerateArray(), row => Assert.Equal("Germany", row.GetProperty("country").GetString()));
    }

    [RequiresDatabasesTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetValues_EqualPostgres(bool filtered)
    {
        var body = filtered ? Requests.FilteredCountryValues : Requests.CountryValues;
        var pg = await api.PostJsonAsync("postgres", "values", body);
        var ef = await api.PostJsonAsync(Provider, "values", body);

        Assert.Equal(HttpStatusCode.OK, ef.Status);
        string[] Canon(JsonElement json) => [.. json.GetProperty("values").EnumerateArray().Select(v => v.GetRawText()).Order(StringComparer.Ordinal)];
        Assert.Equal(Canon(pg.Body), Canon(ef.Body));
    }

    [RequiresDatabasesFact]
    public async Task Csv_StatusExport_EqualsPostgresCellByCell()
    {
        var pg = await api.PostRawAsync("postgres", "export.csv", Requests.StatusExport);
        var ef = await api.PostRawAsync(Provider, "export.csv", Requests.StatusExport);

        Assert.Equal(HttpStatusCode.OK, ef.Status);
        Assert.True(EfParity.CsvSame(EfParity.ParseCsv(pg.Bytes), EfParity.ParseCsv(ef.Bytes)));
    }

    [RequiresDatabasesFact]
    public async Task Csv_WholeTableExport_EqualsPostgresOverEveryKeysetChunk()
    {
        var pg = await api.PostRawAsync("postgres", "export.csv", Requests.FullExport);
        var ef = await api.PostRawAsync(Provider, "export.csv", Requests.FullExport);

        Assert.Equal(HttpStatusCode.OK, ef.Status);
        var rows = EfParity.ParseCsv(ef.Bytes);
        Assert.True(rows.Count > 5000, "more than one chunk");
        Assert.True(EfParity.CsvSame(EfParity.ParseCsv(pg.Bytes), rows));
    }

    [RequiresDatabasesFact]
    public async Task TheQueryView_IsSqlWithTheEfSqliteDialect()
    {
        var (status, json) = await api.PostJsonAsync(Provider, "query", Requests.TextNumberSort);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("sql", json.GetProperty("kind").GetString());
        Assert.Equal("ef-sqlite", json.GetProperty("dialect").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("text").GetString()));
    }

    [RequiresDatabasesFact]
    public async Task UnknownColumnFilter_Is400UnknownField()
    {
        var (status, json) = await api.PostJsonAsync(Provider, "rows", Requests.UnknownColumn);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("unknown-field", json.GetProperty("error").GetProperty("kind").GetString());
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("values")]
    [InlineData("export.csv")]
    public async Task AMissingFile_Is503BackendUnavailable_AndIsNotCreated(string route)
    {
        var missing = Path.Combine(Path.GetTempPath(), $"craticula-demo-missing-{Guid.NewGuid():N}", "none.sqlite");
        var (app, client) = await ApiHostFixture.StartAsync(null, builder => builder.Configuration[DemoSettings.SqliteEnvVar] = missing);
        await using (app)
        using (client)
        {
            var body = route == "values" ? Requests.CountryValues : route == "export.csv" ? Requests.StatusExport : Requests.PageSize(10);
            using var response = await client.PostAsync($"/api/{Provider}/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("backend-unavailable", json.GetProperty("error").GetProperty("kind").GetString());
            Assert.False(File.Exists(missing), "the API must never create the database file");
        }
    }

    // ---- datetime parameters and the keyset walk (the context maps datetimes to the stored text form) -------

    [RequiresDatabasesFact]
    public Task DatetimeRangeWithBoundsAtNoon_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeRangeAtNoon);

    [RequiresDatabasesFact]
    public Task DatetimeGreaterThanNoon_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeGreaterThanNoon);

    [RequiresDatabasesTheory]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task Csv_WholeTableSortedByADatetime_HasEveryRowOnce_AndEqualsPostgres(string direction)
    {
        var pg = await api.PostRawAsync("postgres", "export.csv", Requests.FullExportByOrderedAt(direction));
        var ef = await api.PostRawAsync(Provider, "export.csv", Requests.FullExportByOrderedAt(direction));

        Assert.Equal(HttpStatusCode.OK, ef.Status);
        var rows = EfParity.ParseCsv(ef.Bytes);
        Assert.Equal(EfParity.ParseCsv(pg.Bytes).Count, rows.Count);
        Assert.True(EfParity.CsvSame(EfParity.ParseCsv(pg.Bytes), rows));
    }

    [RequiresDatabasesTheory]
    [InlineData("country", "contains", "germ")]
    [InlineData("country", "startsWith", "GERMANY")]
    [InlineData("status", "contains", "SHIP")]
    public async Task TextFilterOnAnyColumn_FoldsCaseLikePostgres(string column, string type, string value) =>
        await AssertRowsEqualPostgres(Requests.TextFilter(column, type, value));
}
