using System.Data.Common;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Craticula.Core.Schema;
using Craticula.Integration.Tests.Oracle;
using Craticula.Sql;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using MySqlConnector;
using Npgsql;
using NpgsqlTypes;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Xunit;

namespace Craticula.Integration.Tests.Containers.Sql;

/// <summary>
/// One SQL engine of the execution-equality tier (T-1b, #2206): the engine's own DDL, its own seed insert and its own
/// ADO driver, behind one shape so the runner is the same for all four. A container engine starts its container once,
/// lazily on the first fixture, so a run without Docker (every test skipped) never pulls an image; the xUnit collection
/// fixture disposes it at the end. Every fixture gets a fresh table (dropped, created, seeded).
///
/// <para>The DDL here is part of what the tier tests (<c>spec/semantics.md</c> SEM-1, GRP-2, SQL-6): each engine's
/// collation choice is spelled once, in <c>TextColumnClause</c> / <see cref="PrepareDatabaseAsync"/>, and
/// explained in <c>Sql/README.md</c>.</para>
/// </summary>
public abstract class SqlEngine : IAsyncLifetime
{
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _started;

    /// <summary>The <c>expected/&lt;backend&gt;.artifact.json</c> stem.</summary>
    public abstract string Backend { get; }

    public abstract ISqlDialect Dialect { get; }

    /// <summary>Starts the engine (container or in-process); called once, before the first connection.</summary>
    protected abstract Task StartAsync();

    protected abstract Task StopAsync();

    /// <summary>Opens a new connection to the started engine.</summary>
    protected abstract Task<DbConnection> OpenConnectionAsync();

    /// <summary>One-time host DDL, run after start (Postgres' <c>craticula_ci</c> collation).</summary>
    protected virtual Task PrepareDatabaseAsync(DbConnection connection) => Task.CompletedTask;

    protected abstract string Quote(string identifier);

    protected abstract string ColumnType(FieldType type);

    /// <summary>Appended to a <c>string</c> column's type: the collation the host DDL declares for that field (SEM-1, SQL-6, GRP-2).</summary>
    protected virtual string TextColumnClause(FieldConfig field) => string.Empty;

    /// <summary>Text appended after the column list of <c>CREATE TABLE</c> (MySQL's table collation).</summary>
    protected virtual string TableOptions => string.Empty;

    /// <summary>The placeholder for the zero-based seed parameter <paramref name="index"/> in an <c>INSERT</c>.</summary>
    protected abstract string SeedPlaceholder(int index);

    /// <summary>Adds <paramref name="value"/> (a CLR value, or a nested list for a set membership) as parameter <paramref name="index"/>.</summary>
    protected abstract void Bind(DbCommand command, int index, object? value);

    /// <summary>Rewrites the compiled text into what the driver accepts (SQLite's bare <c>?</c> becomes <c>?1</c>…).</summary>
    protected virtual string PrepareSql(string sql, int parameterCount) => sql;

    protected abstract DbCommand CreateCommand(DbConnection connection);

