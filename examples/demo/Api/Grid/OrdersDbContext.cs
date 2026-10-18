using Craticula.Core.Schema;
using Craticula.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace Demo.Api.Grid;

/// <summary>
/// The EF Core model of the <c>orders</c> table, built from the grid schema rather than from an entity class
/// (<see cref="GridSchemaPropertyBag"/>: columns come from <c>physical.sql.column</c> in <c>orders.schema.json</c>).
/// <see cref="IGridSchemaModel"/> plus <c>GridSchemaModelCacheKeyFactory</c> (registered in <c>DemoApp</c>) keep EF's model
/// cache keyed by the schema. The wiring in <c>DemoApp</c> is:
/// <code>
/// services.AddDbContextFactory&lt;OrdersDbContext&gt;((sp, o) => o
///     .UseNpgsql(sp.GetRequiredService&lt;NpgsqlDataSource&gt;())
///     .ReplaceService&lt;IModelCacheKeyFactory, GridSchemaModelCacheKeyFactory&gt;());
/// </code>
/// </summary>
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options, GridSchema schema)
    : DbContext(options), IGridSchemaModel
{
    /// <summary>
    /// The five engine facts the EF adapter cannot derive, declared for this PostgreSQL database (the adapter ships only a
    /// SQLite preset on purpose; see <see cref="EfCoreProviderSemantics"/>). Each value is read from that type's remarks.
    /// </summary>
    public static EfCoreProviderSemantics PostgresSemantics { get; } = new()
    {
        // SEM-1: the nondeterministic ICU collation PostgresSeeder creates; the adapter spells COLLATE on the column.
        CaseInsensitiveCollation = "craticula_ci",
        // PostgreSQL's default collation already distinguishes case, so a case-sensitive field needs nothing spelled.
        CaseSensitiveCollation = null,
        // PostgreSQL LIKE never folds case. The adapter therefore emits lower(column) LIKE lowered-pattern, because
        // LIKE against craticula_ci (deterministic = false) is rejected. This is the documented divergence from the Dapper
        // route's ILIKE: they agree on ASCII and ordinary Unicode case pairs.
        PatternMatchFoldsCase = false,
        // Only SQL Server's LIKE treats [ as a metacharacter.
        PatternMatchTreatsLeftBracketAsMetacharacter = false,
        // Only Oracle treats '' as NULL; PostgreSQL keeps them apart.
        EmptyStringIsNull = false
    };

    public GridSchema Schema => schema;

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        GridSchemaPropertyBag.Configure(modelBuilder, schema);
}
