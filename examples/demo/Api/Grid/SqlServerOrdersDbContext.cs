using Craticula.Core.Schema;
using Craticula.EntityFrameworkCore;
using Craticula.Sql;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Demo.Api.Grid;

/// <summary>
/// The <c>orders</c> model on SQL Server (route <c>ef-sqlserver</c>), over the table <c>seed sqlserver</c> fills. The adapter ships only a
/// SQLite preset, so the five engine facts are declared here, each read from <see cref="EfCoreProviderSemantics"/>'s remarks and the
/// SQL Server rules of <see cref="MssqlDialect"/>.
/// </summary>
public sealed class SqlServerOrdersDbContext(DbContextOptions<SqlServerOrdersDbContext> options, GridSchema schema)
    : SchemaDbContext<SqlServerOrdersDbContext>(options, schema)
{
    public static EfCoreProviderSemantics Semantics { get; } = new()
    {
        // SEM-1: the same collation names the Dapper route spells (MssqlDialect), so the two routes compare alike.
        CaseInsensitiveCollation = MssqlDialect.CaseInsensitiveCollation,
        CaseSensitiveCollation = MssqlDialect.CaseSensitiveCollation,
        // false: the adapter then lowers both sides (lower(column) LIKE lower(pattern)), which is right for every column. With true it
        // emits a bare LIKE, which follows the column's own collation: exact-case on the binary-collated grouping columns (country,
        // category, status: Latin1_General_100_BIN2), so a "germ" filter would find no "Germany" (measured by the review: 0 rows against 1,933).
        PatternMatchFoldsCase = false,
        // SEC-4: only T-SQL's LIKE treats [ as a metacharacter.
        PatternMatchTreatsLeftBracketAsMetacharacter = true,
        // Only Oracle treats '' as NULL.
        EmptyStringIsNull = false
    };

    /// <summary>
    /// SQL Server's <c>datetime2</c> carries no zone, so EF hands back a <see cref="DateTime"/> of unspecified kind and the JSON would lose the
    /// trailing <c>Z</c> every other route sends. The EF adapter has no reader of its own to normalise it (bug #2798); until that is decided
    /// the host states the kind, as EF Core's documentation advises for UTC columns: this converter reads every datetime as UTC and writes it unchanged.
    /// The seed stores UTC instants.
    /// </summary>
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateTime>().HaveConversion<UtcDateTimeConverter>();

    private sealed class UtcDateTimeConverter()
        : ValueConverter<DateTime, DateTime>(value => value, value => DateTime.SpecifyKind(value, DateTimeKind.Utc));
}
