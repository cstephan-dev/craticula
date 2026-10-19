using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Craticula.Sql;
using Demo.Api.Errors;
using Demo.Api.Grid;
using Demo.Api.Seed;
using Microsoft.Data.Sqlite;

namespace Demo.Api.Tests;

/// <summary>
/// The <c>sqlite</c> provider. <see cref="ApiHostFixture"/> seeds a temporary SQLite file from the same
/// <c>OrderGenerator</c> rows as PostgreSQL and serves it, so the comparisons with <c>postgres</c> (the baseline) need only
/// PostgreSQL to be up. Two differences are documented, not hidden: SQLite stores NUMERIC as floating point, so a sum can
/// differ from PostgreSQL's exact decimal in the last digits (relative 1e-9, the rule smoke.sh uses for keys ending
/// <c>_sum</c>; <c>_avg</c> keeps 1e-6), and NOCASE folds ASCII only.
/// </summary>
[Collection("api")]
public sealed class SqliteProviderTests(ApiHostFixture api)
{
    // ---- helpers -----------------------------------------------------------------------------------

    private async Task<JsonElement> RowsOk(string provider, string body)
    {
        var (status, json) = await api.PostJsonAsync(provider, "rows", body);
        Assert.True(HttpStatusCode.OK == status, $"{provider}: {status} {json}");
        return json;
    }

    /// <summary>Numbers by value (12877.0 equals 12877), except keys ending _sum (relative 1e-9, sqlite only) and _avg (1e-6);
    /// everything else exact.</summary>
    private static bool Same(JsonElement a, JsonElement b, string key = "")
    {
        if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
        {
            var left = a.EnumerateObject().ToArray();
            var right = b.EnumerateObject().ToArray();
            return left.Length == right.Length
                && left.All(property => b.TryGetProperty(property.Name, out var other) && Same(property.Value, other, property.Name));
        }

        if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array)
        {
            return a.GetArrayLength() == b.GetArrayLength()
                && a.EnumerateArray().Zip(b.EnumerateArray()).All(pair => Same(pair.First, pair.Second, key));
        }

        if (a.ValueKind == JsonValueKind.Number && b.ValueKind == JsonValueKind.Number)
        {
            var x = a.GetDouble();
            var y = b.GetDouble();
            var scale = Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
            return key.EndsWith("_sum", StringComparison.Ordinal) ? Math.Abs(x - y) <= 1e-9 * scale
                : key.EndsWith("_avg", StringComparison.Ordinal) ? Math.Abs(x - y) <= 1e-6 * scale
                : a.GetDecimal() == b.GetDecimal();
        }

