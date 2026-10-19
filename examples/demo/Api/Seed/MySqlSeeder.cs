using System.Text;
using MySqlConnector;

namespace Demo.Api.Seed;

/// <summary>
/// Drops and recreates table <c>orders</c> in the MySQL database, then loads the rows with batched multi-row <c>INSERT</c>
/// statements in one transaction (<c>MySqlBulkCopy</c> would need <c>AllowLoadLocalInfile</c> on the client and <c>local_infile</c>
/// on the server, which MySQL 8.4 switches off: a setting a demo should not ask a host to change). The type map is the library's
/// (the Docker tier's <c>MySqlEngine</c>): BIGINT, VARCHAR(1000) in utf8mb4, DECIMAL(12,2) for amount, DATETIME(3), DATE, BOOLEAN.
/// Case sensitivity is DDL-side on this engine (MySqlDialect remarks, SQL-6): the table default is the stock accent- and
/// case-insensitive <c>utf8mb4_0900_ai_ci</c> (what contains, equals and sort compile against), and the groupable columns
/// (country, category, status in orders.schema.json; none is declared case-sensitive) carry <c>utf8mb4_bin</c> so GROUP BY buckets
/// and drill-down equality are exact (GRP-2's host obligation). MySQL commits DDL on its own, so the table is created first, the
/// rows are loaded in one transaction, and the indexes are built afterwards (faster, and an index statement would end a transaction anyway).
/// </summary>
public static class MySqlSeeder
{
    /// <summary>The database used when the connection string names none (compose creates it).</summary>
    public const string DefaultDatabase = "demo";

    private const int BatchRows = 500;

    // utf8mb4 reserves four bytes per character, so VARCHAR(1000) is 4000 bytes, over InnoDB's 3072-byte index key limit:
    // the two text indexes below use a prefix, which still serves equality and the grouping scan.
    private const string Ddl = """
        DROP TABLE IF EXISTS orders;
        CREATE TABLE orders (
            id          BIGINT NOT NULL PRIMARY KEY,
            order_no    VARCHAR(1000) NOT NULL,
            customer    VARCHAR(1000) NULL,
            country     VARCHAR(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
            category    VARCHAR(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NULL,
            status      VARCHAR(1000) CHARACTER SET utf8mb4 COLLATE utf8mb4_bin NOT NULL,
            amount      DECIMAL(12,2) NULL,
            quantity    BIGINT NULL,
            ordered_at  DATETIME(3) NOT NULL,
            shipped_on  DATE NULL,
            is_priority BOOLEAN NOT NULL
        ) DEFAULT CHARACTER SET utf8mb4 COLLATE utf8mb4_0900_ai_ci;
        """;

    private const string Columns = "id, order_no, customer, country, category, status, amount, quantity, ordered_at, shipped_on, is_priority";

    public static async Task<long> SeedAsync(string connectionString, IReadOnlyList<OrderRow> rows)
    {
        var target = new MySqlConnectionStringBuilder(connectionString);
        if (string.IsNullOrWhiteSpace(target.Database))
        {
            target.Database = DefaultDatabase;
        }

        // The first start can still be initialising for a moment: wait a little longer than the API does.
        target.ConnectionTimeout = Math.Max(target.ConnectionTimeout, 15);

        await using var connection = new MySqlConnection(target.ConnectionString);
        await connection.OpenAsync();

        // The session zone is UTC so nothing converts the instants on the way in; DATETIME(3) holds the UTC wall clock.
        await ExecuteAsync(connection, null, "SET time_zone = '+00:00'");
        await ExecuteAsync(connection, null, Ddl);

        await using (var transaction = await connection.BeginTransactionAsync())
        {
            for (var start = 0; start < rows.Count; start += BatchRows)
            {
                await InsertBatchAsync(connection, transaction, rows, start, Math.Min(BatchRows, rows.Count - start));
            }

            await transaction.CommitAsync();
        }

        await ExecuteAsync(connection, null, """
            CREATE INDEX orders_country_idx ON orders (country(191));
            CREATE INDEX orders_category_idx ON orders (category(191));
            CREATE INDEX orders_ordered_at_idx ON orders (ordered_at);
            """);

        await using var count = new MySqlCommand("SELECT COUNT(*) FROM orders", connection);
        return Convert.ToInt64(await count.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task ExecuteAsync(MySqlConnection connection, MySqlTransaction? transaction, string sql)
    {
        await using var command = new MySqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>One multi-row INSERT with a parameter per value: the demo's text (a quoted line break, a non-ASCII name) never
    /// goes into the statement itself.</summary>
    private static async Task InsertBatchAsync(
        MySqlConnection connection, MySqlTransaction transaction, IReadOnlyList<OrderRow> rows, int start, int count)
    {
        var sql = new StringBuilder("INSERT INTO orders (").Append(Columns).Append(") VALUES ");
        await using var command = new MySqlCommand { Connection = connection, Transaction = transaction };
        for (var i = 0; i < count; i++)
        {
            var row = rows[start + i];
            sql.Append(i == 0 ? "(" : ", (");
            var values = new object?[]
            {
                row.Id, row.OrderNo, row.Customer, row.Country, row.Category, row.Status, row.Amount, row.Quantity,
                row.OrderedAt, row.ShippedOn is { } date ? date.ToDateTime(TimeOnly.MinValue) : null, row.IsPriority
            };
            for (var column = 0; column < values.Length; column++)
            {
                var name = $"@v{i}_{column}";
                sql.Append(column == 0 ? "" : ", ").Append(name);
                command.Parameters.Add(new MySqlParameter(name, values[column] ?? DBNull.Value));
            }

            sql.Append(')');
        }

        command.CommandText = sql.ToString();
        await command.ExecuteNonQueryAsync();
    }
}
