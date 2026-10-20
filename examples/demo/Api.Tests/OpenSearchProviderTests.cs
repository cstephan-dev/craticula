using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Craticula.OpenSearch.Client;
using Demo.Api.Errors;
using Demo.Api.Grid;
using Demo.Api.Seed;
using OpenSearch.Net;

namespace Demo.Api.Tests;

/// <summary>
/// The <c>opensearch</c> provider, an optional engine. The comparisons with <c>postgres</c> (the baseline) run only when
/// <c>DEMO_OPENSEARCH</c> is set and the engine answers (<see cref="RequiresEngineFactAttribute"/>); the tests that need no engine (a
/// closed port, the query view, the unsupported shapes, the classification) always run. The documented differences, measured on
/// OpenSearch 2.19.1 with the index the seeder builds: amounts are <c>double</c> (the sums of the demo data are exactly PostgreSQL's,
/// the averages differ in the last digits, inside smoke.sh's 1e-6 relative tolerance); the row count above 10,000 is unknown
/// (<c>lastRow</c> is null, PERF-3); the null group of a grouped level sits at the other end (OpenSearch treats null as the lowest
/// value, PostgreSQL as the highest, AGG-5/OSE-11(b)); the distinct values leave out null; a contains-filter folds ASCII case only
/// (a typed capital accented letter misses the stored lowercase one); two grouped shapes are unsupported.
/// Everything else is exact, including case-insensitive sort and the deep pages past 10,000 rows.
/// </summary>
[Collection("api-opensearch")]
// Acceptance ids (docs/feat-breakdown/2494): AC-1 rows, AC-2 groups and drill-down, AC-3 CSV, AC-6 typed 4xx; the per-AC method list is in docs/feat/2501/validation.md.
public sealed class OpenSearchProviderTests(OpenSearchApiFixture api)
{
    private const string Engine = DemoSettings.OpenSearchEnvVar;

    // ---- helpers -----------------------------------------------------------------------------------

    private async Task<JsonElement> RowsOk(string provider, string body)
    {
        var (status, json) = await ApiHostFixture.PostJsonAsync(api.Client, provider, "rows", body);
        Assert.True(HttpStatusCode.OK == status, $"{provider}: {status} {json}");
        return json;
    }

    /// <summary>Numbers by value (12877.0 equals 12877); keys ending <c>_avg</c> compare with a relative 1e-6; everything else is exact.</summary>
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

    /// <summary>The rows equal PostgreSQL's one by one. <c>lastRow</c> is equal too, except that OpenSearch does not know a count above
    /// 10,000 (the adapter caps the count there, PERF-3) and answers null where PostgreSQL knows it; the last block of the table is short,
    /// so there the grid learns the end and the number is equal.</summary>
    private async Task AssertRowsEqualPostgres(string body)
    {
        var pg = await RowsOk("postgres", body);
        var os = await RowsOk("opensearch", body);

        AssertLastRow(pg, os);
        var pgRows = pg.GetProperty("rows");
        var osRows = os.GetProperty("rows");
        Assert.Equal(pgRows.GetArrayLength(), osRows.GetArrayLength());
        for (var i = 0; i < pgRows.GetArrayLength(); i++)
        {
            Assert.True(Same(pgRows[i], osRows[i]), $"row {i}: {pgRows[i]} vs {osRows[i]}");
        }
    }

    private static void AssertLastRow(JsonElement pg, JsonElement os)
    {
        var expected = pg.GetProperty("lastRow");
        var actual = os.GetProperty("lastRow");
        if (expected.ValueKind == JsonValueKind.Number && expected.GetInt64() > 10_000 && actual.ValueKind == JsonValueKind.Null)
        {
            return;
        }

        Assert.Equal(expected.GetRawText(), actual.GetRawText());
    }

