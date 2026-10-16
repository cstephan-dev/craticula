using Npgsql;
using NpgsqlTypes;

namespace Demo.Api.Seed;

/// <summary>Drops and recreates table <c>orders</c>, then loads the rows with binary COPY in one transaction.
/// datetime is timestamptz (an absolute instant, SEM-4), shipped_on is date, amount is numeric(12,2).</summary>
public static class PostgresSeeder
{
    // craticula_ci is the host DDL the PostgreSQL dialect requires (SEM-1): the case-insensitive ICU collation that text
    // equals/in/sort compile against. Without it every `equals` filter on a text column fails with 42704.
    private const string Ddl = """
        CREATE COLLATION IF NOT EXISTS craticula_ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false);
        DROP TABLE IF EXISTS orders;
        CREATE TABLE orders (
            id          bigint PRIMARY KEY,
            order_no    text NOT NULL,
            customer    text,
            country     text,
            category    text,
            status      text NOT NULL,
            amount      numeric(12,2),
            quantity    bigint,
            ordered_at  timestamptz NOT NULL,
            shipped_on  date,
            is_priority boolean NOT NULL
        );
        """;

    public static async Task<long> SeedAsync(string connectionString, IReadOnlyList<OrderRow> rows)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var ddl = new NpgsqlCommand(Ddl, connection, transaction))
        {
            await ddl.ExecuteNonQueryAsync();
        }

        await using (var writer = await connection.BeginBinaryImportAsync(
            "COPY orders (id, order_no, customer, country, category, status, amount, quantity, ordered_at, shipped_on, is_priority) FROM STDIN (FORMAT BINARY)"))
        {
            foreach (var row in rows)
            {
                await writer.StartRowAsync();
                await writer.WriteAsync(row.Id, NpgsqlDbType.Bigint);
                await writer.WriteAsync(row.OrderNo, NpgsqlDbType.Text);
                await writer.WriteAsync(row.Customer, NpgsqlDbType.Text);
                await writer.WriteAsync(row.Country, NpgsqlDbType.Text);
                await writer.WriteAsync(row.Category, NpgsqlDbType.Text);
                await writer.WriteAsync(row.Status, NpgsqlDbType.Text);
                await writer.WriteAsync(row.Amount, NpgsqlDbType.Numeric);
                await writer.WriteAsync(row.Quantity, NpgsqlDbType.Bigint);
                await writer.WriteAsync(row.OrderedAt, NpgsqlDbType.TimestampTz);
                await writer.WriteAsync(row.ShippedOn, NpgsqlDbType.Date);
                await writer.WriteAsync(row.IsPriority, NpgsqlDbType.Boolean);
            }

            await writer.CompleteAsync();
        }

        await using (var indexes = new NpgsqlCommand("""
            CREATE INDEX orders_country_idx ON orders (country);
            CREATE INDEX orders_category_idx ON orders (category);
            CREATE INDEX orders_ordered_at_idx ON orders (ordered_at);
            """, connection, transaction))
        {
            await indexes.ExecuteNonQueryAsync();
        }

        await transaction.CommitAsync();

        await using var count = new NpgsqlCommand("SELECT count(*) FROM orders", connection);
        return (long)(await count.ExecuteScalarAsync())!;
    }
}
