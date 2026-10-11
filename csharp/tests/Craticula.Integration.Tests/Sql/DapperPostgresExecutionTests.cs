using System.Text;
using Craticula.AgGrid;
using Craticula.Core.Export;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Dapper;
using Craticula.Integration.Tests.Docker;
using Craticula.Integration.Tests.Oracle;
using Craticula.Sql;
using Xunit;

namespace Craticula.Integration.Tests.Containers.Sql;

/// <summary>
/// Bug #2304 (set membership) and Bug #2320 (scalar comparisons, date presets and the export keyset): the
/// <c>Craticula.Dapper</c> adapter returns the right rows for <c>in</c> / <c>notIn</c>, <c>eq</c>, <c>inRange</c> and a
/// date preset on decimal, date and datetime fields, and its export walk spans chunks on such a sort key, on a real
/// PostgreSQL 16 (the shared <see cref="PostgresEngine"/> container).
///
/// <para>Why this is its own test and not covered by <see cref="PostgresExecutionEqualityTests"/>: that tier binds the
/// array through <c>PostgresEngine.Bind</c>, a hand-written binder. A host that uses the adapter gets the parameter
/// shape <c>DapperParameterBinder.ToTypedArray</c> builds, which for string-encoded decimal/date/datetime values (AR-7)
/// is a <c>string[]</c>, that is a <c>text[]</c> on the wire. <c>numeric = ANY(text[])</c> does not exist in PostgreSQL;
/// it is the dialect's explicit cast (<c>= ANY($1::numeric[])</c>, <c>$1::date[]</c>, <c>$1::timestamptz[]</c>, SQL-4) that
/// converts the <c>text[]</c> server-side. A host port must therefore test the same parameter shape, which is what runs here.
/// The string and int64 fixtures (035..038) are the regression guard for the arrays that need no cast.
/// Scalar comparisons have the same shape of problem (SQL-8): a string parameter is <c>text</c> and PostgreSQL has no
/// <c>numeric = text</c>, <c>date >= text</c> or <c>timestamptz > text</c> operator, so the dialect casts the placeholder
/// (<c>$1::numeric</c>, <c>$1::date</c>, <c>$1::timestamptz</c>); without it these cases fail with 42883.</para>
///
/// <para>The request is the real pipeline (parser, <see cref="GridFilterPipeline"/>, default <see cref="ResourceLimits"/>),
/// the table is seeded by the engine exactly as in the execution tier, and the rows are compared with
/// <see cref="RowComparer"/> against <c>expectedRows.json</c> (XEQ-3) after mapping onto the logical columns.</para>
/// </summary>
[Collection(PostgresCollection.Name)]
public class DapperPostgresExecutionTests(PostgresEngine engine)
{
    public static IEnumerable<object[]> Fixtures() =>
    [
        ["operators/035-in-string"],
        ["operators/036-notin-string"],
        ["operators/037-in-int64"],
        ["operators/038-notin-int64"],
        ["operators/048-in-decimal"],
        ["operators/049-notin-decimal"],
        ["operators/050-in-date"],
        ["operators/051-notin-date"],
        ["operators/052-in-datetime"],
        ["operators/053-notin-datetime"],
        // SQL-8 (#2320): scalar comparisons, inRange bounds and a date preset on the three string-encoded types.
        ["operators/046-eq-decimal"],
        ["operators/040-inrange-date-inclusive-exclusive"],
        ["operators/041-inrange-datetime-exclusive-inclusive"],
        ["date-presets/001-today"]
    ];

    // Export keyset walk (SQL-8 on the export path): fixture, sort field. Every seed has a null in the sort field and,
    // for the decimal one, two rows equal as numbers (19.99 / 19.990), so the boundary tie rules are exercised too.
    public static IEnumerable<object[]> ExportCases() =>
    [
        ["operators/046-eq-decimal", "unitCost", 1],
        ["operators/046-eq-decimal", "unitCost", 3],
        ["operators/040-inrange-date-inclusive-exclusive", "birthDate", 1],
        ["operators/040-inrange-date-inclusive-exclusive", "birthDate", 3],
        ["operators/041-inrange-datetime-exclusive-inclusive", "createdAt", 1],
        ["operators/041-inrange-datetime-exclusive-inclusive", "createdAt", 3]
    ];