    /// <summary>Grouped rows: the group whose key is null is the same row at the other end (PostgreSQL: last when ascending, first when
    /// descending; OpenSearch the reverse); every other row, in order, and the count are equal.</summary>
    private async Task AssertGroupsEqualPostgres(string body)
    {
        var pg = await RowsOk("postgres", body);
        var os = await RowsOk("opensearch", body);

        Assert.Equal(pg.GetProperty("lastRow").GetRawText(), os.GetProperty("lastRow").GetRawText());
        var (pgNull, pgRest) = SplitNullGroup(pg.GetProperty("rows"));
        var (osNull, osRest) = SplitNullGroup(os.GetProperty("rows"));
        Assert.Equal(pgNull.Count, osNull.Count);
        for (var i = 0; i < pgNull.Count; i++)
        {
            Assert.True(Same(pgNull[i], osNull[i]), $"null group: {pgNull[i]} vs {osNull[i]}");
        }

        Assert.Equal(pgRest.Count, osRest.Count);
        for (var i = 0; i < pgRest.Count; i++)
        {
            Assert.True(Same(pgRest[i], osRest[i]), $"group {i}: {pgRest[i]} vs {osRest[i]}");
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

    private static string ClosedPortUrl() => $"http://127.0.0.1:{ApiHostFixture.FreePort()}";

    /// <summary>A host whose <c>opensearch</c> points at a port nothing listens on: it needs no container.</summary>
    private static Task<(Microsoft.AspNetCore.Builder.WebApplication App, HttpClient Client)> StartWithClosedEngineAsync() =>
        ApiHostFixture.StartAsync(null, builder => builder.Configuration[DemoSettings.OpenSearchEnvVar] = ClosedPortUrl());

    private static async Task<JsonElement> ErrorBody(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("error").Clone();

    private static string GroupBody(string[] columns, string[] keys, string sortColId, string direction = "asc", int start = 0, int end = 100, string aggs = "sum-avg-count")
    {
        var cols = string.Join(",", columns.Select(c => $$"""{ "id": "{{c}}", "displayName": "{{c}}", "field": "{{c}}" }"""));
        var groupKeys = string.Join(",", keys.Select(k => k == "null" ? """{ "type": "null" }""" : $$"""{ "type": "string", "value": "{{k}}" }"""));
        var values = aggs switch
        {
            "sum-avg-count" => """
                { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "sum" },
                { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "avg" },
                { "id": "quantity", "displayName": "Quantity", "field": "quantity", "aggFunc": "count" }
                """,
            "all" => """
                { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "sum" },
                { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "min" },
                { "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "max" },
                { "id": "quantity", "displayName": "Quantity", "field": "quantity", "aggFunc": "avg" },
                { "id": "quantity", "displayName": "Quantity", "field": "quantity", "aggFunc": "sum" },
                { "id": "quantity", "displayName": "Quantity", "field": "quantity", "aggFunc": "count" }
                """,
            "sum-only" => """{ "id": "amount", "displayName": "Amount", "field": "amount", "aggFunc": "sum" }, { "id": "quantity", "displayName": "Quantity", "field": "quantity", "aggFunc": "count" }""",
            _ => ""
        };
        return $$"""
            {
              "startRow": {{start}}, "endRow": {{end}},
              "rowGroupCols": [ {{cols}} ],
              "valueCols": [ {{values}} ], "pivotCols": [], "pivotMode": false, "groupKeys": [ {{groupKeys}} ],
              "filterModel": null,
              "sortModel": [ { "colId": "{{sortColId}}", "sort": "{{direction}}" } ]
            }
            """;
    }

    private static string SortedBy(string colId, string direction, int start = 0, int end = 100) => $$"""
        {
          "startRow": {{start}}, "endRow": {{end}}, "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": null,
          "sortModel": [ { "colId": "{{colId}}", "sort": "{{direction}}" } ]
        }
        """;

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

    /// <summary>The mapping's lowercase normalizer makes the sort case-insensitive; without it uppercase text would sort before lowercase.</summary>
    [RequiresEngineTheory(Engine)]
    [InlineData("customer", "asc")]
    [InlineData("customer", "desc")]
    [InlineData("orderNo", "asc")]
    [InlineData("country", "asc")]
    [InlineData("country", "desc")]
    [InlineData("amount", "desc")]
    [InlineData("shippedOn", "asc")]
    [InlineData("orderedAt", "desc")]
    public Task Sort_FirstPage_EqualsPostgres(string colId, string direction) => AssertRowsEqualPostgres(SortedBy(colId, direction));

    [RequiresEngineTheory(Engine)]
    [InlineData("customer", "asc")]
    [InlineData("amount", "desc")]
    public Task Sort_APageNearTheEnd_EqualsPostgres(string colId, string direction) => AssertRowsEqualPostgres(SortedBy(colId, direction, 9900, 10000));

    /// <summary>The 20,000 rows pass <c>max_result_window</c> (10,000): rows 10,000 to 10,100 come from a point in time and
    /// <c>search_after</c> through the shared cursor cache; the next block continues from the cached cursor.</summary>
    [RequiresEngineFact(Engine)]
    public async Task DeepPaging_PastTenThousandRows_EqualsPostgres()
    {
        await AssertRowsEqualPostgres(SortedBy("id", "asc", 10_000, 10_100));
        await AssertRowsEqualPostgres(SortedBy("id", "asc", 10_100, 10_200));
        await AssertRowsEqualPostgres(SortedBy("amount", "desc", 15_000, 15_100));
        await AssertRowsEqualPostgres(SortedBy("id", "asc", 19_950, 20_050));
    }

    /// <summary>A filter that matches fewer than 10,000 rows has an exact count; the whole table has 20,000, which OpenSearch reports as
    /// unknown (null) instead of a capped number (PERF-3): the grid keeps asking and ends on the first short block.</summary>
    [RequiresEngineFact(Engine)]
    public async Task RowCount_IsExactBelowTenThousand_AndUnknownAbove()
    {
        var filtered = await RowsOk("opensearch", Requests.TextNumberSort);
        Assert.Equal(
            (await RowsOk("postgres", Requests.TextNumberSort)).GetProperty("lastRow").GetInt64(),
            filtered.GetProperty("lastRow").GetInt64());

        var all = await RowsOk("opensearch", Requests.PageSize(50));
        Assert.Equal(JsonValueKind.Null, all.GetProperty("lastRow").ValueKind);
        Assert.Equal(50, all.GetProperty("rows").GetArrayLength());
    }

    [RequiresEngineFact(Engine)]
    public async Task Datetimes_AreSentAsUtcInstantsWithTheTrailingZ()
    {
        var page = await RowsOk("opensearch", Requests.PageSize(5));

        Assert.All(page.GetProperty("rows").EnumerateArray(), row =>
            Assert.EndsWith("Z", row.GetProperty("orderedAt").GetString(), StringComparison.Ordinal));
    }

    [RequiresEngineFact(Engine)]
    public async Task Booleans_AreJsonBooleans()
    {
        var page = await RowsOk("opensearch", Requests.Boolean);

        Assert.All(page.GetProperty("rows").EnumerateArray(), row => Assert.Equal(JsonValueKind.True, row.GetProperty("isPriority").ValueKind));
    }

    // ---- grouping and drill-down ---------------------------------------------------------------------

    [RequiresEngineTheory(Engine)]
    [InlineData("country", "asc")]
    [InlineData("country", "desc")]
    [InlineData("category", "asc")]
    [InlineData("status", "asc")]
    public async Task GroupBy_SumAvgCount_EqualPostgres_ExceptWhereTheNullGroupSits(string column, string direction)
    {
        var body = GroupBody([column], [], column, direction);
        Assert.True((await RowsOk("postgres", body)).GetProperty("rows").GetArrayLength() >= 3);

        await AssertGroupsEqualPostgres(body);
    }

    [RequiresEngineFact(Engine)]
    public async Task GroupBy_EveryAggregate_EqualPostgres()
    {
        var body = GroupBody(["country"], [], "country", aggs: "all");

        await AssertGroupsEqualPostgres(body);
    }

    [RequiresEngineFact(Engine)]
    public async Task GroupBy_WithAFilter_EqualsPostgres()
    {
        var body = GroupBody(["country"], [], "country").Replace(
            "\"filterModel\": null",
            "\"filterModel\": { \"customer\": { \"filterType\": \"text\", \"type\": \"contains\", \"filter\": \"GmbH\" }, \"status\": { \"filterType\": \"set\", \"values\": [\"shipped\", \"paid\"] } }");

        await AssertGroupsEqualPostgres(body);
    }

    [RequiresEngineFact(Engine)]
    public async Task GroupBy_TheSumsOfTheDemoDataAreExactlyPostgresSums_AndTheAveragesAreClose()
    {
        // Amounts are double in the index, and OpenSearch sums in double precision; for the 20,000 demo rows (two decimals, below 1e8)
        // the sums come out exactly as PostgreSQL's numeric sums, which is what smoke.sh's exact comparison relies on. The averages
        // differ in the last digits (1e-15 relative), inside the 1e-6 rule.
        var body = GroupBody(["category"], [], "category");
        var pg = (await RowsOk("postgres", body)).GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("category").ValueKind != JsonValueKind.Null).ToArray();
        var os = (await RowsOk("opensearch", body)).GetProperty("rows").EnumerateArray().Where(r => r.GetProperty("category").ValueKind != JsonValueKind.Null).ToArray();

        Assert.Equal(pg.Length, os.Length);
        for (var i = 0; i < pg.Length; i++)
        {
            Assert.Equal(pg[i].GetProperty("amount_sum").GetDecimal(), os[i].GetProperty("amount_sum").GetDecimal());
            Assert.Equal(pg[i].GetProperty("quantity_count").GetInt64(), os[i].GetProperty("quantity_count").GetInt64());
        }
    }

    [RequiresEngineFact(Engine)]
    public async Task GroupBy_TheNullGroupIsTheLowestValue_FirstWhenAscendingLastWhenDescending()
    {
        var asc = (await RowsOk("opensearch", GroupBody(["country"], [], "country"))).GetProperty("rows");
        var desc = (await RowsOk("opensearch", GroupBody(["country"], [], "country", "desc"))).GetProperty("rows");

        Assert.Equal(JsonValueKind.Null, asc[0].GetProperty("country").ValueKind);
        Assert.Equal(JsonValueKind.Null, desc[desc.GetArrayLength() - 1].GetProperty("country").ValueKind);
    }

    [RequiresEngineFact(Engine)]
    public async Task GroupBy_TheSecondLevelUnderACountry_EqualsPostgres()
    {
        await AssertGroupsEqualPostgres(GroupBody(["country", "category"], ["Germany"], "category"));
        await AssertGroupsEqualPostgres(GroupBody(["country", "category"], ["Germany"], "category", "desc"));
    }

    /// <summary>Blocks after the first one go through the composite aggregation's <c>after</c> key kept in the shared cache: the blocks of
    /// three groups, read in order and then newest first, add up to the whole level.</summary>
    [RequiresEngineTheory(Engine)]
    [InlineData("asc")]
    [InlineData("desc")]
    public async Task GroupBy_BlocksAfterTheFirst_AddUpToTheWholeLevel(string direction)
    {
        var whole = (await RowsOk("opensearch", GroupBody(["country"], [], "country", direction))).GetProperty("rows").EnumerateArray().Select(r => r.GetRawText()).ToArray();
        Assert.Equal(11, whole.Length);

        var inOrder = new List<string>();
        foreach (var start in new[] { 0, 3, 6, 9 })
        {
            var block = await RowsOk("opensearch", GroupBody(["country"], [], "country", direction, start, start + 3));
            inOrder.AddRange(block.GetProperty("rows").EnumerateArray().Select(r => r.GetRawText()));
            Assert.Equal(11, block.GetProperty("lastRow").GetInt64());
        }

        Assert.Equal(whole, inOrder);

        var newestFirst = new List<string>();
        foreach (var start in new[] { 9, 6, 3, 0 })
        {
            var block = await RowsOk("opensearch", GroupBody(["country"], [], "country", direction, start, start + 3));
            newestFirst.InsertRange(0, block.GetProperty("rows").EnumerateArray().Select(r => r.GetRawText()));
        }

        Assert.Equal(whole, newestFirst);
    }

    [RequiresEngineFact(Engine)]
    public async Task DrillDownIntoACountry_EqualsPostgres()
    {
        await AssertRowsEqualPostgres(Requests.DrillDownGermany);

        var page = await RowsOk("opensearch", Requests.DrillDownGermany);
        Assert.NotEmpty(page.GetProperty("rows").EnumerateArray());
        Assert.All(page.GetProperty("rows").EnumerateArray(), row => Assert.Equal("Germany", row.GetProperty("country").GetString()));
    }

    // ---- set values --------------------------------------------------------------------------------

    /// <summary>The distinct values leave out null: a terms aggregation has no bucket for a missing value. The page lists strings only.</summary>
    [RequiresEngineTheory(Engine)]
    [InlineData("plain")]
    [InlineData("status filter")]
    [InlineData("date filters")]
    [InlineData("datetime range")]
    public async Task SetValues_EqualPostgres_WithoutTheNull(string kind)
    {
        var body = kind switch
        {
            "plain" => Requests.CountryValues,
            "status filter" => Requests.FilteredCountryValues,
            "date filters" => Requests.DateFilteredCountryValues,
            _ => Requests.DatetimeFilteredCountryValues
        };
        var pg = await ApiHostFixture.PostJsonAsync(api.Client, "postgres", "values", body);
        var os = await ApiHostFixture.PostJsonAsync(api.Client, "opensearch", "values", body);

        Assert.Equal(HttpStatusCode.OK, os.Status);
        string[] Canon(JsonElement json) => [.. json.GetProperty("values").EnumerateArray().Where(v => v.ValueKind != JsonValueKind.Null).Select(v => v.GetRawText()).Order(StringComparer.Ordinal)];
        Assert.Equal(Canon(pg.Body), Canon(os.Body));
        Assert.DoesNotContain(os.Body.GetProperty("values").EnumerateArray(), v => v.ValueKind == JsonValueKind.Null);
        if (kind == "plain")
        {
            Assert.Contains(pg.Body.GetProperty("values").EnumerateArray(), v => v.ValueKind == JsonValueKind.Null);
        }
    }

    // ---- CSV ---------------------------------------------------------------------------------------

    [RequiresEngineFact(Engine)]
    public async Task Csv_StatusExport_EqualsPostgresCellByCell()
    {
        var pg = await ApiHostFixture.PostRawAsync(api.Client, "postgres", "export.csv", Requests.StatusExport);
        var os = await ApiHostFixture.PostRawAsync(api.Client, "opensearch", "export.csv", Requests.StatusExport);

        Assert.Equal(HttpStatusCode.OK, os.Status);
        Assert.StartsWith("text/csv", os.ContentType);
        Assert.True(os.Bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }), "UTF-8 BOM");
        var left = ParseCsv(pg.Bytes);
        var right = ParseCsv(os.Bytes);
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

    /// <summary>The export stream returns every schema column in schema order (OSE-13); the service narrows and reorders the rows to the
    /// columns the request lists, so the file equals PostgreSQL's for a subset with custom headers.</summary>
    [RequiresEngineFact(Engine)]
    public async Task Csv_ASubsetOfColumnsWithHeaders_EqualsPostgres()
    {
        const string body = """
            {
              "startRow": null, "endRow": null, "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
              "filterModel": { "country": { "filterType": "text", "type": "equals", "filter": "Germany" } },
              "sortModel": [ { "colId": "id", "sort": "asc" } ],
              "exportFormat": "csv", "exportScope": "all",
              "exportColumns": [ { "colId": "customer", "header": "Customer" }, { "colId": "id", "header": "Id" }, { "colId": "orderedAt", "header": "Ordered" } ]
            }
            """;
        var pg = await ApiHostFixture.PostRawAsync(api.Client, "postgres", "export.csv", body);
        var os = await ApiHostFixture.PostRawAsync(api.Client, "opensearch", "export.csv", body);

        Assert.Equal(HttpStatusCode.OK, os.Status);
        Assert.Equal(Encoding.UTF8.GetString(pg.Bytes), Encoding.UTF8.GetString(os.Bytes));
        Assert.StartsWith("Customer,Id,Ordered", Encoding.UTF8.GetString(os.Bytes[3..]));
    }

    /// <summary>The whole table through <c>OpenSearchExportStream</c> (a point in time, <c>search_after</c> chunks of 5,000).</summary>
    [RequiresEngineFact(Engine)]
    public async Task Csv_WholeTableExport_HasEveryRowOnceInKeyOrder()
    {
        var (status, _, _, bytes) = await ApiHostFixture.PostRawAsync(api.Client, "opensearch", "export.csv", Requests.FullExport);

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
        var (status, _, _, bytes) = await ApiHostFixture.PostRawAsync(api.Client, "opensearch", "export.csv", Requests.FormulaCustomer);

        Assert.Equal(HttpStatusCode.OK, status);
        var records = ParseCsv(bytes);
        Assert.True(records.Count >= 2, "the =SUM(A1) customer was not exported");
        Assert.All(records.Skip(1), r => Assert.Equal("'=SUM(A1)", r[2]));
    }

    // ---- text semantics, measured -----------------------------------------------------------------------

    /// <summary>
    /// Measured on OpenSearch 2.19.1 with the seed's mapping: a contains-filter is a <c>wildcard</c> with <c>case_insensitive</c> on the exact
    /// keyword sub-field, and that case folding covers ASCII letters only (as SQLite's NOCASE does): the lower-case and mixed-case texts
    /// "söhne", "Söhne" and "söHNE" find "Müller, Söhne &amp; Co" as on PostgreSQL, but a typed capital non-ASCII letter ("SÖHNE", "MÜLLER", "ÖH")
    /// does not match the stored lower-case one, where PostgreSQL's collation matches it. The accent-less "sohne" misses on both.
    /// </summary>
    [RequiresEngineFact(Engine)]
    public async Task NonAsciiContains_FoldsAsciiCaseOnly_ACapitalAccentedLetterMissesTheStoredLowerCaseOne()
    {
        const string customer = "Müller, Söhne & Co";
        bool Finds(JsonElement page) => page.GetProperty("rows").EnumerateArray().Any(r => r.GetProperty("customer").GetString() == customer);
        static string Contains(string text) => Requests.FirstPage($$"""{ "customer": { "filterType": "text", "type": "contains", "filter": "{{text}}" } }""");

        foreach (var text in new[] { "söhne", "Söhne", "söHNE", "müller" })
        {
            Assert.True(Finds(await RowsOk("postgres", Contains(text))), $"postgres {text}");
            Assert.True(Finds(await RowsOk("opensearch", Contains(text))), $"opensearch {text}");
        }

        foreach (var text in new[] { "SÖHNE", "MÜLLER", "ÖH" })
        {
            Assert.True(Finds(await RowsOk("postgres", Contains(text))), $"postgres {text}");
            Assert.False(Finds(await RowsOk("opensearch", Contains(text))), $"opensearch {text} (ASCII-only case folding)");
        }

        foreach (var text in new[] { "sohne", "SOHNE" })
        {
            Assert.False(Finds(await RowsOk("postgres", Contains(text))), $"postgres {text}");
            Assert.False(Finds(await RowsOk("opensearch", Contains(text))), $"opensearch {text}");
        }
    }

    // ---- compile only, unsupported shapes and errors (no engine needed) -----------------------------

    [Fact]
    public async Task Query_ReturnsTheCompiledSearchBodyAsJson_WithTheValueAsData()
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var (status, json) = await ApiHostFixture.PostJsonAsync(client, "opensearch", "query", Requests.NeedleFilter);

            Assert.Equal(HttpStatusCode.OK, status);
            Assert.Equal("json", json.GetProperty("kind").GetString());
            Assert.Equal("opensearch", json.GetProperty("dialect").GetString());
            var body = JsonDocument.Parse(json.GetProperty("text").GetString()!).RootElement;
            Assert.Equal(25, body.GetProperty("size").GetInt32());
            Assert.Contains(Requests.UnlikelyValue, json.GetProperty("text").GetString());
            Assert.Empty(json.GetProperty("parameters").EnumerateArray());
        }
    }

    [Fact]
    public async Task UnknownColumnFilter_Is400UnknownField()
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var (status, json) = await ApiHostFixture.PostJsonAsync(client, "opensearch", "rows", Requests.UnknownColumn);

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("unknown-field", json.GetProperty("error").GetProperty("kind").GetString());
            Assert.DoesNotContain("   at ", json.ToString());
        }
    }

    /// <summary>A shape PostgreSQL answers and OpenSearch cannot: the groups ordered by one aggregate (a composite lists groups in key order only).</summary>
    [Theory]
    [InlineData("rows")]
    [InlineData("query")]
    public async Task AGroupedLevelOrderedByAnAggregate_Is400UnsupportedShape_NamingTheShape(string route)
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            // One aggregate under the column id "amount": a sort on it is unambiguous, so PostgreSQL answers it.
            var (status, json) = await ApiHostFixture.PostJsonAsync(
                client, "opensearch", route, GroupBody(["country"], [], "amount", "desc", aggs: "sum-only"));

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("unsupported-shape", json.GetProperty("error").GetProperty("kind").GetString());
            var message = json.GetProperty("error").GetProperty("message").GetString()!;
            Assert.Contains("order groups by an aggregate", message);
            Assert.Contains("'amount'", message);
            Assert.Contains("Sort by 'country'", message);
        }
    }

    /// <summary>Expanding the group whose key is null: PostgreSQL's <c>= NULL</c> matches nothing, OpenSearch would send a term with a null
    /// value and the engine rejects it. At the top level and under another key, as a leaf and as the next level.</summary>
    [Theory]
    [InlineData("country", "null", "id")]
    [InlineData("category", "Germany|null", "id")]
    public async Task ExpandingTheNullGroup_Is400UnsupportedShape_NamingTheColumn(string nullColumn, string keys, string sortColId)
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var columns = keys.Contains('|') ? new[] { "country", "category" } : new[] { "country" };
            var (status, json) = await ApiHostFixture.PostJsonAsync(
                client, "opensearch", "rows", GroupBody(columns, keys.Split('|'), sortColId, aggs: "none"));

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("unsupported-shape", json.GetProperty("error").GetProperty("kind").GetString());
            var message = json.GetProperty("error").GetProperty("message").GetString()!;
            Assert.Contains($"'{nullColumn}' value is empty (null)", message);
            Assert.Contains("blank filter", message);
        }
    }

    /// <summary>A refusal every provider gives stays the compiler's: a sort on a column the grouped level does not produce, and a sort key
    /// that two aggregates share, answer <c>compilation-failed</c> on OpenSearch as on PostgreSQL, never <c>unsupported-shape</c>.</summary>
    [Theory]
    [InlineData("customer", "sum-avg-count")]
    [InlineData("amount", "sum-avg-count")]
    public async Task ARefusalEveryProviderGives_StaysCompilationFailed(string sortColId, string aggs)
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var (status, json) = await ApiHostFixture.PostJsonAsync(
                client, "opensearch", "rows", GroupBody(["country"], [], sortColId, aggs: aggs));

            Assert.Equal(HttpStatusCode.BadRequest, status);
            Assert.Equal("compilation-failed", json.GetProperty("error").GetProperty("kind").GetString());
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
            using var response = await client.PostAsync($"/api/opensearch/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
            var error = await ErrorBody(response);

            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("backend-unavailable", error.GetProperty("kind").GetString());
        }
    }

    /// <summary>
    /// An overloaded cluster answers HTTP 503. The library's export stream used to drop that status (bug #2523), so the demo answered
    /// <c>export.csv</c> with a 500; the status now travels with the exception and every route is a 503 <c>backend-unavailable</c>.
    /// A stub server answers 503 to any request, so no cluster is needed.
    /// </summary>
    [Theory]
    [InlineData("rows")]
    [InlineData("values")]
    [InlineData("export.csv")]
    public async Task AClusterThatAnswersHttp503_Is503BackendUnavailable_OnEveryRoute_TheExportIncluded(string route)
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
                builder.Configuration[DemoSettings.OpenSearchEnvVar] = $"http://127.0.0.1:{port}");
            await using (app)
            using (client)
            {
                var body = route == "values" ? Requests.CountryValues : route == "export.csv" ? Requests.StatusExport : Requests.PageSize(10);
                using var response = await client.PostAsync($"/api/opensearch/{route}", new StringContent(body, Encoding.UTF8, "application/json"));
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

    [Fact]
    public async Task AServerThatNeverAnswers_Is503AfterTheServicesTenSecondTimeout()
    {
        // A listener that never accepts leaves the request unanswered: the client's 10 s request timeout expires (a TaskCanceledException
        // with a TimeoutException inside, wrapped by the adapter).
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var (app, client) = await ApiHostFixture.StartAsync(null, builder =>
                builder.Configuration[DemoSettings.OpenSearchEnvVar] = $"http://127.0.0.1:{port}");
            await using (app)
            using (client)
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var (status, _) = await ApiHostFixture.PostJsonAsync(client, "opensearch", "rows", Requests.PageSize(10));

                Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
                Assert.InRange(clock.Elapsed.TotalSeconds, 8, 20);
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Health_ListsOpenSearchOnlyWhenItsSettingIsPresent()
    {
        var (app, client) = await StartWithClosedEngineAsync();
        await using (app)
        using (client)
        {
            var json = JsonDocument.Parse(await client.GetStringAsync("/api/health")).RootElement;
            Assert.Equal(
                ["ef", "mongo", "opensearch", "postgres", "sqlite"],
                json.GetProperty("providers").EnumerateArray().Select(p => p.GetString()!));
        }
    }

    // ---- engine failures and the seed target (no engine needed unless marked) ---------------------------

    [Theory]
    [InlineData(503, null, true)]
    [InlineData(429, null, true)]
    [InlineData(400, null, false)]
    [InlineData(404, null, false)]
    [InlineData(500, null, false)]
    [InlineData(null, "http", true)]
    [InlineData(null, "socket", true)]
    [InlineData(null, "timeout", true)]
    [InlineData(null, "canceled", true)]
    [InlineData(null, "bug", false)]
    public void EngineFailures_ListsTheOpenSearchStatusesAndTransportExceptions_AndNoOtherAnswer(int? status, string? inner, bool unavailable)
    {
        Exception? cause = inner switch
        {
            "http" => new HttpRequestException("down"),
            "socket" => new SocketException(),
            "timeout" => new TimeoutException(),
            "canceled" => new TaskCanceledException(),
            "bug" => new InvalidOperationException("The call did not succeed."),
            _ => status is null ? null : new InvalidOperationException("status")
        };
        var failure = cause is null
            ? new OpenSearchClientCompilationException("refused up front")
            : new OpenSearchClientCompilationException("call failed", cause, status);

        Assert.Equal(unavailable, EngineFailures.IsUnavailable(failure));
    }

    [Fact]
    public async Task EngineFailures_ARealRefusedConnection_IsUnavailable()
    {
        var preparer = new GridRequestPreparer(OpenSearchSeeder.LoadSchema(), DemoLimits.Default);
        var service = new OpenSearchGridService(ClosedPortUrl(), preparer);

        var failure = await Assert.ThrowsAsync<OpenSearchClientCompilationException>(
            () => service.FetchRowsAsync(preparer.PrepareRows(Requests.PageSize(5)), CancellationToken.None));

        Assert.Null(failure.HttpStatusCode);
        Assert.IsType<HttpRequestException>(failure.InnerException);
        Assert.True(EngineFailures.IsUnavailable(failure));
    }

    [RequiresEngineFact(Engine)]
    public async Task EngineFailures_ARejectedQuery_StaysAnOrdinaryFailure()
    {
        var client = new OpenSearchLowLevelClient(new ConnectionConfiguration(new Uri(Environment.GetEnvironmentVariable(Engine)!)));
        var response = await client.SearchAsync<StringResponse>("orders", PostData.String("""{ "query": { "term": { "country.keyword": { "value": null } } } }"""));

        var failure = Assert.Throws<OpenSearchClientCompilationException>(() => OpenSearchCalls.Require(response, "A rejected query"));

        Assert.Equal(400, failure.HttpStatusCode);
        Assert.False(EngineFailures.IsUnavailable(failure));
    }

    [Fact]
    public void TheOpenSearchSeedTarget_IsNotADefault_AndTheHintNamesTheProfileAndTheSetting()
    {
        var target = SeedTargets.All.Single(t => t.Name == "opensearch");

        Assert.False(target.Default);
        Assert.Contains("DEMO_OPENSEARCH", target.Hint);
        Assert.Contains("--profile opensearch", target.Hint);
    }

    [Fact]
    public async Task Seed_WithTheSettingUnset_ReportsItForOpenSearchAndExits2()
    {
        var saved = Environment.GetEnvironmentVariable(Engine);
        Environment.SetEnvironmentVariable(Engine, null);
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await SeedCommand.RunAsync(["opensearch"], SeedTargets.All, output, error);

            Assert.Equal(2, exit);
            Assert.Contains("DEMO_OPENSEARCH is not set.", error.ToString());
            Assert.Contains("--profile opensearch", error.ToString());
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
        Environment.SetEnvironmentVariable(Engine, ClosedPortUrl());
        try
        {
            using var output = new StringWriter();
            using var error = new StringWriter();

            var exit = await SeedCommand.RunAsync(["opensearch"], SeedTargets.All, output, error);

            Assert.Equal(2, exit);
            Assert.Contains("Seed failed for opensearch", error.ToString());
            Assert.Contains("--profile opensearch", error.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable(Engine, saved);
        }
    }

    // ---- the seeder (needs the engine; same collection, so no test reads the index while it is rebuilt) ------

    [RequiresEngineFact(Engine)]
    public async Task Seeder_StoresTheMappingTheNormalizerAndEveryRow()
    {
        var setting = Environment.GetEnvironmentVariable(Engine)!;
        var rows = OrderGenerator.Generate();
        // Twice: the second run deletes and recreates the index.
        Assert.Equal(rows.Count, await OpenSearchSeeder.SeedAsync(setting, rows));
        Assert.Equal(rows.Count, await OpenSearchSeeder.SeedAsync(setting, rows));

        using var http = new HttpClient { BaseAddress = new Uri(setting) };
        var mapping = JsonDocument.Parse(await http.GetStringAsync("orders/_mapping")).RootElement.GetProperty("orders").GetProperty("mappings").GetProperty("properties");

        Assert.Equal("keyword", mapping.GetProperty("customer").GetProperty("type").GetString());
        Assert.Equal("keyword", mapping.GetProperty("customer").GetProperty("fields").GetProperty("keyword").GetProperty("type").GetString());
        Assert.Equal("lowercase_normalizer", mapping.GetProperty("customer").GetProperty("fields").GetProperty("normalized").GetProperty("normalizer").GetString());
        Assert.Equal("double", mapping.GetProperty("amount").GetProperty("type").GetString());
        Assert.Equal("long", mapping.GetProperty("quantity").GetProperty("type").GetString());
        Assert.Equal("strict_date_optional_time", mapping.GetProperty("orderedAt").GetProperty("format").GetString());
        Assert.Equal("strict_date", mapping.GetProperty("shippedOn").GetProperty("format").GetString());
        Assert.Equal("boolean", mapping.GetProperty("isPriority").GetProperty("type").GetString());

        var found = JsonDocument.Parse(await (await http.PostAsync(
            "orders/_count",
            new StringContent("""{ "query": { "term": { "customer.keyword": "Müller, Söhne & Co" } } }""", Encoding.UTF8, "application/json"))).Content.ReadAsStringAsync()).RootElement;
        Assert.True(found.GetProperty("count").GetInt64() > 0, "the non-ASCII customer is seeded, not filtered out");

        var order = rows.First(row => row.Amount is not null);
        var source = JsonDocument.Parse(await http.GetStringAsync($"orders/_doc/{order.Id}")).RootElement.GetProperty("_source");
        Assert.Equal(JsonValueKind.Number, source.GetProperty("amount").ValueKind);
        Assert.Equal(order.Amount, source.GetProperty("amount").GetDecimal());
        Assert.Equal(order.OrderedAt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), source.GetProperty("orderedAt").GetString());
    }
}
