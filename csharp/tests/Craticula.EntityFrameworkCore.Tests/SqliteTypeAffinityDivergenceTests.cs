using Craticula.Sql;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// The two places the two paths <b>legitimately disagree</b> on SQLite, executed and asserted rather than
/// skipped — because a divergence nobody wrote down is the failure mode AR-4 exists to prevent, and a
/// divergence quietly excluded from a harness is the same thing with extra steps.
///
/// <para><b>Both have one cause: SQLite has no native type for <c>decimal</c> or for a timestamp.</b> Both
/// are stored as TEXT, and the two paths then read that TEXT differently — not because either is wrong, but
/// because each is doing the most correct thing available to it:</para>
///
/// <list type="number">
/// <item><description><b><c>decimal</c>.</b> The compiled statement is <c>"unit_cost" &gt; ?</c> with a
/// numeric STRING bound (AR-7 carries a decimal as a string precisely so nothing rounds it), which SQLite
/// evaluates as a TEXT comparison — lexicographic, so <c>'5.00' &gt; '49.95'</c> is true. EF's SQLite
/// provider registers its own <c>ef_compare</c> function on the connection and emits
/// <c>ef_compare("unit_cost", @p) &gt; 0</c>, which compares NUMERICALLY. EF's answer is the one a user
/// means; the compiler's is the one that is right on every engine where <c>NUMERIC</c> is a real
/// type.</description></item>
/// <item><description><b><c>datetime</c>.</b> SEM-4 pins the compiled parameter to canonical ISO-8601 UTC
/// (<c>2026-02-01T00:00:00Z</c>) and the artifacts compare it byte-for-byte. EF hands the provider a
/// <see cref="DateTime"/>, which <c>Microsoft.Data.Sqlite</c> renders in ITS storage format
/// (<c>2026-02-01 00:00:00</c> — a space, no <c>Z</c>). Against a TEXT column those two strings order
/// differently: <c>' '</c> sorts before <c>'T'</c>, so at an exact boundary an <c>lte</c> excludes the row
/// the compiled statement includes.</description></item>
/// </list>
///
/// <para><b>On a real engine neither can arise.</b> PostgreSQL's <c>NUMERIC</c> and
/// <c>TIMESTAMPTZ</c>, SQL Server's <c>DECIMAL</c> and <c>DATETIME2</c>, Oracle's <c>NUMBER</c> and
/// <c>TIMESTAMP</c> are ordered types, and both paths bind a value the driver converts into them. That is
/// why #1655's execution tier — which runs against a seeded PostgreSQL — is the place these two shapes get
/// their parity assertion, and why <see cref="EfCoreExecutionEqualityTests"/> covers the five types SQLite
/// stores natively instead of loosening its comparison to accommodate two it does not.</para>
///
/// <para><b>These tests assert the disagreement.</b> If one starts failing, something changed the story —
/// a provider release, or a decision to normalise one side — and the right response is to re-read this
/// file, not to adjust an assertion. #1644's lesson was a compiler bent to fit a bad artifact; the mirror
/// of it is a test loosened to hide a real difference.</para>
/// </summary>
public class SqliteTypeAffinityDivergenceTests
{
    private static readonly EfCoreProviderSemantics Sqlite = EfCoreProviderSemantics.Sqlite;

    /// <summary>
    /// <c>operators/047</c>: <c>unitCost &gt; 49.95</c>. Values chosen so lexicographic and numeric order
    /// disagree — <c>'5.00'</c> is numerically below the bound and lexicographically above it, and
    /// <c>'100.00'</c> is the reverse.
    /// </summary>
    [Fact]
    public async Task A_Decimal_Comparison_Disagrees_Because_Sqlite_Has_No_Decimal_Type()
    {
        var fixture = FixtureRequest.Load("operators", FixtureRequest.ResolveDirectory("operators", "047"));

        IReadOnlyList<IReadOnlyDictionary<string, object?>> dataset =
        [
            new Dictionary<string, object?> { ["id"] = 1L, ["unitCost"] = 5.00m },    // numerically below, lexicographically above
            new Dictionary<string, object?> { ["id"] = 2L, ["unitCost"] = 100.00m },  // numerically above, lexicographically below
            new Dictionary<string, object?> { ["id"] = 3L, ["unitCost"] = 49.95m },   // exactly ON the bound: excluded by both
            new Dictionary<string, object?> { ["id"] = 4L, ["unitCost"] = null }
        ];

        using var database = SqliteFixtureDatabase.Create(fixture.Schema, dataset);

        var compiled = SqlQueryBuilder.Build(fixture.Sql, SqliteDialect.Instance);
        var sqlIds = Ids(database.ExecuteCompiled(compiled), fixture);

        var page = await EfCoreGridQuery.FetchPageAsync(database.Rows(), fixture.Ef, Sqlite);
        var efIds = Ids(page.Rows, fixture);

        // EF is numerically right: only 100.00 exceeds 49.95.
        Assert.Equal([2L], efIds);

        // The compiled statement compares TEXT: '5.00' > '49.95' because '5' > '4'.
        Assert.Equal([1L], sqlIds);

        Assert.NotEqual(sqlIds, efIds);

        // ...and the cause is visible in the statement EF sent, not inferred.
        Assert.Contains(
            database.ExecutedCommands,
            command => command.Contains("ef_compare", StringComparison.Ordinal));
    }

