using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Craticula.Sql;
using Demo.Api.Errors;
using Demo.Api.Grid;
using Demo.Api.Seed;
using MySqlConnector;

namespace Demo.Api.Tests;

/// <summary>
/// The <c>mysql</c> provider, an optional engine. The comparisons with <c>postgres</c> (the baseline) run only when
/// <c>DEMO_MYSQL</c> is set and the engine answers (<see cref="RequiresEngineFactAttribute"/>); the tests that need no engine
/// (a closed port, the query view, the binding, the classification) always run. The documented differences, measured on
/// MySQL 8.4.11: <c>AVG</c> over a BIGINT column returns four more decimals than an integer (<c>div_precision_increment</c>, the
/// baseline value rounded half up to 4 decimals), AVG over the decimal column has six decimals (inside smoke.sh's 1e-6 relative
/// tolerance), and the stock collation is accent-insensitive, so a contains-filter for "sohne" finds "Söhne". Everything else is exact.
/// </summary>
[Collection("api-mysql")]
// Acceptance ids (docs/feat-breakdown/2494): AC-1 rows, AC-2 groups and drill-down, AC-3 CSV, AC-6 typed 4xx; the per-AC method list is in docs/feat/2500/validation.md.
public sealed class MySqlProviderTests(MySqlApiFixture api)
{
    private const string Engine = DemoSettings.MySqlEnvVar;

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task<JsonElement> RowsOk(string provider, string body)
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, provider, "rows", body);
        Assert.True(HttpStatusCode.OK == status, $"{provider}: {status} {json}");
        return json;
    }

    /// <summary>Numbers by value (12877.0 equals 12877). Keys ending <c>quantity_avg</c> must equal the baseline rounded half up to 4
    /// decimals (MySQL's AVG over an integer type); other <c>_avg</c> keys compare with a relative 1e-6; everything else is exact.</summary>
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
                return other.GetDecimal() == RoundedToFour(baseline);
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

    /// <summary>The baseline as MySQL rounds a decimal average: half up, to four decimals.</summary>
    private static decimal RoundedToFour(JsonElement baseline) =>
        decimal.Round(decimal.Parse(baseline.GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture), 4, MidpointRounding.AwayFromZero);

    private async Task AssertRowsEqualPostgres(string body)
    {
        var pg = await RowsOk("postgres", body);
        var sql = await RowsOk("mysql", body);

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

    private static string ClosedPortConnectionString(string extra = "Connection Timeout=1") =>
        $"Server=127.0.0.1;Port={ApiHostFixture.FreePort()};User ID=demo;Password=unused;Database=demo;{extra}";

    /// <summary>The live engine's connection string with a password the server does not know.</summary>
    private static string WithAWrongPassword() =>
        new MySqlConnectionStringBuilder(Environment.GetEnvironmentVariable(Engine)!) { Password = "nope", Pooling = false }.ConnectionString;

    /// <summary>A host whose <c>mysql</c> points at a port nothing listens on: it needs no container.</summary>
    private static Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client)> StartWithClosedEngineAsync() =>
        ApiHostFixture.StartAsync(null, builder => builder.Configuration[DemoSettings.MySqlEnvVar] = ClosedPortConnectionString());

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
        // DATETIME has no zone; the library's Dapper adapter reads it as UTC (SEM-4, #2511), or the JSON would drop the Z every other provider sends.
        var page = await RowsOk("mysql", Requests.PageSize(5));

        Assert.All(page.GetProperty("rows").EnumerateArray(), row =>
            Assert.EndsWith("Z", row.GetProperty("orderedAt").GetString(), StringComparison.Ordinal));
    }

    [RequiresEngineFact(Engine)]
    public async Task Booleans_AreJsonBooleans()
    {
        var page = await RowsOk("mysql", Requests.Boolean);

        Assert.All(page.GetProperty("rows").EnumerateArray(), row => Assert.Equal(JsonValueKind.True, row.GetProperty("isPriority").ValueKind));
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
    public async Task GroupBy_AverageOfAWholeNumberColumn_IsThePostgresValueRoundedHalfUpToFourDecimals(string column)
    {
        var body = Requests.GroupByWithQuantityAvg(column);
        var pg = (await RowsOk("postgres", body)).GetProperty("rows");
        var sql = (await RowsOk("mysql", body)).GetProperty("rows");

        Assert.Equal(pg.GetArrayLength(), sql.GetArrayLength());
        for (var i = 0; i < pg.GetArrayLength(); i++)
        {
            var baseline = decimal.Parse(pg[i].GetProperty("quantity_avg").GetRawText(), NumberStyles.Float, CultureInfo.InvariantCulture);
            // The demo data averages about 25.xx with a long fraction: more digits than MySQL's four.
            Assert.NotEqual(decimal.Round(baseline, 4), baseline);
            Assert.Equal(RoundedToFour(pg[i].GetProperty("quantity_avg")), sql[i].GetProperty("quantity_avg").GetDecimal());
        }

        await AssertRowsEqualPostgres(body);
    }

    [RequiresEngineFact(Engine)]
    public async Task SetValues_OfAGroupableColumn_AreTheSameBucketsAsPostgres()
    {
        // The stock accent-insensitive collation would merge accent variants; the groupable columns are utf8mb4_bin, so the distinct
        // values are exactly PostgreSQL's.
        var pg = await ApiHostFixture.PostJsonAsync(api.Client, "postgres", "values", Requests.CountryValues);
        var sql = await ApiHostFixture.PostJsonAsync(api.Client, "mysql", "values", Requests.CountryValues);

        string[] Canon(JsonElement json) => [.. json.GetProperty("values").EnumerateArray().Select(v => v.GetRawText()).Order(StringComparer.Ordinal)];
        Assert.Equal(Canon(pg.Body), Canon(sql.Body));
    }

    [RequiresEngineFact(Engine)]
    public async Task DrillDownIntoACountry_EqualsPostgres()
    {
        await AssertRowsEqualPostgres(Requests.DrillDownGermany);

        var page = await RowsOk("mysql", Requests.DrillDownGermany);
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
        var sql = await ApiHostFixture.PostJsonAsync(api.Client, "mysql", "values", body);

        // Date and datetime values are bound as strings (the adapter's way): MySQL converts them on comparison.
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
        var sql = await ApiHostFixture.PostRawAsync(api.Client, "mysql", "export.csv", Requests.StatusExport);

        Assert.Equal(HttpStatusCode.OK, sql.Status);
        Assert.StartsWith("text/csv", sql.ContentType);
        Assert.True(sql.Bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM");
        var left = ParseCsv(pg.Bytes);
        var right = ParseCsv(sql.Bytes);
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

    /// <summary>The whole table through <c>DapperExportStream</c> (more than one keyset chunk).</summary>
    [RequiresEngineFact(Engine)]
    public async Task Csv_WholeTableExport_HasEveryRowOnceInKeyOrder()
    {
        var (status, _, _, bytes) = await ApiHostFixture.PostRawAsync(api.Client, "mysql", "export.csv", Requests.FullExport);

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
        var (status, _, _, bytes) = await ApiHostFixture.PostRawAsync(api.Client, "mysql", "export.csv", Requests.FormulaCustomer);

        Assert.Equal(HttpStatusCode.OK, status);
        var records = ParseCsv(bytes);
        Assert.True(records.Count >= 2, "the =SUM(A1) customer was not exported");
        Assert.All(records.Skip(1), r => Assert.Equal("'=SUM(A1)", r[2]));
    }

    // ---- text semantics, measured -----------------------------------------------------------------------

    /// <summary>
    /// Measured on MySQL 8.4.11 with the seed's collations (table default <c>utf8mb4_0900_ai_ci</c>): a contains-filter compiles to a bare
    /// <c>LIKE</c>, so the column's collation decides. "SÖHNE", "söhne" and "Söhne" find "Müller, Söhne &amp; Co" as on PostgreSQL, and so
    /// do the accent-less "sohne" and "SOHNE", which PostgreSQL's case-insensitive but accent-sensitive collation does not match. That
    /// is the documented difference: the stock MySQL collation is accent-insensitive.
    /// </summary>
    [RequiresEngineFact(Engine)]
    public async Task NonAsciiContains_FindsTheCustomerInAnyCase_AndMySqlAlsoIgnoresTheAccent()
    {
        const string customer = "Müller, Söhne & Co";
        bool Finds(JsonElement page) => page.GetProperty("rows").EnumerateArray().Any(r => r.GetProperty("customer").GetString() == customer);
        static string Contains(string text) => Requests.FirstPage($$"""{ "customer": { "filterType": "text", "type": "contains", "filter": "{{text}}" } }""");

        foreach (var text in new[] { "SÖHNE", "söhne", "Söhne" })
        {
            Assert.True(Finds(await RowsOk("postgres", Contains(text))), $"postgres {text}");
            Assert.True(Finds(await RowsOk("mysql", Contains(text))), $"mysql {text}");
        }

        foreach (var text in new[] { "sohne", "SOHNE" })
        {
            Assert.False(Finds(await RowsOk("postgres", Contains(text))), $"postgres {text} (accent-sensitive)");
            Assert.True(Finds(await RowsOk("mysql", Contains(text))), $"mysql {text} (accent-insensitive)");
        }
    }

    // ---- compile only and errors (no engine needed) ---------------------------------------------------

    [Fact]
    public async Task Query_ReturnsParameterizedMysqlSqlAndNeverTheLiteralValue()
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var (status, json) = await ApiHostFixture.PostJsonAsync(client, "mysql", "query", Requests.NeedleFilter);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("sql", json.GetProperty("kind").GetString());
            Assert.Equal("mysql", json.GetProperty("dialect").GetString());
            var sql = json.GetProperty("text").GetString()!;
            Assert.Contains("?", sql);
            Assert.DoesNotContain("COLLATE", sql);
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
            var (status, json) = await ApiHostFixture.PostJsonAsync(client, "mysql", "rows", Requests.UnknownColumn);

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
            using var response = await client.PostAsync($"/api/mysql/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
            var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("backend-unavailable", json.GetProperty("error").GetProperty("kind").GetString());
        }
    }

    [Fact]
    public async Task AServerThatNeverAnswers_WithoutAConnectTimeoutInTheString_Is503AfterTheServicesFiveSeconds()
    {
        // The driver's own default is 15 s; the service sets 5 s when the string says nothing. A listener that never accepts leaves the
        // handshake unanswered: the connect timeout expires (MySqlException 1042, "Connect Timeout expired.").
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var (app, client) = await ApiHostFixture.StartAsync(null, builder =>
                builder.Configuration[DemoSettings.MySqlEnvVar] = $"Server=127.0.0.1;Port={port};User ID=demo;Password=unused");
            await using (app)
            using (client)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var (status, _) = await ApiHostFixture.PostJsonAsync(client, "mysql", "rows", Requests.PageSize(10));

                Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
                Assert.InRange(clock.Elapsed.TotalSeconds, 3, 10);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Health_ListsMySqlOnlyWhenItsSettingIsPresent()
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var json = JsonDocument.Parse(await client.GetStringAsync("/api/health")).RootElement;
            Assert.Equal(
                ["ef", "ef-mysql", "ef-sqlite", "mongo", "mysql", "postgres", "sqlite"],
                json.GetProperty("providers").EnumerateArray().Select(p => p.GetString()!));
        }
    }

    // ---- engine failures, the binding and the seed target (no engine needed) ---------------------------

    [Theory]
    [InlineData(1042, true)]
    [InlineData(1045, false)]
    [InlineData(1044, false)]
    [InlineData(1049, false)]
    [InlineData(1146, false)]
    [InlineData(1064, false)]
    [InlineData(1054, false)]
    public void EngineFailures_ListsTheMySqlConnectionNumber_AndNoLoginOrStatementErrors(int number, bool unavailable) =>
        Assert.Equal(unavailable, EngineFailures.MySqlConnectionFailureNumbers.Contains(number));

    [Fact]
    public async Task EngineFailures_ARealRefusedConnection_IsUnavailable()
    {
        await using var connection = new MySqlConnection(ClosedPortConnectionString());

        var failure = await Assert.ThrowsAsync<MySqlException>(() => connection.OpenAsync());

        Assert.Equal(1042, failure.Number);
        Assert.True(EngineFailures.IsUnavailable(failure), $"MySqlException number {failure.Number} is not declared");
    }

    [RequiresEngineFact(Engine)]
    public async Task EngineFailures_ARefusedLogin_StaysAnOrdinaryFailure()
    {
        await using var connection = new MySqlConnection(WithAWrongPassword());

        var failure = await Assert.ThrowsAsync<MySqlException>(() => connection.OpenAsync());

        Assert.Equal(1045, failure.Number);
        Assert.False(EngineFailures.IsUnavailable(failure));
    }

    [RequiresEngineFact(Engine)]
    public async Task EngineFailures_AStatementError_StaysAnOrdinaryFailure()
    {
        await using var connection = new MySqlConnection(Environment.GetEnvironmentVariable(Engine));
        await connection.OpenAsync();
        await using var command = new MySqlCommand("SELECT * FROM no_such_table_in_demo", connection);

        var failure = await Assert.ThrowsAsync<MySqlException>(() => command.ExecuteScalarAsync());

        Assert.Equal(1146, failure.Number);
        Assert.False(EngineFailures.IsUnavailable(failure));
    }

    [Fact]
    public void CreateCommand_BindsEveryParameterByOrderUnderP0ToPn_AndRefusesAList()
    {
        var query = new SqlQuery
        {
            Sql = "SELECT 1 WHERE `a` = ? AND `b` IN (?, ?)",
            Dialect = "mysql",
            Parameters = ["x", 7L, null]
        };
        using var connection = new MySqlConnection();

        using var command = MySqlBinding.CreateCommand(connection, query);

        Assert.Equal(query.Sql, command.CommandText);
        Assert.Equal(["p0", "p1", "p2"], command.Parameters.Cast<MySqlParameter>().Select(p => p.ParameterName));
        Assert.Equal("x", command.Parameters["p0"].Value);
        Assert.Equal(7L, command.Parameters["p1"].Value);
        Assert.Equal(DBNull.Value, command.Parameters["p2"].Value);
        Assert.Throws<NotSupportedException>(() =>
            MySqlBinding.CreateCommand(connection, query with { Parameters = [new List<object?> { 1L }] }));
    }

    [Fact]
    public void TheMySqlSeedTarget_IsNotADefault_AndTheHintNamesTheProfileAndTheSetting()
    {
        var target = SeedTargets.All.Single(t => t.Name == "mysql");

        Assert.False(target.Default);
        Assert.Contains("DEMO_MYSQL", target.Hint);
        Assert.Contains("--profile mysql", target.Hint);
    }

    [Fact]
    public async Task Seed_WithTheSettingUnset_ReportsItForMySqlAndExits2()
    {
        var saved = Environment.GetEnvironmentVariable(Engine);
        Environment.SetEnvironmentVariable(Engine, null);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await SeedCommand.RunAsync(["mysql"], SeedTargets.All, output, error);

            Assert.Equal(2, exit);
            Assert.Contains("DEMO_MYSQL is not set.", error.ToString());
            Assert.Contains("--profile mysql", error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Engine, saved);
        }
    }

    [Fact]
    public async Task Seed_WithAnEngineThatIsNotListening_IsAReportedFailureWithTheHint()
    {
        var saved = Environment.GetEnvironmentVariable(Engine);
        Environment.SetEnvironmentVariable(Engine, ClosedPortConnectionString());
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await SeedCommand.RunAsync(["mysql"], SeedTargets.All, output, error);

            Assert.Equal(2, exit);
            Assert.Contains("Seed failed for mysql", error.ToString());
            Assert.Contains("--profile mysql", error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Engine, saved);
        }
    }

    [RequiresEngineFact(Engine)]
    public async Task Seed_WithARefusedLogin_IsAReportedFailureNotACrash()
    {
        var saved = Environment.GetEnvironmentVariable(Engine);
        Environment.SetEnvironmentVariable(Engine, WithAWrongPassword());
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await SeedCommand.RunAsync(["mysql"], SeedTargets.All, output, error);

            Assert.Equal(2, exit);
            Assert.Contains("Seed failed for mysql", error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Engine, saved);
        }
    }

    // ---- the seeder (needs the engine; same collection, so no test reads the table while it is rebuilt) ------

    [RequiresEngineFact(Engine)]
    public async Task Seeder_StoresTheLibrarysTypesTheCollationsAndEveryRow()
    {
        var setting = Environment.GetEnvironmentVariable(Engine)!;
        var rows = OrderGenerator.Generate();
        // Twice: the second run drops and recreates the table.
        Assert.Equal(rows.Count, await MySqlSeeder.SeedAsync(setting, rows));
        Assert.Equal(rows.Count, await MySqlSeeder.SeedAsync(setting, rows));

        await using var connection = new MySqlConnection(setting);
        await connection.OpenAsync();

        var columns = new Dictionary<string, (string? Collation, string Type, string Nullable)>();
        await using (var info = new MySqlCommand(
            "SELECT COLUMN_NAME, COLLATION_NAME, COLUMN_TYPE, IS_NULLABLE FROM information_schema.COLUMNS WHERE TABLE_SCHEMA = DATABASE() AND TABLE_NAME = 'orders'",
            connection))
        await using (var reader = await info.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                columns[reader.GetString(0)] = (reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3));
            }
        }

        Assert.Equal("utf8mb4_bin", columns["country"].Collation);
        Assert.Equal("utf8mb4_bin", columns["category"].Collation);
        Assert.Equal("utf8mb4_bin", columns["status"].Collation);
        Assert.Equal("utf8mb4_0900_ai_ci", columns["customer"].Collation);
        Assert.Equal("utf8mb4_0900_ai_ci", columns["order_no"].Collation);
        Assert.Equal("datetime(3)", columns["ordered_at"].Type);
        Assert.Equal("date", columns["shipped_on"].Type);
        Assert.Equal("decimal(12,2)", columns["amount"].Type);
        Assert.Equal("bigint", columns["quantity"].Type);
        Assert.Equal("NO", columns["status"].Nullable);
        Assert.Equal("NO", columns["order_no"].Nullable);
        Assert.Equal("NO", columns["is_priority"].Nullable);
        Assert.Equal("YES", columns["customer"].Nullable);

        await using var facts = new MySqlCommand(
            "SELECT COUNT(*), SUM(customer = 'Müller, Söhne & Co'), SUM(amount), MIN(ordered_at), MAX(ordered_at) FROM orders", connection);
        await using var result = await facts.ExecuteReaderAsync();
        Assert.True(await result.ReadAsync());
        Assert.Equal(rows.Count, result.GetInt64(0));
        Assert.True(result.GetInt32(1) > 0, "the non-ASCII customer is seeded, not filtered out");
        Assert.Equal(rows.Sum(row => row.Amount ?? 0), result.GetDecimal(2));
        Assert.Equal(rows.Min(row => row.OrderedAt), result.GetDateTime(3));
        Assert.Equal(rows.Max(row => row.OrderedAt), result.GetDateTime(4));
    }
}
