using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Demo.Api.Seed;

/// <summary>The SQLite engine in the bundled library is older than the 3.30 the dialect's native <c>NULLS FIRST/LAST</c> needs.</summary>
public sealed class SqliteVersionException(string message) : Exception(message);

/// <summary>
/// Drops and recreates table <c>orders</c> in the SQLite file, then loads the rows in one transaction. The type map is the
/// library's (SqliteDialect remarks, SEM-4): INTEGER, TEXT, NUMERIC for amount; dates as TEXT <c>yyyy-MM-dd</c>; datetimes as
/// TEXT <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> (exactly three digits and Z, or range filters mis-compare); booleans as 0/1.
/// </summary>
public static class SqliteSeeder
{
    /// <summary>The first release with native NULLS FIRST / NULLS LAST, which every compiled sqlite statement uses.</summary>
    public static readonly Version MinimumVersion = new(3, 30, 0);

    private const string Ddl = """
        DROP TABLE IF EXISTS orders;
        CREATE TABLE orders (
            id          INTEGER PRIMARY KEY,
            order_no    TEXT NOT NULL,
            customer    TEXT,
            country     TEXT,
            category    TEXT,
            status      TEXT NOT NULL,
            amount      NUMERIC,
            quantity    INTEGER,
            ordered_at  TEXT NOT NULL,
            shipped_on  TEXT,
            is_priority INTEGER NOT NULL
        );
        """;

    private const string Insert = """
        INSERT INTO orders (id, order_no, customer, country, category, status, amount, quantity, ordered_at, shipped_on, is_priority)
        VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9, ?10, ?11)
        """;

    public static async Task<long> SeedAsync(string path, IReadOnlyList<OrderRow> rows)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString;

        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await EnsureVersionAsync(connection);

        await using (var transaction = (SqliteTransaction)await connection.BeginTransactionAsync())
        {
            await using (var ddl = new SqliteCommand(Ddl, connection, transaction))
            {
                await ddl.ExecuteNonQueryAsync();
            }

            await using (var insert = new SqliteCommand(Insert, connection, transaction))
            {
                var parameters = new SqliteParameter[11];
                for (var i = 0; i < parameters.Length; i++)
                {
                    parameters[i] = new SqliteParameter($"?{i + 1}", null);
                    insert.Parameters.Add(parameters[i]);
                }

                foreach (var row in rows)
                {
                    parameters[0].Value = row.Id;
                    parameters[1].Value = row.OrderNo;
                    parameters[2].Value = Bind(row.Customer);
                    parameters[3].Value = Bind(row.Country);
                    parameters[4].Value = Bind(row.Category);
                    parameters[5].Value = row.Status;
                    parameters[6].Value = Bind(row.Amount);
                    parameters[7].Value = Bind(row.Quantity);
                    parameters[8].Value = FormatDatetime(row.OrderedAt);
                    parameters[9].Value = row.ShippedOn is { } date ? FormatDate(date) : DBNull.Value;
                    parameters[10].Value = row.IsPriority;
                    await insert.ExecuteNonQueryAsync();
                }
            }

            await using (var indexes = new SqliteCommand("""
                CREATE INDEX orders_country_idx ON orders (country);
                CREATE INDEX orders_category_idx ON orders (category);
                CREATE INDEX orders_ordered_at_idx ON orders (ordered_at);
                """, connection, transaction))
            {
                await indexes.ExecuteNonQueryAsync();
            }

            await transaction.CommitAsync();
        }

        await using var count = new SqliteCommand("SELECT count(*) FROM orders", connection);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    /// <summary>The canonical stored date: <c>yyyy-MM-dd</c>.</summary>
    public static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>The canonical stored instant: UTC, exactly three fraction digits, a literal Z (SEM-4's host obligation).</summary>
    public static string FormatDatetime(DateTime instant) =>
        instant.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    private static object Bind<T>(T? value) where T : struct => value is { } present ? (object)present : DBNull.Value;

    private static object Bind(string? value) => value is null ? DBNull.Value : value;

    private static async Task EnsureVersionAsync(SqliteConnection connection)
    {
        await using var command = new SqliteCommand("SELECT sqlite_version()", connection);
        var text = (string)(await command.ExecuteScalarAsync())!;
        if (!Version.TryParse(text, out var version) || version < MinimumVersion)
        {
            throw new SqliteVersionException(
                $"The SQLite engine in this build is {text}; the compiled sqlite statements need {MinimumVersion.Major}.{MinimumVersion.Minor} or later (native NULLS FIRST/LAST).");
        }
    }
}