    /// <summary>
    /// <c>operators/024</c>: <c>createdAt &lt;= 2026-02-01T00:00:00Z</c>, with a row exactly ON the
    /// boundary — the one place the two TEXT encodings of an instant order differently.
    /// </summary>
    [Fact]
    public async Task A_Datetime_Boundary_Disagrees_Because_The_Two_Paths_Encode_The_Instant_Differently()
    {
        var fixture = FixtureRequest.Load("operators", FixtureRequest.ResolveDirectory("operators", "024"));

        // Seeded through Microsoft.Data.Sqlite from a real DateTime, so the stored TEXT is the provider's
        // own format — which is what EF will read back and compare against, and what any host writing rows
        // through EF would have produced.
        IReadOnlyList<IReadOnlyDictionary<string, object?>> dataset =
        [
            new Dictionary<string, object?> { ["id"] = 1L, ["createdAt"] = new DateTime(2026, 1, 15, 12, 0, 0, DateTimeKind.Utc) },
            new Dictionary<string, object?> { ["id"] = 2L, ["createdAt"] = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc) }, // ON the boundary
            new Dictionary<string, object?> { ["id"] = 3L, ["createdAt"] = new DateTime(2026, 2, 1, 6, 0, 0, DateTimeKind.Utc) }, // SAME DAY, six hours after it
            new Dictionary<string, object?> { ["id"] = 4L, ["createdAt"] = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc) },
            new Dictionary<string, object?> { ["id"] = 5L, ["createdAt"] = null }
        ];

        using var database = SqliteFixtureDatabase.Create(fixture.Schema, dataset);

        var compiled = SqlQueryBuilder.Build(fixture.Sql, SqliteDialect.Instance);
        var sqlIds = Ids(database.ExecuteCompiled(compiled), fixture);

        var page = await EfCoreGridQuery.FetchPageAsync(database.Rows(), fixture.Ef, Sqlite);
        var efIds = Ids(page.Rows, fixture);

        // EF binds '2026-02-01 00:00:00' — the provider's own storage format, character-for-character what
        // the stored text uses — so `lte` includes the boundary row and excludes the one six hours later.
        // This is the correct answer.
        Assert.Equal([1L, 2L], efIds);

        // The compiled parameter is SEM-4's canonical '2026-02-01T00:00:00Z'. Against a TEXT column that is
        // a TEXT comparison, and ' ' (0x20) precedes 'T' (0x54) — so EVERY stored timestamp on
        // 2026-02-01 compares less than the bound, whatever its time of day, and id 3 is admitted.
        Assert.Equal([1L, 2L, 3L], sqlIds);

        Assert.NotEqual(sqlIds, efIds);
        Assert.Contains("2026-02-01T00:00:00Z", (string)compiled.Parameters[0]!, StringComparison.Ordinal);

        // The cause, isolated from either compiler: the two encodings of ONE instant do not compare the
        // same way against the stored text. Both statements are correct about the value they were given.
        Assert.Equal(1, CountRowsMatching(database, "SELECT COUNT(*) FROM items WHERE created_at = ?1", "2026-02-01 00:00:00"));
        Assert.Equal(0, CountRowsMatching(database, "SELECT COUNT(*) FROM items WHERE created_at = ?1", "2026-02-01T00:00:00Z"));
    }

    private static long CountRowsMatching(SqliteFixtureDatabase database, string sql, string parameter) =>
        database.ScalarCount(sql, parameter);

    private static IReadOnlyList<long> Ids(
        IReadOnlyList<IReadOnlyList<object?>> rows, FixtureRequest fixture)
    {
        var index = fixture.Schema.FieldOrder.ToList().IndexOf(fixture.Schema.StableKey);
        return [.. rows.Select(row => Convert.ToInt64(row[index]))];
    }
}