    async Task IAsyncLifetime.InitializeAsync() => await Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_started)
        {
            await StopAsync();
        }
    }

    private async Task<DbConnection> ConnectAsync()
    {
        await _startGate.WaitAsync();
        try
        {
            if (!_started)
            {
                await StartAsync();
                _started = true;
                await using var host = await OpenConnectionAsync();
                await PrepareDatabaseAsync(host);
            }
        }
        finally
        {
            _startGate.Release();
        }

        return await OpenConnectionAsync();
    }

    /// <summary>Creates the fixture's table from the schema's physical SQL names and types, seeds it, runs the compiled query.</summary>
    public async Task<EngineResult> RunAsync(ExecutionFixture fixture, SqlQuery query)
    {
        await using var connection = await ConnectAsync();
        await CreateAndSeedAsync(connection, fixture);
        return await ExecuteQueryAsync(connection, query);
    }

    /// <summary>
    /// Starts the engine if needed, creates and seeds the fixture's table, and hands back the OPEN connection for a
    /// host-port test that runs the query through an adapter (Dapper) instead of <see cref="RunAsync"/>. The caller disposes it.
    /// </summary>
    public async Task<DbConnection> OpenSeededConnectionAsync(ExecutionFixture fixture)
    {
        var connection = await ConnectAsync();
        await CreateAndSeedAsync(connection, fixture);
        return connection;
    }

    /// <summary>
    /// Starts the engine if needed and creates and seeds the fixture's table once, leaving it for any number of
    /// <see cref="QueryAsync"/> calls (the parity property seeds once per run and runs every generated case against it).
    /// </summary>
    public async Task SeedAsync(ExecutionFixture fixture)
    {
        await using var connection = await ConnectAsync();
        await CreateAndSeedAsync(connection, fixture);
    }

    /// <summary>Runs a compiled query against what <see cref="SeedAsync"/> left behind.</summary>
    public async Task<EngineResult> QueryAsync(SqlQuery query)
    {
        await using var connection = await ConnectAsync();
        return await ExecuteQueryAsync(connection, query);
    }

    private async Task<EngineResult> ExecuteQueryAsync(DbConnection connection, SqlQuery query)
    {
        var sql = SqlMutation.Apply(query.Sql);
        await using var command = CreateCommand(connection);
        command.CommandText = PrepareSql(sql, query.Parameters.Count);
        for (var index = 0; index < query.Parameters.Count; index++)
        {
            Bind(command, index, query.Parameters[index]);
        }

        await using var reader = await command.ExecuteReaderAsync();
        var columns = new List<string>(reader.FieldCount);
        for (var index = 0; index < reader.FieldCount; index++)
        {
            columns.Add(reader.GetName(index));
        }

        var rows = new List<IReadOnlyList<object?>>();
        while (await reader.ReadAsync())
        {
            var cells = new object?[reader.FieldCount];
            for (var index = 0; index < cells.Length; index++)
            {
                cells[index] = await reader.IsDBNullAsync(index) ? null : reader.GetValue(index);
            }

            rows.Add(cells);
        }

        return new EngineResult(columns, rows, sql);
    }

    private async Task CreateAndSeedAsync(DbConnection connection, ExecutionFixture fixture)
    {
        var schema = fixture.Schema;
        var table = Quote(schema.Source);

        await ExecuteAsync(connection, $"DROP TABLE IF EXISTS {table}");

        var ddl = new StringBuilder("CREATE TABLE ").Append(table).Append(" (");
        for (var index = 0; index < schema.FieldOrder.Count; index++)
        {
            var field = schema.FieldOrder[index];
            var type = schema.Fields[field].Type;
            ddl.Append(index > 0 ? ", " : string.Empty)
                .Append(Quote(PhysicalColumn(schema, field))).Append(' ').Append(ColumnType(type));
            if (type == FieldType.String)
            {
                ddl.Append(TextColumnClause(schema.Fields[field]));
            }
        }

        // Every column is nullable, the stable key included: SEM-2's null handling is what the seeds exercise, and
        // XEQ-2 (a unique, non-null stableKey) is a property of the seed, checked by the fixture loader's tests.
        ddl.Append(')').Append(TableOptions);
        await ExecuteAsync(connection, ddl.ToString());

        var columns = string.Join(", ", schema.FieldOrder.Select(field => Quote(PhysicalColumn(schema, field))));
        var placeholders = string.Join(", ", schema.FieldOrder.Select((_, index) => SeedPlaceholder(index)));
        var insert = $"INSERT INTO {table} ({columns}) VALUES ({placeholders})";

        foreach (var row in fixture.Seed)
        {
            await using var command = CreateCommand(connection);
            command.CommandText = insert;
            for (var index = 0; index < schema.FieldOrder.Count; index++)
            {
                var field = schema.FieldOrder[index];
                Bind(command, index, SeedValue(schema.Fields[field].Type, row.Cells[field]));
            }

            await command.ExecuteNonQueryAsync();
        }
    }

    protected async Task ExecuteAsync(DbConnection connection, string sql)
    {
        await using var command = CreateCommand(connection);
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    internal static string PhysicalColumn(GridSchema schema, string field) =>
        schema.Fields[field].Physical?.Sql?.Column ?? field;

    /// <summary>A seed cell (XEQ-2's JSON form) as the CLR value a driver binds: <c>null</c>, long, double, decimal, bool, string, <see cref="DateOnly"/> or a UTC <see cref="DateTime"/>.</summary>
    private static object? SeedValue(FieldType type, JsonElement cell)
    {
        if (cell.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return type switch
        {
            FieldType.Int64 => cell.GetInt64(),
            FieldType.Double => cell.GetDouble(),
            FieldType.Decimal => decimal.Parse(cell.GetString()!, NumberStyles.Float, CultureInfo.InvariantCulture),
            FieldType.Boolean => cell.GetBoolean(),
            FieldType.String => cell.GetString(),
            FieldType.Date => DateOnly.ParseExact(cell.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Datetime => DateTime.SpecifyKind(
                DateTimeOffset.Parse(cell.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal).UtcDateTime,
                DateTimeKind.Utc),
            _ => throw new InvalidOperationException($"Unmapped FieldType '{type}'.")
        };
    }
}

/// <summary>What an engine returned for the compiled query: the reader's column names, the rows, and the SQL actually sent (after any test-only mutation).</summary>
public sealed record EngineResult(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<object?>> Rows, string SentSql);

/// <summary>
/// A test-only fault injection, switched by <c>CRATICULA_XEQ_MUTATION=&lt;name&gt;</c>, for the mutation Definition of
/// Done (#1655, #1656): each name damages the compiled SQL the way a real compiler defect would, so a tier or a property
/// that cannot see the damage shows up as passing under it. Never set in a normal run. The names:
/// <list type="bullet">
/// <item><c>drop-collate</c>: removes every <c>COLLATE &lt;name&gt;</c> from the compiled SQL before it is sent (and, on MySQL,
/// which spells no collation in SQL, declares the string columns <c>utf8mb4_bin</c> instead, the DDL equivalent);</item>
/// <item><c>drop-nulls</c>: removes every <c>NULLS FIRST</c> / <c>NULLS LAST</c> (SEM-3's null placement) from the SQL of the engines that
/// spell it that way, PostgreSQL and SQLite, leaving each engine's own default;</item>
/// <item><c>drop-tiebreaker</c>: removes the final stableKey key SEM-6 appends to an ORDER BY that has other keys, on every SQL engine.</item>
/// </list>
/// </summary>
public static class SqlMutation
{
    public const string Variable = "CRATICULA_XEQ_MUTATION";

    private static readonly System.Text.RegularExpressions.Regex Nulls =
        new(@"\s+NULLS\s+(FIRST|LAST)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // The id key of each dialect's spelling (PostgreSQL/SQLite, MySQL, SQL Server), as the last key before the page clause.
    private static readonly System.Text.RegularExpressions.Regex Tiebreaker = new(
        @",\s*(?:""id""\s+ASC\s+NULLS\s+LAST|ISNULL\(`id`\)\s+ASC,\s*`id`\s+ASC|CASE\s+WHEN\s+\[id\]\s+IS\s+NULL\s+THEN\s+1\s+ELSE\s+0\s+END\s+ASC,\s*\[id\]\s+ASC)(?=\s+(?:LIMIT|OFFSET)\b)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static string? Name => Environment.GetEnvironmentVariable(Variable);

    private static bool Is(string name) => string.Equals(Name, name, StringComparison.OrdinalIgnoreCase);

    public static bool DropCollate => Is("drop-collate");

    public static string Apply(string sql) => Apply(sql, Name);

    /// <summary>The same rewrite for a named mutation (or none), without reading the environment, so it can be tested.</summary>
    public static string Apply(string sql, string? mutation)
    {
        if (string.Equals(mutation, "drop-collate", StringComparison.OrdinalIgnoreCase))
        {
            return System.Text.RegularExpressions.Regex.Replace(sql, @"\s+COLLATE\s+[A-Za-z0-9_\.]+", string.Empty);
        }

        if (string.Equals(mutation, "drop-nulls", StringComparison.OrdinalIgnoreCase))
        {
            return Nulls.Replace(sql, string.Empty);
        }

        return string.Equals(mutation, "drop-tiebreaker", StringComparison.OrdinalIgnoreCase) ? Tiebreaker.Replace(sql, string.Empty) : sql;
    }
}

/// <summary>
/// PostgreSQL 16. SEM-1's insensitive mechanism is the host-created ICU collation <c>craticula_ci</c> (semantics.md
/// SEM-1, nondeterministic, level 2); the database's own collation stays the image default, which SQL-6 calls
/// "deterministic" (it is the exact comparison GRP-2 and the stableKey tiebreaker rely on).
/// </summary>
public sealed class PostgresEngine : SqlEngine
{
    public const string Image = "postgres:16.15";

    private PostgreSqlContainer? _container;

    public override string Backend => "postgres";

    public override ISqlDialect Dialect => PostgresDialect.Instance;

    protected override async Task StartAsync()
    {
        _container = new PostgreSqlBuilder(Image).Build();
        await _container.StartAsync();
    }

    protected override async Task StopAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    protected override async Task<DbConnection> OpenConnectionAsync()
    {
        var connection = new NpgsqlConnection(_container!.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    protected override Task PrepareDatabaseAsync(DbConnection connection) =>
        ExecuteAsync(connection, "CREATE COLLATION IF NOT EXISTS craticula_ci (provider = icu, locale = 'und-u-ks-level2', deterministic = false)");

    protected override string Quote(string identifier) => $"\"{identifier}\"";

    protected override string ColumnType(FieldType type) => type switch
    {
        FieldType.Int64 => "BIGINT",
        FieldType.Double => "DOUBLE PRECISION",
        FieldType.Decimal => "NUMERIC(18,4)",
        FieldType.Boolean => "BOOLEAN",
        FieldType.String => "TEXT",
        FieldType.Date => "DATE",
        FieldType.Datetime => "TIMESTAMPTZ",
        _ => throw new InvalidOperationException($"Unmapped FieldType '{type}'.")
    };

    protected override string SeedPlaceholder(int index) => $"${index + 1}";

    protected override DbCommand CreateCommand(DbConnection connection) => new NpgsqlCommand { Connection = (NpgsqlConnection)connection };

    // Positional $n with unnamed parameters, in order. A compiled string parameter is sent with an UNKNOWN type so the
    // server infers it from the column it meets (a date or decimal arrives as a string, AR-7); a nested list is the
    // single array parameter of "= ANY($n)".
    protected override void Bind(DbCommand command, int index, object? value)
    {
        var parameter = value switch
        {
            null => new NpgsqlParameter { Value = DBNull.Value, NpgsqlDbType = NpgsqlDbType.Unknown },
            string text => new NpgsqlParameter { Value = text, NpgsqlDbType = NpgsqlDbType.Unknown },
            IReadOnlyList<object?> list => ArrayParameter(list),
            _ => new NpgsqlParameter { Value = value }
        };

        command.Parameters.Add(parameter);
    }

    // Typed arrays for string, int64, double and boolean only. decimal, date and datetime arrays are intentionally NOT
    // typed here: the compiler binds those values as strings (SQL-4, AR-7), and Bug #2304 (PostgreSQL refuses
    // numeric/date/timestamptz = text) must stay visible; the parity generator excludes those sets until it is fixed.
    private static NpgsqlParameter ArrayParameter(IReadOnlyList<object?> list) =>
        list.All(item => item is string)
            ? new NpgsqlParameter { Value = list.Select(item => (string)item!).ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Text }
            : list.All(item => item is long)
                ? new NpgsqlParameter { Value = list.Select(item => (long)item!).ToArray() }
                : list.All(item => item is double)
                    ? new NpgsqlParameter { Value = list.Select(item => (double)item!).ToArray() }
                    : list.All(item => item is bool)
                        ? new NpgsqlParameter { Value = list.Select(item => (bool)item!).ToArray() }
                        : throw new NotSupportedException("The harness binds a set-membership array of strings, int64, doubles or booleans only.");
}

/// <summary>
/// SQL Server 2022. SQL-6: the insensitive and the sensitive direction are both spelled in the statement
/// (<c>COLLATE Latin1_General_CI_AS</c> / <c>_CS_AS</c>); a bucket-identity key spells nothing, so it takes the column's
/// collation: the DDL declares a groupable column <c>Latin1_General_100_BIN2</c> (GRP-2's host obligation, so buckets and
/// drill-down matches are exact) and every other string column the stock <c>SQL_Latin1_General_CP1_CI_AS</c>.
/// </summary>
public sealed class MssqlEngine : SqlEngine
{
    public const string Image = "mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04";

    private MsSqlContainer? _container;

    public override string Backend => "mssql";

    public override ISqlDialect Dialect => MssqlDialect.Instance;

    protected override async Task StartAsync()
    {
        _container = new MsSqlBuilder(Image).Build();
        await _container.StartAsync();
    }

    protected override async Task StopAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    protected override async Task<DbConnection> OpenConnectionAsync()
    {
        var connection = new SqlConnection(_container!.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    protected override string Quote(string identifier) => $"[{identifier}]";

    protected override string ColumnType(FieldType type) => type switch
    {
        FieldType.Int64 => "BIGINT",
        FieldType.Double => "FLOAT",
        FieldType.Decimal => "DECIMAL(18,4)",
        FieldType.Boolean => "BIT",
        FieldType.String => "NVARCHAR(450)",
        FieldType.Date => "DATE",
        FieldType.Datetime => "DATETIME2(3)",
        _ => throw new InvalidOperationException($"Unmapped FieldType '{type}'.")
    };

    // GRP-2's host obligation: SQL Server's stock default folds case, so a groupable column is declared with a binary
    // collation to make its buckets and drill-down matches exact; every other string column is the stock CI default,
    // spelled out. (The inline COLLATE of SEM-1/SQL-6 does not depend on the column's own collation.)
    protected override string TextColumnClause(FieldConfig field) =>
        field.Groupable ? " COLLATE Latin1_General_100_BIN2" : " COLLATE SQL_Latin1_General_CP1_CI_AS";

    protected override string SeedPlaceholder(int index) => $"@p{index + 1}";

    protected override DbCommand CreateCommand(DbConnection connection) => new SqlCommand { Connection = (SqlConnection)connection };

    // Named @p1..@pn, the dialect's own placeholder (the seed insert uses the same names). SqlClient has no DateOnly, and a bare DateTime
    // would be inferred as the coarse DATETIME, so both are typed.
    protected override void Bind(DbCommand command, int index, object? value)
    {
        var name = $"@p{index + 1}";

        var parameter = value switch
        {
            null => new SqlParameter(name, DBNull.Value),
            DateOnly date => new SqlParameter(name, System.Data.SqlDbType.Date) { Value = date.ToDateTime(TimeOnly.MinValue) },
            DateTime instant => new SqlParameter(name, System.Data.SqlDbType.DateTime2) { Value = instant },
            IReadOnlyList<object?> => throw new NotSupportedException("SQL Server's compiled text flattens a set membership; a list parameter is a compiler change."),
            _ => new SqlParameter(name, value)
        };

        command.Parameters.Add(parameter);
    }
}

/// <summary>
/// MySQL 8.4. SEM-1's insensitive mechanism is the column's own collation (SQL-6: nothing in the statement), so the DDL
/// declares the table <c>utf8mb4_0900_ai_ci</c>, the example semantics.md SEM-1 names. The case-sensitive direction
/// has no portable spelling and is a host DDL obligation, which this harness meets in the DDL: a groupable or case-sensitive
/// field's column is declared <c>utf8mb4_bin</c>.
/// </summary>
public sealed class MySqlEngine : SqlEngine
{
    public const string Image = "mysql:8.4.11";

    private MySqlContainer? _container;

    public override string Backend => "mysql";

    public override ISqlDialect Dialect => MySqlDialect.Instance;

    protected override async Task StartAsync()
    {
        _container = new MySqlBuilder(Image).Build();
        await _container.StartAsync();
    }

    protected override async Task StopAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    protected override async Task<DbConnection> OpenConnectionAsync()
    {
        var connection = new MySqlConnection(_container!.GetConnectionString());
        await connection.OpenAsync();
        return connection;
    }

    protected override string Quote(string identifier) => $"`{identifier}`";

    protected override string ColumnType(FieldType type) => type switch
    {
        FieldType.Int64 => "BIGINT",
        FieldType.Double => "DOUBLE",
        FieldType.Decimal => "DECIMAL(18,4)",
        FieldType.Boolean => "BOOLEAN",
        FieldType.String => "VARCHAR(1000)",
        FieldType.Date => "DATE",
        FieldType.Datetime => "DATETIME(3)",
        _ => throw new InvalidOperationException($"Unmapped FieldType '{type}'.")
    };

    // The mutation's DDL equivalent of dropping COLLATE: a binary column collation everywhere folds nothing.
    private static string Collation => SqlMutation.DropCollate ? "utf8mb4_bin" : "utf8mb4_0900_ai_ci";

    protected override string TableOptions => $" DEFAULT CHARACTER SET utf8mb4 COLLATE {Collation}";

    // SQL-6 and GRP-2's host obligation: MySQL has no portable in-statement spelling, so a field declared
    // caseSensitivity "sensitive" needs a binary column collation, and so does a groupable column (exact buckets and
    // drill-down matches). Every other string column keeps the table's insensitive collation.
    protected override string TextColumnClause(FieldConfig field) =>
        field.Groupable || field.Text?.CaseSensitivity == CaseSensitivity.Sensitive
            ? " CHARACTER SET utf8mb4 COLLATE utf8mb4_bin"
            : $" CHARACTER SET utf8mb4 COLLATE {Collation}";

    protected override string SeedPlaceholder(int index) => "?";

    protected override DbCommand CreateCommand(DbConnection connection) => new MySqlCommand { Connection = (MySqlConnection)connection };

    // Bare ? is positional: MySqlConnector binds by order whatever the name.
    protected override void Bind(DbCommand command, int index, object? value)
    {
        if (value is IReadOnlyList<object?>)
        {
            throw new NotSupportedException("MySQL's compiled text flattens a set membership; a list parameter is a compiler change.");
        }

        command.Parameters.Add(new MySqlParameter { ParameterName = $"p{index}", Value = value ?? DBNull.Value });
    }
}

/// <summary>
/// SQLite, in-process (no container). SQL-6: the insensitive mechanism is <c>COLLATE NOCASE</c> in the statement; the
/// default sequence stays <c>BINARY</c>. SQLite has no decimal, date or datetime type: decimal is a NUMERIC-affinity
/// column (a bound text decimal is converted by the column's affinity), date and datetime are TEXT in the canonical
/// forms the host stores and the compiler binds (<c>yyyy-MM-dd</c>, <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>, SEM-4).
/// </summary>
public sealed class SqliteEngine : SqlEngine
{
    // One shared in-memory database per run, so the host connection and each test's connection see the same data:
    // an in-memory database lives only while a connection to it is open.
    private SqliteConnection? _keeper;

    public override string Backend => "sqlite";

    public override ISqlDialect Dialect => SqliteDialect.Instance;

    private const string ConnectionString = "DataSource=craticula-xeq;Mode=Memory;Cache=Shared";

    protected override Task StartAsync()
    {
        _keeper = new SqliteConnection(ConnectionString);
        _keeper.Open();
        return Task.CompletedTask;
    }

    protected override Task StopAsync()
    {
        _keeper?.Dispose();
        return Task.CompletedTask;
    }

    protected override async Task<DbConnection> OpenConnectionAsync()
    {
        var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    protected override string Quote(string identifier) => $"\"{identifier}\"";

    protected override string ColumnType(FieldType type) => type switch
    {
        FieldType.Int64 or FieldType.Boolean => "INTEGER",
        FieldType.Double => "REAL",
        FieldType.Decimal => "NUMERIC",
        FieldType.String or FieldType.Date or FieldType.Datetime => "TEXT",
        _ => throw new InvalidOperationException($"Unmapped FieldType '{type}'.")
    };

    protected override string SeedPlaceholder(int index) => $"?{index + 1}";

    protected override DbCommand CreateCommand(DbConnection connection) => new SqliteCommand { Connection = (SqliteConnection)connection };

    protected override string PrepareSql(string sql, int parameterCount)
    {
        var numbered = new StringBuilder(sql.Length + parameterCount * 2);
        var ordinal = 0;
        foreach (var character in sql)
        {
            numbered.Append(character);
            if (character == '?')
            {
                numbered.Append(++ordinal);
            }
        }

        return ordinal == parameterCount
            ? numbered.ToString()
            : throw new InvalidOperationException($"Found {ordinal} '?' placeholders but the compiled query carries {parameterCount} parameters.");
    }

    protected override void Bind(DbCommand command, int index, object? value)
    {
        object? bound = value switch
        {
            null => DBNull.Value,
            DateOnly date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            DateTime instant => instant.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture), // SEM-4's host obligation: exactly three digits
            IReadOnlyList<object?> => throw new NotSupportedException("SQLite's compiled text flattens a set membership; a list parameter is a compiler change."),
            _ => value
        };

        command.Parameters.Add(new SqliteParameter($"?{index + 1}", bound));
    }
}
