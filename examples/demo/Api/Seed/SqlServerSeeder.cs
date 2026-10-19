using System.Data;
using Demo.Api.Grid;
using Microsoft.Data.SqlClient;

namespace Demo.Api.Seed;

/// <summary>An optional engine's setting is missing; <c>seed</c> reports it for that provider and goes on with the others.</summary>
public sealed class SeedSettingException(string message) : Exception(message);

/// <summary>
/// Creates database <c>demo</c> when it is absent (connecting to <c>master</c> first), drops and recreates table <c>orders</c>,
/// then loads the rows with <c>SqlBulkCopy</c> in one transaction. The type map is the library's (MssqlDialect remarks,
/// the Docker tier's <c>SqlEngine</c>): BIGINT, NVARCHAR(450), DECIMAL(12,2) for amount, DATETIME2(3) (never the coarse DATETIME),
/// DATE, BIT. The groupable columns (country, category, status in orders.schema.json) carry the binary collation
/// <c>Latin1_General_100_BIN2</c> (GRP-2's host obligation: GROUP BY buckets and drill-down equality must be exact, and the stock
/// collation folds case); the other text keeps the server's default, case-insensitive one.
/// </summary>
public static class SqlServerSeeder
{
    /// <summary>The database used when the connection string names none.</summary>
    public const string DefaultDatabase = "demo";

    private const string BinaryCollation = "Latin1_General_100_BIN2";

    private static readonly string Ddl = $"""
        DROP TABLE IF EXISTS orders;
        CREATE TABLE orders (
            id          BIGINT PRIMARY KEY,
            order_no    NVARCHAR(450) NOT NULL,
            customer    NVARCHAR(450) NULL,
            country     NVARCHAR(450) COLLATE {BinaryCollation} NULL,
            category    NVARCHAR(450) COLLATE {BinaryCollation} NULL,
            status      NVARCHAR(450) COLLATE {BinaryCollation} NOT NULL,
            amount      DECIMAL(12,2) NULL,
            quantity    BIGINT NULL,
            ordered_at  DATETIME2(3) NOT NULL,
            shipped_on  DATE NULL,
            is_priority BIT NOT NULL
        );
        """;

    public static async Task<long> SeedAsync(string connectionString, IReadOnlyList<OrderRow> rows)
    {
        var target = ConnectionStringGuard.Build(
            connectionString,
            value => new SqlConnectionStringBuilder(value),
            () => new SeedSettingException(ConnectionStringGuard.Message(DemoSettings.SqlServerEnvVar, "SQL Server")));
        if (string.IsNullOrWhiteSpace(target.InitialCatalog))
        {
            target.InitialCatalog = DefaultDatabase;
        }

        var database = target.InitialCatalog;
        // The first start can still be recovering databases for a moment: wait a little longer than the API does.
        target.ConnectTimeout = Math.Max(target.ConnectTimeout, 15);
        target.ConnectRetryCount = 0;

        await EnsureDatabaseAsync(target, database);

        await using var connection = new SqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using (var ddl = new SqlCommand(Ddl, connection, transaction))
        {
            await ddl.ExecuteNonQueryAsync();
        }

        using (var table = ToTable(rows))
        using (var bulk = new SqlBulkCopy(connection, SqlBulkCopyOptions.Default, transaction)
        {
            DestinationTableName = "orders",
            BatchSize = 5000,
            BulkCopyTimeout = 120
        })
        {
            // Explicit by name: never by position, so a column order change in either place cannot shift a value.
            foreach (DataColumn column in table.Columns)
            {
                bulk.ColumnMappings.Add(column.ColumnName, column.ColumnName);
            }

            await bulk.WriteToServerAsync(table);
        }

        await using (var indexes = new SqlCommand("""
            CREATE INDEX orders_country_idx ON orders (country);
            CREATE INDEX orders_category_idx ON orders (category);
            CREATE INDEX orders_ordered_at_idx ON orders (ordered_at);
            """, connection, transaction))
        {
            await indexes.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();

        await using var count = new SqlCommand("SELECT COUNT_BIG(*) FROM orders", connection);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    /// <summary>Creates the database through <c>master</c> when it does not exist yet; the name is bracket-quoted.</summary>
    private static async Task EnsureDatabaseAsync(SqlConnectionStringBuilder target, string database)
    {
        var master = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();

        var quoted = "[" + database.Replace("]", "]]", StringComparison.Ordinal) + "]";
        await using var command = new SqlCommand(
            $"IF DB_ID(@name) IS NULL CREATE DATABASE {quoted};", connection);
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = database });
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>The rows as a typed table. A DataTable has no DateOnly: the date goes as a DateTime at midnight into a DATE column,
    /// and the instant keeps its millisecond precision for DATETIME2(3).</summary>
    private static DataTable ToTable(IReadOnlyList<OrderRow> rows)
    {
        var table = new DataTable("orders");
        table.Columns.Add("id", typeof(long));
        table.Columns.Add("order_no", typeof(string));
        table.Columns.Add("customer", typeof(string));
        table.Columns.Add("country", typeof(string));
        table.Columns.Add("category", typeof(string));
        table.Columns.Add("status", typeof(string));
        table.Columns.Add("amount", typeof(decimal));
        table.Columns.Add("quantity", typeof(long));
        table.Columns.Add("ordered_at", typeof(DateTime));
        table.Columns.Add("shipped_on", typeof(DateTime));
        table.Columns.Add("is_priority", typeof(bool));

        foreach (var row in rows)
        {
            table.Rows.Add(
                row.Id,
                row.OrderNo,
                Bind(row.Customer),
                Bind(row.Country),
                Bind(row.Category),
                row.Status,
                Bind(row.Amount),
                Bind(row.Quantity),
                row.OrderedAt,
                row.ShippedOn is { } date ? date.ToDateTime(TimeOnly.MinValue) : DBNull.Value,
                row.IsPriority);
        }

        return table;
    }

    private static object Bind<T>(T? value) where T : struct => value is { } present ? present : DBNull.Value;

    private static object Bind(string? value) => value is null ? DBNull.Value : value;
}
