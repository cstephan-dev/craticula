using Craticula.Sql;

namespace Craticula.Dapper.Tests;

/// <summary>
/// Proves <c>Craticula.Dapper</c>'s own row materialization against a real database, not a mock: the
/// SAME compiled <see cref="SqlQuery"/>, read back by two INDEPENDENT readers — a hand-written raw-ADO
/// oracle (<see cref="SqliteFixtureDatabase.ExecuteCompiled"/>, which owes nothing to this package's own
/// code) and this package's own production entry point (<see cref="DapperGridQuery.FetchPageAsync"/>).
/// A divergence here is a bug in <see cref="DapperValueReader"/> or <see cref="DapperParameterBinder"/>,
/// never a semantics disagreement — both readers execute the byte-identical SQL text and parameters, so
/// nothing about SEM-1/SEM-2/etc. is in play; what is in play is whether this package binds and
/// materializes that ONE statement correctly.
///
/// <para>Each case is checked for the anti-tautology property before the real assertion: a dataset that
/// returns everything, or nothing, would be satisfied by an implementation that ignored the filter
/// entirely.</para>
/// </summary>
public class DapperExecutionEqualityTests
{
    private static readonly ISqlDialect Sqlite = SqliteDialect.Instance;

    /// <summary>operators/001: <c>status equals 'active' OR status equals 'approved'</c>, exercising
    /// SEM-1's default case-insensitive collation on a <c>string</c> field.</summary>
    [Fact]
    public async Task Operators_001_Matches_The_Independent_Oracle()
    {
        var fixture = FixtureRequest.Load("operators", FixtureRequest.ResolveDirectory("operators", "001"));
        await AssertMatchesOracle(fixture, Datasets.StatusAndId());
    }

    /// <summary>filter-tree/001: four ANDed predicates across a string substring match, a numeric
    /// comparison, a text equality and AR-6's boolean shorthand — several operator families and several
    /// declared <see cref="Craticula.Core.Schema.FieldType"/>s materializing in one statement.</summary>
    [Fact]
    public async Task Filter_Tree_001_Matches_The_Independent_Oracle()
    {
        var fixture = FixtureRequest.Load("filter-tree", FixtureRequest.ResolveDirectory("filter-tree", "001"));
        await AssertMatchesOracle(fixture, Datasets.Orders());
    }

    /// <summary>sort-paging/007: the suite's hardest ordering (three sort keys, mixed-case collation,
    /// three different null placements, ties resolved by the tiebreaker) — proves the ORDER, not just
    /// the SET, matches between the two readers.</summary>
    [Fact]
    public async Task Sort_Paging_007_Matches_The_Independent_Oracle_Including_Order()
    {
        var fixture = FixtureRequest.Load("sort-paging", FixtureRequest.ResolveDirectory("sort-paging", "007"));
        var dataset = Datasets.Employees();
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, dataset);

        var request = fixture.Sql with { Projection = fixture.Schema.FieldOrder };
        var query = SqlQueryBuilder.Build(request, Sqlite);
        var oracleRows = database.ExecuteCompiled(query, fixture.Schema.FieldOrder);

        var page = await DapperGridQuery.FetchPageAsync(database.Connection, fixture.Sql, Sqlite);

        var stableKeyIndex = fixture.Schema.FieldOrder.ToList().IndexOf(fixture.Schema.StableKey);
        var oracleIds = oracleRows.Select(row => Convert.ToInt64(row[stableKeyIndex])).ToArray();
        var dapperIds = page.Rows.Select(row => Convert.ToInt64(row[stableKeyIndex])).ToArray();

        Assert.Equal(Datasets.EmployeesOrderedIds, oracleIds);
        Assert.Equal(Datasets.EmployeesOrderedIds, dapperIds);
    }

    private static async Task AssertMatchesOracle(
        FixtureRequest fixture, IReadOnlyList<IReadOnlyDictionary<string, object?>> dataset)
    {
        using var database = SqliteFixtureDatabase.Create(fixture.Schema, dataset);

        var request = fixture.Sql with { Projection = fixture.Schema.FieldOrder };
        var query = SqlQueryBuilder.Build(request, Sqlite);
        var oracleRows = database.ExecuteCompiled(query, fixture.Schema.FieldOrder);

        // Anti-tautology: a case that returns every seeded row, or none of them, proves nothing about
        // whether the filter was actually applied.
        Assert.True(
            oracleRows.Count > 0 && oracleRows.Count < dataset.Count,
            $"{fixture.Name}: expected a strict subset of {dataset.Count} seeded rows, got " +
            $"{oracleRows.Count} — this dataset does not discriminate.");

        var page = await DapperGridQuery.FetchPageAsync(database.Connection, fixture.Sql, Sqlite);

        Assert.Equal(fixture.Schema.FieldOrder, page.Columns);
        Assert.Equal(oracleRows.Count, page.Rows.Count);

        for (var index = 0; index < oracleRows.Count; index++)
        {
            Assert.Equal(oracleRows[index], page.Rows[index]);
        }
    }
}