        return a.ValueKind == b.ValueKind && a.GetRawText() == b.GetRawText();
    }

    private async Task AssertRowsEqualPostgres(string body)
    {
        var pg = await RowsOk("postgres", body);
        var sqlite = await RowsOk("sqlite", body);

        Assert.Equal(pg.GetProperty("lastRow").GetInt64(), sqlite.GetProperty("lastRow").GetInt64());
        var pgRows = pg.GetProperty("rows");
        var sqliteRows = sqlite.GetProperty("rows");
        Assert.Equal(pgRows.GetArrayLength(), sqliteRows.GetArrayLength());
        for (var i = 0; i < pgRows.GetArrayLength(); i++)
        {
            Assert.True(Same(pgRows[i], sqliteRows[i]), $"row {i}: {pgRows[i]} vs {sqliteRows[i]}");
        }
    }

    private static List<List<string>> ParseCsv(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes[3..]).Replace("\r\n", "\n");
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(',').ToList())];
    }

    // ---- rows: filters, sort, paging ----------------------------------------------------------------

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

    // ---- grouping and drill-down ---------------------------------------------------------------------

    [RequiresDatabasesTheory]
    [InlineData("country")]
    [InlineData("category")]
    public async Task GroupBy_SumAvgCountEqualPostgresWithinTheDocumentedTolerance(string column)
    {
        var body = Requests.GroupBy(column);
        var pg = (await RowsOk("postgres", body)).GetProperty("rows");
        Assert.True(pg.GetArrayLength() >= 5);

        await AssertRowsEqualPostgres(body);
    }

    [RequiresDatabasesFact]
    public async Task DrillDownIntoACountry_EqualsPostgres()
    {
        await AssertRowsEqualPostgres(Requests.DrillDownGermany);

        var page = await RowsOk("sqlite", Requests.DrillDownGermany);
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
        Assert.All(page.GetProperty("rows").EnumerateArray(), row => Assert.Equal("Germany", row.GetProperty("country").GetString()));
    }

    // ---- set values --------------------------------------------------------------------------------

    [RequiresDatabasesTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SetValues_EqualPostgres(bool filtered)
    {
        var body = filtered ? Requests.FilteredCountryValues : Requests.CountryValues;
        var pg = await api.PostJsonAsync("postgres", "values", body);
        var sqlite = await api.PostJsonAsync("sqlite", "values", body);

        Assert.Equal(HttpStatusCode.OK, sqlite.Status);
        string[] Canon(JsonElement json) => [.. json.GetProperty("values").EnumerateArray().Select(v => v.GetRawText()).Order(StringComparer.Ordinal)];
        Assert.Equal(Canon(pg.Body), Canon(sqlite.Body));
        Assert.Contains("\"Germany\"", Canon(sqlite.Body));
    }

    /// <summary>Needs no PostgreSQL: the filtered statement has a parameter, so this proves the <c>?n</c> renumbering end to end.</summary>
    [Fact]
    public async Task SetValues_WithAFilterParameter_RunsOnTheFile()
    {
        var (status, json) = await api.PostJsonAsync("sqlite", "values", Requests.FilteredCountryValues);

        Assert.Equal(HttpStatusCode.OK, status);
        var values = json.GetProperty("values").EnumerateArray().Select(v => v.GetString()).ToArray();
        Assert.Contains("Germany", values);
        Assert.Equal(values.Distinct().Count(), values.Length);
    }

    // ---- CSV ---------------------------------------------------------------------------------------

    [RequiresDatabasesFact]
    public async Task Csv_StatusExport_EqualsPostgresCellByCell()
    {
        var pg = await api.PostRawAsync("postgres", "export.csv", Requests.StatusExport);
        var sqlite = await api.PostRawAsync("sqlite", "export.csv", Requests.StatusExport);

        Assert.Equal(HttpStatusCode.OK, sqlite.Status);
        Assert.StartsWith("text/csv", sqlite.ContentType);
        Assert.True(sqlite.Bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM");
        var left = ParseCsv(pg.Bytes);
        var right = ParseCsv(sqlite.Bytes);
        Assert.True(left.Count > 500);
        Assert.Equal(left.Count, right.Count);
        for (var i = 0; i < left.Count; i++)
        {
            Assert.Equal(left[i].Count, right[i].Count);
            for (var c = 0; c < left[i].Count; c++)
            {
                // PostgreSQL writes numeric(12,2) as 20568.00, SQLite's REAL as 20568: the same number.
                var same = left[i][c] == right[i][c]
                    || (decimal.TryParse(left[i][c], NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
                        && decimal.TryParse(right[i][c], NumberStyles.Number, CultureInfo.InvariantCulture, out var b) && a == b);
                Assert.True(same, $"line {i}, column {c}: {left[i][c]} vs {right[i][c]}");
            }
        }
    }

    [Fact]
    public async Task Csv_PrefixesAFormulaCustomerWithAnApostrophe()
    {
        var (status, _, _, bytes) = await api.PostRawAsync("sqlite", "export.csv", Requests.FormulaCustomer);

        Assert.Equal(HttpStatusCode.OK, status);
        var records = ParseCsv(bytes);
        Assert.True(records.Count >= 2, "the =SUM(A1) customer was not exported");
        Assert.All(records.Skip(1), r => Assert.Equal("'=SUM(A1)", r[2]));
    }

    // ---- compile only and errors ---------------------------------------------------------------------

    [Fact]
    public async Task Query_ReturnsParameterizedSqliteSqlAndNeverTheLiteralValue()
    {
        var (status, json) = await api.PostJsonAsync("sqlite", "query", Requests.NeedleFilter);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("sql", json.GetProperty("kind").GetString());
        Assert.Equal("sqlite", json.GetProperty("dialect").GetString());
        var sql = json.GetProperty("text").GetString()!;
        Assert.False(string.IsNullOrWhiteSpace(sql));
        Assert.DoesNotContain(Requests.UnlikelyValue, sql);
        Assert.Contains(json.GetProperty("parameters").EnumerateArray(), p => p.ValueKind == JsonValueKind.String && p.GetString() == Requests.UnlikelyValue);
    }

    [Fact]
    public async Task UnknownColumnFilter_Is400UnknownField()
    {
        var (status, json) = await api.PostJsonAsync("sqlite", "rows", Requests.UnknownColumn);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("unknown-field", json.GetProperty("error").GetProperty("kind").GetString());
        Assert.DoesNotContain("   at ", json.ToString());
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
            using var response = await client.PostAsync($"/api/sqlite/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("backend-unavailable", json.GetProperty("error").GetProperty("kind").GetString());
            Assert.False(File.Exists(missing), "the API must never create the database file");
        }
    }

    // ---- the documented non-ASCII difference ---------------------------------------------------------

    /// <summary>
    /// PostgreSQL's collation is case-insensitive beyond ASCII; SQLite's NOCASE and LIKE fold ASCII only. Measured: "SÖHNE" finds
    /// "Müller, Söhne &amp; Co" on postgres and does NOT on sqlite (Ö and ö differ); "söhne" finds it on both (only the ASCII S
    /// differs in case). The smoke scenarios avoid this case.
    /// </summary>
    [RequiresDatabasesFact]
    public async Task NonAsciiContains_FindsTheCustomerOnPostgresButNotOnSqlite_WhenTheNonAsciiLetterChangesCase()
    {
        const string customer = "Müller, Söhne & Co";
        bool Finds(JsonElement page) => page.GetProperty("rows").EnumerateArray().Any(r => r.GetProperty("customer").GetString() == customer);

        Assert.True(Finds(await RowsOk("postgres", Requests.NonAsciiContains)));
        Assert.False(Finds(await RowsOk("sqlite", Requests.NonAsciiContains)));
        Assert.Equal(0, (await RowsOk("sqlite", Requests.NonAsciiContains)).GetProperty("lastRow").GetInt64());

        Assert.True(Finds(await RowsOk("postgres", Requests.NonAsciiContainsLower)));
        Assert.True(Finds(await RowsOk("sqlite", Requests.NonAsciiContainsLower)));
    }

    // ---- the seed and the binding --------------------------------------------------------------------

    [Fact]
    public async Task Seeder_StoresTheCanonicalTextFormsAndEveryRow()
    {
        var path = Path.Combine(Path.GetTempPath(), $"craticula-demo-seed-{Guid.NewGuid():N}", "orders.sqlite");
        try
        {
            var rows = OrderGenerator.Generate();
            // Twice: the second run drops and recreates, and creates the missing folder the first time.
            Assert.Equal(rows.Count, await SqliteSeeder.SeedAsync(path, rows));
            Assert.Equal(rows.Count, await SqliteSeeder.SeedAsync(path, rows));

            await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
            await connection.OpenAsync();
            await using var command = new SqliteCommand(
                "SELECT count(*), sum(ordered_at GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]T[0-9][0-9]:[0-9][0-9]:[0-9][0-9].[0-9][0-9][0-9]Z'), " +
                "sum(shipped_on IS NULL OR shipped_on GLOB '[0-9][0-9][0-9][0-9]-[0-9][0-9]-[0-9][0-9]'), sum(is_priority IN (0, 1)), " +
                "sum(customer = 'Müller, Söhne & Co') FROM orders", connection);
            await using var reader = await command.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal(rows.Count, reader.GetInt64(0));
            Assert.Equal(rows.Count, reader.GetInt64(1));
            Assert.Equal(rows.Count, reader.GetInt64(2));
            Assert.Equal(rows.Count, reader.GetInt64(3));
            Assert.True(reader.GetInt64(4) > 0, "the non-ASCII customer is seeded, not filtered out");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }

    [Fact]
    public void FormatDatetime_IsUtcWithExactlyThreeDigitsAndZ()
    {
        var instant = new DateTime(2025, 6, 1, 8, 5, 3, 7, DateTimeKind.Utc);

        Assert.Equal("2025-06-01T08:05:03.007Z", SqliteSeeder.FormatDatetime(instant));
        Assert.Equal("2025-06-01T08:05:03.000Z", SqliteSeeder.FormatDatetime(instant.AddMilliseconds(-7)));
        Assert.Matches(new Regex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{3}Z$"), SqliteSeeder.FormatDatetime(DateTime.UtcNow));
        Assert.Equal("2025-06-01", SqliteSeeder.FormatDate(new DateOnly(2025, 6, 1)));
    }

    [Fact]
    public void NumberPlaceholders_NumbersEveryMarkerInOrder_AndRefusesACountMismatch()
    {
        Assert.Equal("a = ?1 AND b IN (?2, ?3) LIMIT ?4 OFFSET ?5", SqliteBinding.NumberPlaceholders("a = ? AND b IN (?, ?) LIMIT ? OFFSET ?", 5));
        Assert.Equal("SELECT 1", SqliteBinding.NumberPlaceholders("SELECT 1", 0));
        Assert.Throws<InvalidOperationException>(() => SqliteBinding.NumberPlaceholders("a = ?", 2));
    }

    [Fact]
    public void EngineFailures_RecognisesSqliteCannotOpenBusyAndLocked_ButNotOtherSqliteErrors()
    {
        Assert.True(EngineFailures.IsUnavailable(new SqliteException("unable to open database file", 14)));
        Assert.True(EngineFailures.IsUnavailable(new SqliteException("database is locked", 5)));
        Assert.True(EngineFailures.IsUnavailable(new SqliteException("table is locked", 6)));
        Assert.False(EngineFailures.IsUnavailable(new SqliteException("no such table: orders", 1)));
        Assert.True(EngineFailures.IsUnavailable(new SqliteVersionException("too old")));
    }

    [Fact]
    public void TheBundledSqlite_IsNewEnoughForTheDialect()
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = new SqliteCommand("SELECT sqlite_version()", connection);

        Assert.True(Version.Parse((string)command.ExecuteScalar()!) >= SqliteSeeder.MinimumVersion);
        Assert.Equal("sqlite", SqliteDialect.Instance.Name);
    }
}