    [DockerTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Dapper_Returns_The_Expected_Rows_On_Postgres(string name)
    {
        var fixture = ExecutionFixture.Load(name);
        var schema = fixture.Schema;
        var request = BuildRequest(fixture);

        await using var connection = await engine.OpenSeededConnectionAsync(fixture);
        var page = await DapperGridQuery.FetchPageAsync(connection, request, PostgresDialect.Instance);

        var expected = ((ExpectRows)fixture.ExpectationFor("postgres")).Result;
        var types = ColumnTypes.Resolve(schema, fixture.Request, expected.Columns);
        var problems = RowComparer.Compare(expected, types, page.Columns, page.Rows);

        if (problems.Count > 0)
        {
            var message = new StringBuilder().Append(name).Append(" through Dapper on postgres: ").Append(problems.Count)
                .AppendLine(" mismatch(es) against expectedRows.json (XEQ-3)");
            foreach (var problem in problems.Take(12))
            {
                message.Append("  - ").AppendLine(problem);
            }

            Assert.Fail(message.ToString());
        }

        // Anti-tautology: the filter must have kept some rows and dropped some.
        Assert.InRange(page.Rows.Count, 1, fixture.Seed.Count - 1);
    }

    [DockerTheory]
    [MemberData(nameof(ExportCases))]
    public async Task Dapper_Export_Walks_A_Decimal_Date_Or_Datetime_Sort_Key_Across_Chunks_On_Postgres(
        string name, string sortField, int chunkSize)
    {
        var fixture = ExecutionFixture.Load(name);
        var schema = fixture.Schema;
        var request = new SqlQueryRequest
        {
            Schema = schema,
            Filter = null,
            Sort = [new SortItem { Field = sortField, Direction = SortDirection.Asc, NullsPosition = NullsPosition.Last }],
            Page = new NonePage()
        };

        await using var connection = await engine.OpenSeededConnectionAsync(fixture);

        var rows = new List<ExportRow>();
        await foreach (var row in DapperExportStream.StreamAsync(
            connection, request, PostgresDialect.Instance, new DapperExportOptions { ChunkSize = chunkSize }))
        {
            rows.Add(row);
        }

        // Projection defaults to schema.FieldOrder: id is column 0, the sort field is its own index.
        var sortIndex = schema.FieldOrder.ToList().IndexOf(sortField);
        var ids = rows.Select(row => Convert.ToInt64(row[0])).ToList();

        // No gap and no duplicate: exactly the seeded ids, each once (chunkSize 1 puts a boundary at every row).
        Assert.True(fixture.Seed.Count > chunkSize * 2, "the seed must span at least three chunks");
        Assert.Equal(fixture.Seed.Count, rows.Count);
        Assert.Equal(ids.Count, ids.Distinct().Count());

        // Sort order: non-null values ascending, then the nulls (NullsPosition.Last); ties by the stable key.
        for (var index = 1; index < rows.Count; index++)
        {
            var previous = rows[index - 1][sortIndex];
            var current = rows[index][sortIndex];
            if (previous is null)
            {
                Assert.Null(current);
                Assert.True(ids[index - 1] < ids[index], $"{name}: null-tied rows must follow the stable key");
                continue;
            }

            if (current is null)
            {
                continue;
            }

            var comparison = Comparer<object>.Default.Compare(previous, current);
            Assert.True(comparison <= 0, $"{name}: row {index} ({current}) sorts before row {index - 1} ({previous})");
            if (comparison == 0)
            {
                Assert.True(ids[index - 1] < ids[index], $"{name}: tied rows must follow the stable key");
            }
        }
    }

    private static SqlQueryRequest BuildRequest(ExecutionFixture fixture)
    {
        var schema = fixture.Schema;
        var json = File.ReadAllText(Path.Combine(fixture.Directory, "agGridRequest.json"));
        // A fixture's requestContext.now is the harness's instruction to pin the clock (date presets, AG-2).
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var clock = document.RootElement.TryGetProperty("requestContext", out var context)
            && context.TryGetProperty("now", out var now) && now.ValueKind == System.Text.Json.JsonValueKind.String
                ? new PinnedClock(DateTimeOffset.Parse(now.GetString()!, System.Globalization.CultureInfo.InvariantCulture))
                : TimeProvider.System;
        var parsed = AgGridRequestParser.Parse(json, new AgGridParseOptions { Schema = schema, Clock = clock });
        var filter = GridFilterPipeline.Process(
            parsed.NativeFilter,
            parsed.CustomFilters,
            parsed.Page,
            schema,
            new ResourceLimits(),
            parsed.Group,
            parsed.Aggregations);

        return new SqlQueryRequest
        {
            Schema = schema,
            Filter = filter,
            Sort = parsed.Sort,
            Page = parsed.Page,
            Group = parsed.Group,
            Aggregations = parsed.Aggregations,
            Projection = parsed.Export?.ProjectionColumns
        };
    }

    private sealed class PinnedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now.ToUniversalTime();
    }
}
