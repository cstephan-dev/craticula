using System.Net;
using System.Text;
using System.Text.Json;
using Demo.Api.Errors;
using Demo.Api.Seed;
using OracleMySqlConnection = MySql.Data.MySqlClient.MySqlConnection;
using OracleMySqlConnectionStringBuilder = MySql.Data.MySqlClient.MySqlConnectionStringBuilder;
using OracleMySqlException = MySql.Data.MySqlClient.MySqlException;

namespace Demo.Api.Tests;

/// <summary>
/// The <c>ef-mysql</c> provider, an optional engine switched on by the same <c>DEMO_MYSQL</c> as <c>mysql</c>:
/// <c>Craticula.EntityFrameworkCore</c> over Oracle's <c>MySql.EntityFrameworkCore</c> with the semantics the demo declares for MySQL
/// (<see cref="Demo.Api.Grid.MySqlOrdersDbContext"/>). The comparisons with <c>postgres</c> and with the Dapper <c>mysql</c> route run
/// only when the engine answers; the stopped-engine test needs no container. Measured on MySQL 8.4.11: the average of the BIGINT
/// <c>quantity</c> has four decimals on MySQL (the Dapper route and this one), so that average is compared with <c>mysql</c>, not with
/// <c>postgres</c>; the substring filters fold case on the utf8mb4_bin columns here (like PostgreSQL) and do not on <c>mysql</c>.
/// </summary>
[Collection("api-mysql")]
public sealed class EfMySqlProviderTests(MySqlApiFixture api)
{
    private const string Engine = DemoSettings.MySqlEnvVar;
    private const string Provider = "ef-mysql";

