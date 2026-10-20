using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Craticula.Sql;
using Demo.Api.Errors;
using Demo.Api.Grid;
using Demo.Api.Seed;
using Microsoft.Data.SqlClient;

namespace Demo.Api.Tests;

/// <summary>
/// The <c>sqlserver</c> provider, an optional engine. The comparisons with <c>postgres</c> (the baseline) run only when
/// <c>DEMO_SQLSERVER</c> is set and the engine answers (<see cref="RequiresEngineFactAttribute"/>); the tests that need no engine
/// (a closed port, the query view, the binding, the classification) always run. The documented differences, measured on
/// SQL Server 2022 CU14: <c>AVG</c> over a BIGINT column returns an integer (the baseline value truncated toward zero), and
/// AVG over the decimal column has six decimals (inside smoke.sh's 1e-6 relative tolerance). Everything else is exact.
/// </summary>
[Collection("api-sqlserver")]
// Acceptance ids (docs/feat-breakdown/2494): AC-1 rows, AC-2 groups and drill-down, AC-3 CSV, AC-6 typed 4xx; the per-AC method list is in docs/feat/2499/validation.md.
public sealed class SqlServerProviderTests(SqlServerApiFixture api)
{
    private const string Engine = DemoSettings.SqlServerEnvVar;

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task<JsonElement> RowsOk(string provider, string body)
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, provider, "rows", body);
        Assert.True(HttpStatusCode.OK == status, $"{provider}: {status} {json}");
        return json;
    }

    /// <summary>Numbers by value (12877.0 equals 12877). Keys ending <c>quantity_avg</c> must equal the baseline truncated toward zero
    /// (SQL Server's integer AVG); other <c>_avg</c> keys compare with a relative 1e-6; everything else is exact.</summary>
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
            if (key.EndsWith("quantity_avg", StringComparison.Ordinal))
            {
                return other.GetDecimal() == decimal.Truncate((decimal)baseline.GetDouble());
            }

            if (key.EndsWith("_avg", StringComparison.Ordinal))
            {
                var x = baseline.GetDouble();
                var y = other.GetDouble();
                return Math.Abs(x - y) <= 1e-6 * Math.Max(1, Math.Max(Math.Abs(x), Math.Abs(y)));
            }

            return baseline.GetDecimal() == other.GetDecimal();
        }

        return baseline.ValueKind == other.ValueKind && baseline.GetRawText() == other.GetRawText();
    }

    private async Task AssertRowsEqualPostgres(string body)
    {
        var pg = await RowsOk("postgres", body);
        var sql = await RowsOk("sqlserver", body);

        Assert.Equal(pg.GetProperty("lastRow").GetInt64(), sql.GetProperty("lastRow").GetInt64());
        var pgRows = pg.GetProperty("rows");
        var sqlRows = sql.GetProperty("rows");
        Assert.Equal(pgRows.GetArrayLength(), sqlRows.GetArrayLength());
        for (var i = 0; i < pgRows.GetArrayLength(); i++)
        {
            Assert.True(Same(pgRows[i], sqlRows[i]), $"row {i}: {pgRows[i]} vs {sqlRows[i]}");
        }
    }

    private static List<List<string>> ParseCsv(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes[3..]).Replace("\r\n", "\n");
        return [.. text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.Split(',').ToList())];
    }

    /// <summary>A host whose <c>sqlserver</c> points at a port nothing listens on: it needs no container.</summary>
    private static Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client)> StartWithClosedEngineAsync() =>
        ApiHostFixture.StartAsync(null, builder => builder.Configuration[DemoSettings.SqlServerEnvVar] =
            $"Server=127.0.0.1,{ApiHostFixture.FreePort()};User Id=sa;Password=unused;Database=demo;TrustServerCertificate=true;Connect Timeout=1");

    // ---- rows: filters, sort, paging ----------------------------------------------------------------

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

    [RequiresEngineFact(Engine)]
    public async Task Datetimes_AreSentAsUtcInstantsWithTheTrailingZ()
    {
        // datetime2 has no zone; the library's Dapper adapter reads it as UTC (SEM-4, #2511), or the JSON would drop the Z every other provider sends.
        var page = await RowsOk("sqlserver", Requests.PageSize(5));

        Assert.All(page.GetProperty("rows").EnumerateArray(), row =>
            Assert.EndsWith("Z", row.GetProperty("orderedAt").GetString(), StringComparison.Ordinal));
    }

    // ---- multi-sorts that name a field twice or go past the stable key (bug #2565, SEM-6) ----------------

    private static (string ColId, string Direction)[] ParseSort(string text) =>
        [.. text.Split('|').Select(part => (part.Split(' ')[0], part.Split(' ')[1]))];

    /// <summary>Each of these used to answer 500 on SQL Server (error 169, the compiled ORDER BY named a column twice): the stable key first,
    /// in the middle, a repeated column, and a repeated stable key. The library now reduces the sort to one key per field and ends it at the
    /// stable key, so the rows equal PostgreSQL's.</summary>
    [RequiresEngineTheory(Engine)]
    [InlineData("id desc|amount asc")]
    [InlineData("amount desc|id asc|country asc")]
    [InlineData("amount desc|amount asc")]
    [InlineData("id asc|id desc")]
    public Task AMultiSortThatRepeatsAFieldOrGoesPastTheStableKey_EqualsPostgres(string sort) =>
        AssertRowsEqualPostgres(Requests.MultiSort(200, ParseSort(sort)));

    [RequiresEngineTheory(Engine)]
    [InlineData("id desc|amount asc")]
    [InlineData("amount desc|id asc|country asc")]
    public async Task TheExportOfAMultiSortThatGoesPastTheStableKey_EqualsPostgresOverEveryChunk(string sort)
    {
        var body = Requests.MultiSortExport(ParseSort(sort));
        var pg = await ApiHostFixture.PostRawAsync(api.Client, "postgres", "export.csv", body);
        var sql = await ApiHostFixture.PostRawAsync(api.Client, "sqlserver", "export.csv", body);

        Assert.Equal(HttpStatusCode.OK, sql.Status);
        var rows = ParseCsv(sql.Bytes);
        Assert.True(rows.Count > 5000, "more than one keyset chunk");
        Assert.Equal(ParseCsv(pg.Bytes).Count, rows.Count);
    }

    // ---- grouping and drill-down ---------------------------------------------------------------------

    [RequiresEngineTheory(Engine)]
    [InlineData("country")]
    [InlineData("category")]
    public async Task GroupBy_SumAvgCount_EqualPostgres(string column)
    {
        var body = Requests.GroupBy(column);
        Assert.True((await RowsOk("postgres", body)).GetProperty("rows").GetArrayLength() >= 5);

        await AssertRowsEqualPostgres(body);
    }

    [RequiresEngineTheory(Engine)]
    [InlineData("country")]
    [InlineData("category")]
    public async Task GroupBy_AverageOfAWholeNumberColumn_IsThePostgresValueTruncatedTowardZero(string column)
    {
        var body = Requests.GroupByWithQuantityAvg(column);
        var pg = (await RowsOk("postgres", body)).GetProperty("rows");
        var sql = (await RowsOk("sqlserver", body)).GetProperty("rows");

        Assert.Equal(pg.GetArrayLength(), sql.GetArrayLength());
        for (var i = 0; i < pg.GetArrayLength(); i++)
        {
            var baseline = pg[i].GetProperty("quantity_avg").GetDouble();
            // The demo data averages about 25.xx: a fraction the baseline keeps and SQL Server drops.
            Assert.NotEqual(Math.Floor(baseline), baseline);
            Assert.Equal((decimal)Math.Floor(baseline), sql[i].GetProperty("quantity_avg").GetDecimal());
        }

        await AssertRowsEqualPostgres(body);
    }

    [RequiresEngineFact(Engine)]
    public async Task DrillDownIntoACountry_EqualsPostgres()
    {
        await AssertRowsEqualPostgres(Requests.DrillDownGermany);

        var page = await RowsOk("sqlserver", Requests.DrillDownGermany);
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
        Assert.All(page.GetProperty("rows").EnumerateArray(), row => Assert.Equal("Germany", row.GetProperty("country").GetString()));
    }

    // ---- set values --------------------------------------------------------------------------------

    [RequiresEngineTheory(Engine)]
    [InlineData("plain")]
    [InlineData("status filter")]
    [InlineData("date filters")]
    [InlineData("datetime range")]
    public async Task SetValues_EqualPostgres(string kind)
    {
        var body = kind switch
        {
            "plain" => Requests.CountryValues,
            "status filter" => Requests.FilteredCountryValues,
            "date filters" => Requests.DateFilteredCountryValues,
            _ => Requests.DatetimeFilteredCountryValues
        };
        var pg = await ApiHostFixture.PostJsonAsync(api.Client, "postgres", "values", body);
        var sql = await ApiHostFixture.PostJsonAsync(api.Client, "sqlserver", "values", body);

        // Date and datetime values are bound as strings (the adapter's way): SQL Server converts them on comparison.
        Assert.Equal(HttpStatusCode.OK, sql.Status);
        string[] Canon(JsonElement json) => [.. json.GetProperty("values").EnumerateArray().Select(v => v.GetRawText()).Order(StringComparer.Ordinal)];
        Assert.Equal(Canon(pg.Body), Canon(sql.Body));
        if (kind == "datetime range")
        {
            Assert.InRange(Canon(sql.Body).Length, 1, 5);
        }
    }

    // ---- CSV ---------------------------------------------------------------------------------------

    [RequiresEngineFact(Engine)]
    public async Task Csv_StatusExport_EqualsPostgresCellByCell()
    {
        var pg = await ApiHostFixture.PostRawAsync(api.Client, "postgres", "export.csv", Requests.StatusExport);
        var sql = await ApiHostFixture.PostRawAsync(api.Client, "sqlserver", "export.csv", Requests.StatusExport);

        Assert.Equal(HttpStatusCode.OK, sql.Status);
        Assert.StartsWith("text/csv", sql.ContentType);
        Assert.True(sql.Bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM");
        var left = ParseCsv(pg.Bytes);
        var right = ParseCsv(sql.Bytes);
        // More than one page of the walk (5000 rows), so the second chunk is exercised too.
        Assert.True(left.Count > 3000);
        Assert.Equal(left.Count, right.Count);
        for (var i = 0; i < left.Count; i++)
        {
            Assert.Equal(left[i].Count, right[i].Count);
            for (var c = 0; c < left[i].Count; c++)
            {
                var same = left[i][c] == right[i][c]
                    || (decimal.TryParse(left[i][c], NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
                        && decimal.TryParse(right[i][c], NumberStyles.Number, CultureInfo.InvariantCulture, out var b) && a == b);
                Assert.True(same, $"line {i}, column {c}: {left[i][c]} vs {right[i][c]}");
            }
        }
    }

    [RequiresEngineFact(Engine)]
    public async Task Csv_WholeTableExport_HasEveryRowOnceInKeyOrder()
    {
        var (status, _, _, bytes) = await ApiHostFixture.PostRawAsync(api.Client, "sqlserver", "export.csv", Requests.FullExport);

        Assert.Equal(HttpStatusCode.OK, status);
        // The seed holds a customer with a line break inside quotes: only the lines that start a record carry an id.
        var ids = Encoding.UTF8.GetString(bytes[3..]).Replace("\r\n", "\n").Split('\n')
            .Select(line => System.Text.RegularExpressions.Regex.Match(line, @"^(\d+),"))
            .Where(match => match.Success)
            .Select(match => long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture))
            .ToArray();
        Assert.Equal(20000, ids.Length);
        Assert.Equal(Enumerable.Range(1, 20000).Select(i => (long)i), ids);
    }

    [RequiresEngineFact(Engine)]
    public async Task Csv_PrefixesAFormulaCustomerWithAnApostrophe()
    {
        var (status, _, _, bytes) = await ApiHostFixture.PostRawAsync(api.Client, "sqlserver", "export.csv", Requests.FormulaCustomer);

        Assert.Equal(HttpStatusCode.OK, status);
        var records = ParseCsv(bytes);
        Assert.True(records.Count >= 2, "the =SUM(A1) customer was not exported");
        Assert.All(records.Skip(1), r => Assert.Equal("'=SUM(A1)", r[2]));
    }

    // ---- text semantics, measured -----------------------------------------------------------------------

    /// <summary>
    /// Measured on SQL Server 2022 CU14: contains-filters compile to <c>COLLATE Latin1_General_CI_AS</c>, which folds the case of
    /// non-ASCII letters too, so "SÖHNE" finds "Müller, Söhne &amp; Co" exactly as on PostgreSQL (SQLite's ASCII-only NOCASE does
    /// not). "söhne" finds it too. There is no difference to document for this case.
    /// </summary>
    [RequiresEngineFact(Engine)]
    public async Task NonAsciiContains_FindsTheCustomerInAnyCaseJustLikePostgres()
    {
        const string customer = "Müller, Söhne & Co";
        bool Finds(JsonElement page) => page.GetProperty("rows").EnumerateArray().Any(r => r.GetProperty("customer").GetString() == customer);

        foreach (var body in new[] { Requests.NonAsciiContains, Requests.NonAsciiContainsLower })
        {
            Assert.True(Finds(await RowsOk("postgres", body)));
            Assert.True(Finds(await RowsOk("sqlserver", body)));
        }
    }

    // ---- compile only and errors (no engine needed) ---------------------------------------------------

    [Fact]
    public async Task Query_ReturnsParameterizedMssqlSqlAndNeverTheLiteralValue()
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var (status, json) = await ApiHostFixture.PostJsonAsync(client, "sqlserver", "query", Requests.NeedleFilter);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("sql", json.GetProperty("kind").GetString());
            Assert.Equal("mssql", json.GetProperty("dialect").GetString());
            var sql = json.GetProperty("text").GetString()!;
            Assert.Contains("@p1", sql);
            Assert.DoesNotContain(Requests.UnlikelyValue, sql);
            Assert.Contains(json.GetProperty("parameters").EnumerateArray(), p => p.ValueKind == JsonValueKind.String && p.GetString()!.Contains(Requests.UnlikelyValue));
        }
    }

    [Fact]
    public async Task UnknownColumnFilter_Is400UnknownField()
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var (status, json) = await ApiHostFixture.PostJsonAsync(client, "sqlserver", "rows", Requests.UnknownColumn);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("unknown-field", json.GetProperty("error").GetProperty("kind").GetString());
            Assert.DoesNotContain("   at ", json.ToString());
        }
    }

    [Theory]
    [InlineData("rows")]
    [InlineData("values")]
    [InlineData("export.csv")]
    public async Task AnEngineThatIsNotListening_Is503BackendUnavailable(string route)
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var body = route == "values" ? Requests.CountryValues : route == "export.csv" ? Requests.StatusExport : Requests.PageSize(10);
            using var response = await client.PostAsync($"/api/sqlserver/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("backend-unavailable", json.GetProperty("error").GetProperty("kind").GetString());
        }
    }

    [Fact]
    public async Task AnEngineThatIsNotListening_WithoutAConnectTimeoutInTheString_AnswersAfterTheServicesFiveSeconds()
    {
        // The driver's own default is 15 s; the service sets 5 s when the string says nothing (measured: a refused connection is retried until then).
        var (app, client) = await ApiHostFixture.StartAsync(null, builder => builder.Configuration[DemoSettings.SqlServerEnvVar] =
            $"Server=127.0.0.1,{ApiHostFixture.FreePort()};User Id=sa;Password=unused;TrustServerCertificate=true");
        await using (app)
        using (client)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var (status, _) = await ApiHostFixture.PostJsonAsync(client, "sqlserver", "rows", Requests.PageSize(10));

            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.InRange(clock.Elapsed.TotalSeconds, 3, 10);
        }
    }

    [Fact]
    public async Task Health_ListsSqlServerOnlyWhenItsSettingIsPresent()
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var json = JsonDocument.Parse(await client.GetStringAsync("/api/health")).RootElement;
            Assert.Equal(
                ["ef", "ef-sqlite", "ef-sqlserver", "mongo", "postgres", "sqlite", "sqlserver"],
                json.GetProperty("providers").EnumerateArray().Select(p => p.GetString()!));
        }
    }

    // ---- engine failures, the binding and the seed target (no engine needed) ---------------------------

    [Theory]
    [InlineData(2, true)]
    [InlineData(53, true)]
    [InlineData(-1, true)]
    [InlineData(-2, true)]
    [InlineData(258, true)]
    [InlineData(4060, true)]
    [InlineData(18456, false)]
    [InlineData(11001, true)]
    [InlineData(-2146893019, false)]
    [InlineData(208, false)]
    [InlineData(207, false)]
    [InlineData(209, false)]
    [InlineData(102, false)]
    public void EngineFailures_ListsTheSqlServerConnectionNumbers_AndNoStatementErrors(int number, bool unavailable) =>
        Assert.Equal(unavailable, EngineFailures.SqlServerConnectionFailureNumbers.Contains(number));

    [Fact]
    public async Task EngineFailures_ARealRefusedConnection_IsUnavailable()
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = $"127.0.0.1,{ApiHostFixture.FreePort()}",
            UserID = "sa",
            Password = "not-used",
            TrustServerCertificate = true,
            ConnectTimeout = 1,
            ConnectRetryCount = 0
        };
        await using var connection = new SqlConnection(builder.ConnectionString);

        var failure = await Assert.ThrowsAsync<SqlException>(() => connection.OpenAsync());

        Assert.True(EngineFailures.IsUnavailable(failure), $"SqlException number {failure.Number} is not declared");
    }

    [RequiresEngineFact(Engine)]
    public async Task EngineFailures_AStatementError_StaysAnOrdinaryFailure()
    {
        await using var connection = new SqlConnection(Environment.GetEnvironmentVariable(Engine));
        await connection.OpenAsync();
        await using var command = new SqlCommand("SELECT * FROM no_such_table_in_demo", connection);

        var failure = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteScalarAsync());

        Assert.Equal(208, failure.Number);
        Assert.False(EngineFailures.IsUnavailable(failure));
    }

    [Fact]
    public void CreateCommand_BindsEveryParameterUnderItsPlaceholderName_AndRefusesAList()
    {
        var query = new SqlQuery
        {
            Sql = "SELECT 1 WHERE [a] = @p1 AND [b] IN (@p2, @p3)",
            Dialect = "mssql",
            Parameters = ["x", 7L, null]
        };
        using var connection = new SqlConnection();

        using var command = SqlServerBinding.CreateCommand(connection, query);

        Assert.Equal(query.Sql, command.CommandText);
        Assert.Equal(["@p1", "@p2", "@p3"], command.Parameters.Cast<SqlParameter>().Select(p => p.ParameterName));
        Assert.Equal("x", command.Parameters["@p1"].Value);
        Assert.Equal(7L, command.Parameters["@p2"].Value);
        Assert.Equal(DBNull.Value, command.Parameters["@p3"].Value);
        Assert.Throws<NotSupportedException>(() =>
            SqlServerBinding.CreateCommand(connection, query with { Parameters = [new List<object?> { 1L }] }));
    }

    [Fact]
    public void TheSqlServerSeedTarget_IsNotADefault_AndAMissingSettingIsAReportedFailure()
    {
        var target = SeedTargets.All.Single(t => t.Name == "sqlserver");

        Assert.False(target.Default);
        Assert.Contains("DEMO_SQLSERVER", target.Hint);
        Assert.Contains("--profile sqlserver", target.Hint);
    }

    [Fact]
    public async Task Seed_WhenTheSettingIsMissing_ReportsItForThatProviderAndExits2()
    {
        var failing = new SeedTarget(
            "sqlserver",
            _ => throw new SeedSettingException("DEMO_SQLSERVER is not set."),
            count => $"{count}",
            "hint for sqlserver",
            Default: false);
        using var output = new StringWriter();
        using var error = new StringWriter();

        var exit = await SeedCommand.RunAsync(["sqlserver"], [failing], output, error);

        Assert.Equal(2, exit);
        Assert.Contains("DEMO_SQLSERVER is not set.", error.ToString());
        Assert.Contains("hint for sqlserver", error.ToString());
    }

    // ---- the seeder (needs the engine, uses a scratch database) -----------------------------------------

    // The seeder needs rights to create a database: the administrator's string (DEMO_SQLSERVER_ADMIN), not the read-only one the API uses.
    private const string Admin = DemoSettings.SqlServerAdminEnvVar;

    [RequiresEngineFact(Admin)]
    public async Task Seeder_CreatesTheDatabase_AndStoresTheLibrarysTypesAndEveryRow()
    {
        var database = $"demo_seedtest_{Guid.NewGuid():N}"[..28];
        var scratch = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Admin)) { InitialCatalog = database };
        try
        {
            var rows = OrderGenerator.Generate();
            // Twice: the first run creates the database, the second drops and recreates the table.
            Assert.Equal(rows.Count, await SqlServerSeeder.SeedAsync(scratch.ConnectionString, rows));
            Assert.Equal(rows.Count, await SqlServerSeeder.SeedAsync(scratch.ConnectionString, rows));

            await using var connection = new SqlConnection(scratch.ConnectionString);
            await connection.OpenAsync();

            var collations = new Dictionary<string, string?>();
            await using (var columns = new SqlCommand(
                "SELECT c.name, c.collation_name, t.name, c.scale FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
                "WHERE c.object_id = OBJECT_ID('orders')", connection))
            await using (var reader = await columns.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    var type = reader.GetString(2);
                    collations[reader.GetString(0)] = reader.IsDBNull(1) ? $"<{type}{(type == "datetime2" ? reader.GetByte(3) : "")}>" : reader.GetString(1);
                }
            }

            Assert.Equal("Latin1_General_100_BIN2", collations["country"]);
            Assert.Equal("Latin1_General_100_BIN2", collations["category"]);
            Assert.Equal("Latin1_General_100_BIN2", collations["status"]);
            Assert.NotEqual("Latin1_General_100_BIN2", collations["customer"]);
            Assert.Equal("<datetime23>", collations["ordered_at"]);
            Assert.Equal("<date>", collations["shipped_on"]);

            await using var facts = new SqlCommand(
                "SELECT COUNT_BIG(*), SUM(CASE WHEN customer = N'Müller, Söhne & Co' THEN 1 ELSE 0 END), SUM(amount), MIN(ordered_at), MAX(ordered_at) FROM orders", connection);
            await using var result = await facts.ExecuteReaderAsync();
            Assert.True(await result.ReadAsync());
            Assert.Equal(rows.Count, result.GetInt64(0));
            Assert.True(result.GetInt32(1) > 0, "the non-ASCII customer is seeded, not filtered out");
            Assert.Equal(rows.Sum(row => row.Amount ?? 0), result.GetDecimal(2));
            Assert.Equal(rows.Min(row => row.OrderedAt), result.GetDateTime(3));
            Assert.Equal(rows.Max(row => row.OrderedAt), result.GetDateTime(4));
        }
        finally
        {
            await DropAsync(database);
        }
    }

    // ---- least privilege (#2525): the API's login can read and nothing else ------------------------------

    [RequiresEngineFact(Admin)]
    public async Task SeedingWithAnAdminString_CreatesAReadOnlyLogin_ThatCanSelectButNotWriteOrDrop()
    {
        var database = $"demo_readtest_{Guid.NewGuid():N}"[..28];
        var admin = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Admin)) { InitialCatalog = database };
        var readerName = $"rdr_{Guid.NewGuid():N}"[..20];
        var reader = new SqlConnectionStringBuilder(admin.ConnectionString)
        {
            UserID = readerName,
            Password = $"Rd-{Guid.NewGuid():N}-aZ9",
            InitialCatalog = database
        };
        try
        {
            var rows = OrderGenerator.Generate();
            // Twice: the second run finds the login and the user already there and must leave them working.
            Assert.Equal(rows.Count, await SqlServerSeeder.SeedAsync(reader.ConnectionString, rows, admin.ConnectionString));
            Assert.Equal(rows.Count, await SqlServerSeeder.SeedAsync(reader.ConnectionString, rows, admin.ConnectionString));

            await using var connection = new SqlConnection(reader.ConnectionString);
            await connection.OpenAsync();

            await using (var select = new SqlCommand("SELECT COUNT_BIG(*) FROM orders", connection))
            {
                Assert.Equal((long)rows.Count, (long)(await select.ExecuteScalarAsync())!);
            }

            foreach (var forbidden in new[]
            {
                "INSERT INTO orders (id, order_no, status, ordered_at, is_priority) VALUES (-1, N'x', N'x', SYSUTCDATETIME(), 0)",
                "UPDATE orders SET status = N'x'",
                "DELETE FROM orders",
                "DROP TABLE orders",
                "CREATE TABLE intruder (id INT)"
            })
            {
                await using var command = new SqlCommand(forbidden, connection);
                var denied = await Assert.ThrowsAsync<SqlException>(() => command.ExecuteNonQueryAsync());
                Assert.Contains(denied.Number, new[] { 229, 262, 3701 }); // permission denied on the object / on the database / a drop without the right (3701)
            }
        }
        finally
        {
            await DropAsync(database);
            await DropLoginAsync(readerName);
        }
    }

    [Fact]
    public async Task SeedingWithAnAdminString_RefusesAReaderWithoutAPasswordOrNamedLikeTheAdmin_BeforeConnecting()
    {
        const string admin = "Server=127.0.0.1,1;User Id=adm;Password=x;Database=demo;ConnectTimeout=1";
        var rows = OrderGenerator.Generate();

        var noPassword = await Assert.ThrowsAsync<SeedSettingException>(() =>
            SqlServerSeeder.SeedAsync("Server=127.0.0.1,1;User Id=rdr;Database=demo", rows, admin));
        Assert.Contains("DEMO_SQLSERVER needs a User Id and a Password", noPassword.Message);

        var same = await Assert.ThrowsAsync<SeedSettingException>(() =>
            SqlServerSeeder.SeedAsync("Server=127.0.0.1,1;User Id=ADM;Password=y;Database=demo", rows, admin));
        Assert.Contains("same login", same.Message);
    }

    [RequiresEngineFact(Admin)]
    public async Task SeedingWithAnAdminString_RefusesToReKeyAnExistingLoginThatHoldsMoreThanRead()
    {
        var database = $"demo_guardtest_{Guid.NewGuid():N}"[..28];
        var admin = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Admin)) { InitialCatalog = database };
        var powerful = $"pwr_{Guid.NewGuid():N}"[..20];
        var master = new SqlConnectionStringBuilder(admin.ConnectionString) { InitialCatalog = "master" };
        try
        {
            await using (var connection = new SqlConnection(master.ConnectionString))
            {
                await connection.OpenAsync();
                await using var create = new SqlCommand(
                    $"CREATE LOGIN [{powerful}] WITH PASSWORD = N'Pw-{Guid.NewGuid():N}-aZ9', CHECK_POLICY = OFF; ALTER SERVER ROLE [dbcreator] ADD MEMBER [{powerful}];",
                    connection);
                await create.ExecuteNonQueryAsync();
            }

            var reader = new SqlConnectionStringBuilder(admin.ConnectionString) { UserID = powerful, Password = "Other-Pw-1aZ9" };
            var refused = await Assert.ThrowsAsync<SeedSettingException>(() =>
                SqlServerSeeder.SeedAsync(reader.ConnectionString, OrderGenerator.Generate(), admin.ConnectionString));
            Assert.Contains("server role", refused.Message);
            Assert.DoesNotContain("Other-Pw-1aZ9", refused.Message);
        }
        finally
        {
            await DropAsync(database);
            await DropLoginAsync(powerful);
        }
    }

    private static async Task DropLoginAsync(string login)
    {
        var master = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Admin)) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"IF SUSER_ID(N'{login}') IS NOT NULL DROP LOGIN [{login}]", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task DropAsync(string database)
    {
        SqlConnection.ClearAllPools();
        var master = new SqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Admin)) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            $"IF DB_ID(N'{database}') IS NOT NULL BEGIN ALTER DATABASE [{database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{database}]; END", connection);
        await command.ExecuteNonQueryAsync();
    }
}
