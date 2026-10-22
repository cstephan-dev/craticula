using System.Data.Common;
using Craticula.Core.Schema;
using Craticula.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Demo.Api.Grid;

/// <summary>
/// The <c>orders</c> model on MySQL (route <c>ef-mysql</c>) through Oracle's <c>MySql.EntityFrameworkCore</c>, over the table <c>seed mysql</c> fills.
/// Two workarounds for defects of that provider, see <see cref="MySqlSessionInterceptor"/> and the DateOnly converter below.
/// </summary>
public sealed class MySqlOrdersDbContext(DbContextOptions<MySqlOrdersDbContext> options, GridSchema schema)
    : SchemaDbContext<MySqlOrdersDbContext>(options, schema)
{
    public static EfCoreProviderSemantics Semantics { get; } = new()
    {
        // Case sensitivity is DDL-side on MySQL: the table is accent/case-insensitive, country/category/status are utf8mb4_bin.
        CaseInsensitiveCollation = null,
        CaseSensitiveCollation = null,
        // false: lower both sides; a bare LIKE would not fold on the utf8mb4_bin columns.
        PatternMatchFoldsCase = false,
        PatternMatchTreatsLeftBracketAsMetacharacter = false,
        EmptyStringIsNull = false
    };

    // MySql.Data hands a DATE back as DateTime and the provider has no DateOnly reader (InvalidCastException), so DateOnly is converted.
    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder) =>
        configurationBuilder.Properties<DateOnly>().HaveConversion<DateOnlyConverter>();

    private sealed class DateOnlyConverter() : ValueConverter<DateOnly, DateTime>(
        value => value.ToDateTime(TimeOnly.MinValue),
        value => DateOnly.FromDateTime(value));
}

/// <summary>
/// The provider writes <c>LIKE ... ESCAPE '\'</c> with a single backslash, which MySQL reads as an escaped quote. NO_BACKSLASH_ESCAPES makes the
/// backslash an ordinary character in string literals, so the literal becomes valid.
/// </summary>
public sealed class MySqlSessionInterceptor : DbConnectionInterceptor
{
    private const string Sql = "SET SESSION sql_mode = CONCAT(@@SESSION.sql_mode, ',NO_BACKSLASH_ESCAPES')";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = connection.CreateCommand();
        command.CommandText = Sql;
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = Sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}