    private async Task<JsonElement> RowsOk(string provider, string body)
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, provider, "rows", body);
        Assert.True(HttpStatusCode.OK == status, $"{provider}: {status} {json}");
        return json;
    }

    private async Task AssertRowsEqual(string baseline, string body)
    {
        var expected = await RowsOk(baseline, body);
        var actual = await RowsOk(Provider, body);

        Assert.Equal(expected.GetProperty("lastRow").GetInt64(), actual.GetProperty("lastRow").GetInt64());
        var expectedRows = expected.GetProperty("rows");
        var actualRows = actual.GetProperty("rows");
        Assert.Equal(expectedRows.GetArrayLength(), actualRows.GetArrayLength());
        for (var i = 0; i < expectedRows.GetArrayLength(); i++)
        {
            Assert.True(EfParity.Same(expectedRows[i], actualRows[i]), $"row {i}: {expectedRows[i]} vs {actualRows[i]}");
        }
    }

    private Task AssertRowsEqualPostgres(string body) => AssertRowsEqual("postgres", body);

    private async Task<int> RowCount(string provider, string body) =>
        (await RowsOk(provider, body)).GetProperty("rows").GetArrayLength();

    // ---- rows: filters, sort, paging -----------------------------------------------------------------

    [RequiresEngineFact(Engine)]
    public Task TextNumberFilterWithSort_EqualsPostgres() => AssertRowsEqualPostgres(Requests.TextNumberSort);

    /// <summary>The first workaround's proof: a DATE comes back as DateTime from the driver and the converter makes it a DateOnly. Without it every page that reads <c>shippedOn</c> is a 500.</summary>
    [RequiresEngineFact(Engine)]
    public async Task ADateColumn_IsReadAsADate_ThroughTheConverter()
    {
        var page = await RowsOk(Provider, Requests.PageSize(200));

        var dates = page.GetProperty("rows").EnumerateArray().Select(row => row.GetProperty("shippedOn")).Where(d => d.ValueKind == JsonValueKind.String).ToList();
        Assert.NotEmpty(dates);
        Assert.All(dates, date => Assert.Matches(@"^\d{4}-\d{2}-\d{2}$", date.GetString()));
        await AssertRowsEqualPostgres(Requests.PageSize(200));
    }

    [RequiresEngineFact(Engine)]
    public Task DateRangeFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DateRange);

    [RequiresEngineFact(Engine)]
    public Task DatetimeRangeFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeRange);

    [RequiresEngineFact(Engine)]
    public Task DatetimeRangeWithBoundsAtNoon_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeRangeAtNoon);

    [RequiresEngineFact(Engine)]
    public Task DatetimeGreaterThanNoon_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DatetimeGreaterThanNoon);

    [RequiresEngineFact(Engine)]
    public Task BooleanFilter_EqualsPostgres() => AssertRowsEqualPostgres(Requests.Boolean);

    [RequiresEngineFact(Engine)]
    public Task WindowPastTheEnd_EqualsPostgres() => AssertRowsEqualPostgres(Requests.PastTheEnd);

    /// <summary>The adapter's result-side normalisation (SEM-4): <c>DATETIME(3)</c> has no zone, the driver hands back an unspecified-kind value, and the adapter delivers it as UTC so the JSON has the <c>Z</c>.</summary>
    [RequiresEngineFact(Engine)]
    public async Task Datetimes_AreSentAsUtcInstantsWithTheTrailingZ()
    {
        var page = await RowsOk(Provider, Requests.PageSize(5));

        Assert.All(page.GetProperty("rows").EnumerateArray(), row =>
            Assert.EndsWith("Z", row.GetProperty("orderedAt").GetString(), StringComparison.Ordinal));
    }

    // ---- groups and drill-down -------------------------------------------------------------------------

    [RequiresEngineTheory(Engine)]
    [InlineData("country")]
    [InlineData("category")]
    public async Task GroupBy_SumAvgCount_EqualPostgres(string column)
    {
        var body = Requests.GroupBy(column);
        Assert.True((await RowsOk("postgres", body)).GetProperty("rows").GetArrayLength() >= 5);

        await AssertRowsEqualPostgres(body);
    }

    /// <summary>MySQL averages a BIGINT with four decimals (<c>div_precision_increment</c>) on both MySQL routes, so the quantity average is not compared with PostgreSQL's sixteen digits.</summary>
    [RequiresEngineFact(Engine)]
    public Task GroupBy_AverageOfAWholeNumberColumn_HasTheDapperMySqlRoutesFourDecimals() =>
        AssertRowsEqual("mysql", Requests.GroupByWithQuantityAvg("country"));

    [RequiresEngineFact(Engine)]
    public async Task DrillDownIntoACountry_EqualsPostgres()
    {
        await AssertRowsEqualPostgres(Requests.DrillDownGermany);

        Assert.True(await RowCount(Provider, Requests.DrillDownGermany) > 0);
    }

    [RequiresEngineFact(Engine)]
    public Task DrillDownIntoTheNullCountry_EqualsPostgres() => AssertRowsEqualPostgres(Requests.DrillDownNullCountry);

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

    // ---- text semantics, measured ------------------------------------------------------------------------

    /// <summary>
    /// The second workaround's proof. The provider writes <c>LIKE ... ESCAPE '\'</c>, which MySQL reads as an escaped quote unless the session has
    /// NO_BACKSLASH_ESCAPES; without the interceptor every substring filter is a 500. The characters that need escaping inside the pattern
    /// (<c>%</c>, <c>_</c>, backslash) and the quote must still match literally: no row has them, so a leaked wildcard would return rows.
    /// </summary>
    [RequiresEngineTheory(Engine)]
    [InlineData("contains", "gmbh")]
    [InlineData("startsWith", "Müller")]
    [InlineData("endsWith", "& Co")]
    [InlineData("contains", "%")]
    [InlineData("contains", "_")]
    [InlineData("contains", "\\\\")]
    [InlineData("contains", "'")]
    [InlineData("contains", "100%_\\\\'")]
    public async Task SubstringFilterOnCustomer_EqualsPostgres_WithTheSessionMode(string type, string value) =>
        await AssertRowsEqualPostgres(Requests.TextFilter("customer", type, value));

    [RequiresEngineFact(Engine)]
    public async Task APercentOrUnderscore_IsNotAWildcard()
    {
        Assert.Equal(0, await RowCount(Provider, Requests.TextFilter("customer", "contains", "%")));
        Assert.Equal(0, await RowCount(Provider, Requests.TextFilter("customer", "contains", "_")));
    }

    /// <summary>
    /// The declared choice (<c>PatternMatchFoldsCase</c> false): the adapter lowers both sides, so a substring filter on the case-exact
    /// (utf8mb4_bin) columns folds case, like PostgreSQL. The Dapper <c>mysql</c> route compiles a bare <c>LIKE</c> and is case-exact there.
    /// </summary>
    [RequiresEngineTheory(Engine)]
    [InlineData("country", "contains", "germ")]
    [InlineData("country", "startsWith", "GERMANY")]
    [InlineData("status", "contains", "SHIP")]
    public async Task TextFilterOnABinaryCollatedColumn_FoldsCaseLikePostgres_NotLikeTheDapperMySqlRoute(string column, string type, string value)
    {
        var body = Requests.TextFilter(column, type, value);

        await AssertRowsEqualPostgres(body);
        Assert.True(await RowCount(Provider, body) > 0, "the folded filter finds rows");
        Assert.Equal(0, await RowCount("mysql", body));
    }

    /// <summary>The customer column keeps the table's accent-insensitive collation: "sohne" finds "Söhne" here and on <c>mysql</c>, not on PostgreSQL.</summary>
    [RequiresEngineFact(Engine)]
    public async Task NonAsciiContains_FindsTheCustomerInAnyCase_AndMySqlAlsoIgnoresTheAccent()
    {
        const string customer = "Müller, Söhne & Co";
        bool Finds(JsonElement page) => page.GetProperty("rows").EnumerateArray().Any(r => r.GetProperty("customer").GetString() == customer);
        static string Contains(string text) => Requests.TextFilter("customer", "contains", text);

        foreach (var text in new[] { "SÖHNE", "söhne", "Söhne", "sohne", "SOHNE" })
        {
            Assert.True(Finds(await RowsOk(Provider, Contains(text))), $"{Provider} {text}");
        }

        Assert.False(Finds(await RowsOk("postgres", Contains("sohne"))), "postgres is accent-sensitive");
    }

    // ---- CSV ------------------------------------------------------------------------------------------------

    [RequiresEngineFact(Engine)]
    public async Task Csv_StatusExport_EqualsPostgresCellByCell()
    {
        var pg = await ApiHostFixture.PostRawAsync(api.Client, "postgres", "export.csv", Requests.StatusExport);
        var ef = await ApiHostFixture.PostRawAsync(api.Client, Provider, "export.csv", Requests.StatusExport);

        Assert.Equal(HttpStatusCode.OK, ef.Status);
        Assert.True(EfParity.CsvSame(EfParity.ParseCsv(pg.Bytes), EfParity.ParseCsv(ef.Bytes)));
    }

    /// <summary>The whole table through <c>EfCoreExportStream</c> (more than one keyset chunk).</summary>
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

    // ---- compile only and errors ------------------------------------------------------------------------

    [RequiresEngineFact(Engine)]
    public async Task TheQueryView_IsSqlWithTheEfMySqlDialect()
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, Provider, "query", Requests.TextNumberSort);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("sql", json.GetProperty("kind").GetString());
        Assert.Equal("ef-mysql", json.GetProperty("dialect").GetString());
        Assert.False(string.IsNullOrWhiteSpace(json.GetProperty("text").GetString()));
    }

    /// <summary>The query view prints the provider's own text; for a substring filter that is <c>ESCAPE '\'</c>, valid only with the session mode on (README).</summary>
    [RequiresEngineFact(Engine)]
    public async Task TheQueryView_ForASubstringFilter_ShowsTheProvidersEscapeClause()
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, Provider, "query", Requests.TextFilter("customer", "contains", "gmbh"));

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Contains("ESCAPE", json.GetProperty("text").GetString());
    }

    [RequiresEngineFact(Engine)]
    public async Task UnknownColumnFilter_Is400UnknownField()
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, Provider, "rows", Requests.UnknownColumn);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("unknown-field", json.GetProperty("error").GetProperty("kind").GetString());
    }

    /// <summary>A host whose MySQL points at a port nothing listens on: it needs no container. Oracle's driver throws its own <c>MySqlException</c> type, which <c>EngineFailures</c> must know.</summary>
    [Theory]
    [InlineData("rows")]
    [InlineData("values")]
    [InlineData("export.csv")]
    public async Task AnEngineThatIsNotListening_Is503BackendUnavailable(string route)
    {
        var (app, client) = await ApiHostFixture.StartAsync(null, builder => builder.Configuration[DemoSettings.MySqlEnvVar] =
            $"Server=127.0.0.1;Port={ApiHostFixture.FreePort()};User ID=demo;Password=unused;Database=demo;Connection Timeout=1");
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

    // ---- Oracle's driver exception (no engine needed except where marked) --------------------------------

    /// <summary>A refused connection through Oracle's driver is its own <c>MySqlException</c> type with number 1042 (measured), declared in <see cref="EngineFailures"/>.</summary>
    [Fact]
    public async Task OraclesDriver_ARefusedConnection_Is1042_AndUnavailable()
    {
        await using var connection = new OracleMySqlConnection(
            $"Server=127.0.0.1;Port={ApiHostFixture.FreePort()};User ID=demo;Password=unused;Database=demo;Connection Timeout=1");

        var failure = await Assert.ThrowsAsync<OracleMySqlException>(() => connection.OpenAsync());

        Assert.Equal(1042, failure.Number);
        Assert.True(EngineFailures.IsUnavailable(failure));
    }

    [Theory]
    [InlineData(1042, true)]
    [InlineData(1045, false)]
    [InlineData(1044, false)]
    [InlineData(1049, false)]
    [InlineData(1064, false)]
    [InlineData(1146, false)]
    public void EngineFailures_OraclesDriver_ListsTheConnectionNumber_AndNoLoginOrStatementErrors(int number, bool unavailable) =>
        Assert.Equal(unavailable, EngineFailures.MySqlConnectionFailureNumbers.Contains(number));

    /// <summary>A refused login is a setting error: 1045 through Oracle's driver stays a 500, not a 503.</summary>
    [RequiresEngineFact(Engine)]
    public async Task OraclesDriver_ARefusedLogin_Is1045_AndNotUnavailable()
    {
        var builder = new OracleMySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Engine)!) { Password = "nope", Pooling = false };
        await using var connection = new OracleMySqlConnection(builder.ConnectionString);

        var failure = await Assert.ThrowsAsync<OracleMySqlException>(() => connection.OpenAsync());

        Assert.Equal(1045, failure.Number);
        Assert.False(EngineFailures.IsUnavailable(failure));
    }

    /// <summary>A bad statement through the route is a bug and stays a 500: only the connection numbers map to 503.</summary>
    [RequiresEngineFact(Engine)]
    public async Task OraclesDriver_ABadStatement_IsNotUnavailable()
    {
        await using var connection = new OracleMySqlConnection(Environment.GetEnvironmentVariable(Engine)!);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELEKT 1";

        var failure = await Assert.ThrowsAsync<OracleMySqlException>(() => command.ExecuteNonQueryAsync());

        Assert.Equal(1064, failure.Number);
        Assert.False(EngineFailures.IsUnavailable(failure));
    }
}
