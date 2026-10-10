using System.Text;
using Craticula.AgGrid;
using Craticula.Core.Schema;
using Craticula.Dapper;
using Craticula.Integration.Tests.Docker;
using Craticula.Integration.Tests.Oracle;
using Craticula.Sql;
using Xunit;

namespace Craticula.Integration.Tests.Containers.Sql;

/// <summary>
/// Bug #2304, criterion 3: the <c>Craticula.Dapper</c> adapter returns the right rows for <c>in</c> / <c>notIn</c> on
/// decimal, date and datetime fields on a real PostgreSQL 16 (the shared <see cref="PostgresEngine"/> container).
///
/// <para>Why this is its own test and not covered by <see cref="PostgresExecutionEqualityTests"/>: that tier binds the
/// array through <c>PostgresEngine.Bind</c>, a hand-written binder. A host that uses the adapter gets the parameter
/// shape <c>DapperParameterBinder.ToTypedArray</c> builds, which for string-encoded decimal/date/datetime values (AR-7)
/// is a <c>string[]</c>, that is a <c>text[]</c> on the wire. <c>numeric = ANY(text[])</c> does not exist in PostgreSQL;
/// it is the dialect's explicit cast (<c>= ANY($1::numeric[])</c>, <c>$1::date[]</c>, <c>$1::timestamptz[]</c>, SQL-4) that
/// converts the <c>text[]</c> server-side. A host port must therefore test the same parameter shape, which is what runs here.
/// The string and int64 fixtures (035..038) are the regression guard for the arrays that need no cast.</para>
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
        ["operators/053-notin-datetime"]
    ];

    [DockerTheory]
    [MemberData(nameof(Fixtures))]
    public async Task Dapper_Returns_The_Expected_Rows_For_Set_Membership_On_Postgres(string name)
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

    private static SqlQueryRequest BuildRequest(ExecutionFixture fixture)
    {
        var schema = fixture.Schema;
        var json = File.ReadAllText(Path.Combine(fixture.Directory, "agGridRequest.json"));
        var parsed = AgGridRequestParser.Parse(json, new AgGridParseOptions { Schema = schema });
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
}
