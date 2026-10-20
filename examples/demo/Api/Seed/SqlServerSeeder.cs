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
/// <para>Least privilege (#2525): the seed needs rights to create a database and a table, the API only reads. With an admin connection
/// string (<c>DEMO_SQLSERVER_ADMIN</c>) the seed runs as that login and then creates, in the demo database, the login named by the
/// ordinary string (<c>DEMO_SQLSERVER</c>) as a member of <c>db_datareader</c> only; the API connects as that one. Without an admin
/// string the ordinary string is used for everything, as before.</para>
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

    public static async Task<long> SeedAsync(string connectionString, IReadOnlyList<OrderRow> rows, string? adminConnectionString = null)
    {
        var reader = ConnectionStringGuard.Build(
            connectionString,
            value => new SqlConnectionStringBuilder(value),
            () => new SeedSettingException(ConnectionStringGuard.Message(DemoSettings.SqlServerEnvVar, "SQL Server")));
        var target = adminConnectionString is null
            ? reader
            : ConnectionStringGuard.Build(
                adminConnectionString,
                value => new SqlConnectionStringBuilder(value),
                () => new SeedSettingException(ConnectionStringGuard.Message(DemoSettings.SqlServerAdminEnvVar, "SQL Server")));
        if (string.IsNullOrWhiteSpace(target.InitialCatalog))
        {
            target.InitialCatalog = DefaultDatabase;
        }

        if (adminConnectionString is not null)
        {
            ValidateReader(target, reader);
        }

        var database = target.InitialCatalog;
        if (adminConnectionString is not null
            && !string.IsNullOrWhiteSpace(reader.InitialCatalog)
            && !string.Equals(reader.InitialCatalog, database, StringComparison.OrdinalIgnoreCase))
        {
            throw new SeedSettingException(
                $"{DemoSettings.SqlServerEnvVar} names another database than {DemoSettings.SqlServerAdminEnvVar}: the read-only login is created in the seeded one.");
        }

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

        if (adminConnectionString is not null)
        {
            await EnsureReaderAsync(target, reader, database);
        }

        await using var count = new SqlCommand("SELECT COUNT_BIG(*) FROM orders", connection);
        return (long)(await count.ExecuteScalarAsync())!;
    }

    /// <summary>The read-only login needs a name and a password, and must not be the administrator itself; checked before any connection is made.</summary>
    private static void ValidateReader(SqlConnectionStringBuilder admin, SqlConnectionStringBuilder reader)
    {
        if (string.IsNullOrWhiteSpace(reader.UserID) || string.IsNullOrEmpty(reader.Password))
        {
            throw new SeedSettingException(
                $"{DemoSettings.SqlServerEnvVar} needs a User Id and a Password: with {DemoSettings.SqlServerAdminEnvVar} set, the seed creates that read-only login.");
        }

        if (string.Equals(reader.UserID, admin.UserID, StringComparison.OrdinalIgnoreCase))
        {
            throw new SeedSettingException(
                $"{DemoSettings.SqlServerEnvVar} names the same login as {DemoSettings.SqlServerAdminEnvVar}: use a separate read-only login for the API.");
        }
    }

    /// <summary>
    /// Creates (or re-keys) the login the API connects with and makes it a member of <c>db_datareader</c> in the demo database, nothing
    /// more: it can select from <c>orders</c> and cannot write, create or drop anything. The name and password come from the ordinary
    /// connection string, so no credential lives in code. The names are bracket-quoted and the password is escaped as a literal
    /// (<c>CREATE LOGIN</c> takes no parameter); both are the operator's own local settings.
    /// </summary>
    private static async Task EnsureReaderAsync(SqlConnectionStringBuilder admin, SqlConnectionStringBuilder reader, string database)
    {
        var user = reader.UserID;
        var login = Quote(user);
        var pw = "N'" + reader.Password.Replace("'", "''", StringComparison.Ordinal) + "'";
        var master = new SqlConnectionStringBuilder(admin.ConnectionString) { InitialCatalog = "master" };

        try
        {
            // Refuse before changing anything: an existing login that holds more than db_datareader (a server role, another database
            // role) is somebody's real account; re-keying it would lock them out and the API would run with its rights.
            await using (var connection = new SqlConnection(master.ConnectionString))
            {
                await connection.OpenAsync();
                await RefuseWhenAsync(
                    connection,
                    "SELECT COUNT(*) FROM sys.server_role_members m JOIN sys.server_principals l ON l.principal_id = m.member_principal_id WHERE l.name = @name",
                    user,
                    "is already a member of a server role");
            }

            await using (var connection = new SqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                await RefuseWhenAsync(
                    connection,
                    "SELECT COUNT(*) FROM sys.database_role_members rm JOIN sys.database_principals u ON u.principal_id = rm.member_principal_id " +
                    "JOIN sys.database_principals r ON r.principal_id = rm.role_principal_id WHERE u.name = @name AND r.name <> N'db_datareader'",
                    user,
                    $"is already a member of a role other than db_datareader in database {database}");
            }

            await using (var connection = new SqlConnection(master.ConnectionString))
            {
                await connection.OpenAsync();
                await using var command = new SqlCommand(
                    $"IF SUSER_ID(@name) IS NULL CREATE LOGIN {login} WITH PASSWORD = {pw}, CHECK_POLICY = OFF " +
                    $"ELSE ALTER LOGIN {login} WITH PASSWORD = {pw};", connection);
                command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = user });
                await command.ExecuteNonQueryAsync();
            }

            await using (var connection = new SqlConnection(admin.ConnectionString))
            {
                await connection.OpenAsync();
                // An existing user of that name may be orphaned (the login was dropped and re-created): WITH LOGIN re-maps it.
                await using var command = new SqlCommand(
                    $"IF USER_ID(@name) IS NULL CREATE USER {login} FOR LOGIN {login}; ELSE ALTER USER {login} WITH LOGIN = {login}; " +
                    $"ALTER ROLE db_datareader ADD MEMBER {login};",
                    connection);
                command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = user });
                await command.ExecuteNonQueryAsync();
            }
        }
        catch (SqlException ex)
        {
            // The server's text is left out: it can name the login, and the seed's own message already says what was being done.
            throw new SeedSettingException(
                $"Creating the read-only login for {DemoSettings.SqlServerEnvVar} failed (SQL Server error {ex.Number}). " +
                $"Does the {DemoSettings.SqlServerAdminEnvVar} login have the right to create logins and users?");
        }
    }

    private static async Task RefuseWhenAsync(SqlConnection connection, string countSql, string user, string reason)
    {
        await using var command = new SqlCommand(countSql, connection);
        command.Parameters.Add(new SqlParameter("@name", SqlDbType.NVarChar, 128) { Value = user });
        if (Convert.ToInt32(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture) > 0)
        {
            throw new SeedSettingException(
                $"The login named in {DemoSettings.SqlServerEnvVar} {reason}: the seed only manages a login that can read and nothing else. " +
                "Name a new login for the API (the seed creates it).");
        }
    }

    private static string Quote(string name) => "[" + name.Replace("]", "]]", StringComparison.Ordinal) + "]";

    /// <summary>Creates the database through <c>master</c> when it does not exist yet; the name is bracket-quoted.</summary>
    private static async Task EnsureDatabaseAsync(SqlConnectionStringBuilder target, string database)
    {
        var master = new SqlConnectionStringBuilder(target.ConnectionString) { InitialCatalog = "master" };
        await using var connection = new SqlConnection(master.ConnectionString);
        await connection.OpenAsync();

        var quoted = Quote(database);
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
