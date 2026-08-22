using System.Data;
using System.Globalization;
using System.Text;
using Craticula.Core.Schema;
using Craticula.Sql;
using Microsoft.Data.Sqlite;

namespace Craticula.Dapper.Tests;

/// <summary>
/// One SQLite in-memory database, created from a <see cref="GridSchema"/> and seeded with rows written
/// in the test that needs them — and both ways of reading it back: an INDEPENDENT raw-ADO oracle
/// (<see cref="ExecuteCompiled"/>, a hand-written reader that owes nothing to
/// <c>Craticula.Dapper</c>'s own code) and <see cref="Connection"/>, the SAME open connection handed to
/// <c>Craticula.Dapper</c>'s own entry points.
///
/// <para><b>One connection, one table, one set of stored bytes.</b> That is what makes a row difference
/// between the two paths attributable: they are not two databases that happen to hold similar data, they
/// are two READERS against the same rows, one hand-written and one production. Own INTERNAL copy of the
/// pattern <c>Craticula.EntityFrameworkCore.Tests.SqliteFixtureDatabase</c> establishes (each is
/// <see langword="internal"/> to its own assembly rather than a shared assembly for one small
/// helper).</para>
///
/// <para><b>These are NOT <c>dataset.json</c> fixtures, deliberately</b> — that filename and
/// <c>expectedRows.json</c> are reserved for #1655's execution-equality tier. The datasets in this suite
/// are inline in the test that reasons about them.</para>
/// </summary>
internal sealed class SqliteFixtureDatabase : IDisposable
{
    private readonly SqliteConnection connection;

    public GridSchema Schema { get; }

    /// <summary>The open connection <c>Craticula.Dapper</c>'s own entry points execute against — the
    /// SAME connection <see cref="ExecuteCompiled"/> reads from independently.</summary>
    public IDbConnection Connection => connection;

    /// <summary>How many rows were seeded — the denominator of the "this case is not a tautology" checks
    /// (a filter that returns all of them, or none, proves nothing).</summary>
    public int SeededRowCount { get; }

    private SqliteFixtureDatabase(SqliteConnection connection, GridSchema schema, int seededRowCount)
    {
        this.connection = connection;
        Schema = schema;
        SeededRowCount = seededRowCount;
    }

    /// <summary>
    /// Creates the table <see cref="GridSchema.Source"/> from the schema's own field declarations and
    /// inserts <paramref name="rows"/> (keyed by LOGICAL colId; a colId absent from a row is stored as
    /// NULL).
    /// </summary>
    public static SqliteFixtureDatabase Create(
        GridSchema schema, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        CreateTable(connection, schema);
        Insert(connection, schema, rows);

        return new SqliteFixtureDatabase(connection, schema, rows.Count);
    }

    /// <summary>
    /// Runs a compiled <see cref="SqlQuery"/> through a hand-written raw ADO reader, normalised to the
    /// schema's declared CLR types and to <see cref="GridSchema.FieldOrder"/> — an INDEPENDENT oracle
    /// <c>Craticula.Dapper</c>'s own <see cref="DapperGridQuery"/>/<see cref="DapperValueReader"/> code
    /// never runs through, so a bug shared between the oracle and the adapter cannot cancel out.
    ///
    /// <para>Assumes the compiled statement is an ordinary/leaf row query projecting
    /// <see cref="GridSchema.FieldOrder"/> under each field's LOGICAL alias — i.e. the SAME explicit
    /// projection <see cref="DapperGridQuery"/> forces for the identical reason (avoiding <c>SELECT
    /// *</c>'s physical column names). <see cref="NumberPlaceholders"/> rewrites SQLite's bare <c>?</c>
    /// exactly like <c>Craticula.Dapper.DapperParameterBinder</c> does, independently re-implemented
    /// here rather than reused, since the whole point of a second reader is that the two do not share
    /// code.</para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>> ExecuteCompiled(SqlQuery query, IReadOnlyList<string> columns)
    {
        using var command = connection.CreateCommand();
        command.CommandText = NumberPlaceholders(query.Sql, query.Parameters.Count);

        for (var index = 0; index < query.Parameters.Count; index++)
        {
            command.Parameters.Add(new SqliteParameter($"?{index + 1}", query.Parameters[index] ?? DBNull.Value));
        }

        using var reader = command.ExecuteReader();

        var rows = new List<IReadOnlyList<object?>>();
        while (reader.Read())
        {
            var cells = new object?[columns.Count];
            for (var index = 0; index < columns.Count; index++)
            {
                cells[index] = ReadDeclared(reader, reader.GetOrdinal(columns[index]), Schema.Fields[columns[index]].Type);
            }

            rows.Add(cells);
        }

        return rows;
    }

    public void Dispose() => connection.Dispose();

    // ---- schema -> DDL ---------------------------------------------------------------------------

    private static void CreateTable(SqliteConnection connection, GridSchema schema)
    {
        var ddl = new StringBuilder("CREATE TABLE \"").Append(schema.Source).Append("\" (");

        for (var index = 0; index < schema.FieldOrder.Count; index++)
        {
            if (index > 0)
            {
                ddl.Append(", ");
            }

            var field = schema.FieldOrder[index];
            ddl.Append('"').Append(PhysicalColumn(schema, field)).Append("\" ")
                .Append(Affinity(schema.Fields[field].Type));
        }

        // Every column nullable, including the stable key: SEM-2's null handling is a first-class part
        // of what these tests exercise.
        ddl.Append(')');

        using var command = connection.CreateCommand();
        command.CommandText = ddl.ToString();
        command.ExecuteNonQuery();
    }

    /// <summary>SQLite's type affinities for the seven declared <see cref="FieldType"/>s — TEXT for
    /// <c>date</c>/<c>datetime</c>/<c>decimal</c>, since SQLite has no native type for any of the
    /// three.</summary>
    private static string Affinity(FieldType type) => type switch
    {
        FieldType.Int64 or FieldType.Boolean => "INTEGER",
        FieldType.Double => "REAL",
        FieldType.String or FieldType.Date or FieldType.Datetime or FieldType.Decimal => "TEXT",
        _ => throw new InvalidOperationException($"Unmapped FieldType '{type}'.")
    };

    private static void Insert(
        SqliteConnection connection, GridSchema schema, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        foreach (var row in rows)
        {
            using var command = connection.CreateCommand();
            var columns = new StringBuilder();
            var values = new StringBuilder();

            for (var index = 0; index < schema.FieldOrder.Count; index++)
            {
                if (index > 0)
                {
                    columns.Append(", ");
                    values.Append(", ");
                }

                var field = schema.FieldOrder[index];
                columns.Append('"').Append(PhysicalColumn(schema, field)).Append('"');
                values.Append("$v").Append(index);

                row.TryGetValue(field, out var value);
                command.Parameters.AddWithValue($"$v{index}", value ?? DBNull.Value);
            }

            command.CommandText = $"INSERT INTO \"{schema.Source}\" ({columns}) VALUES ({values})";
            command.ExecuteNonQuery();
        }
    }

    internal static string PhysicalColumn(GridSchema schema, string field) =>
        schema.Fields[field].Physical?.Sql?.Column ?? field;

    // ---- reader -> declared CLR type ---------------------------------------------------------------

    private static object? ReadDeclared(SqliteDataReader reader, int ordinal, FieldType type)
    {
        if (reader.IsDBNull(ordinal))
        {
            return null;
        }

        return type switch
        {
            FieldType.String => reader.GetString(ordinal),
            FieldType.Int64 => reader.GetInt64(ordinal),
            FieldType.Decimal => reader.GetDecimal(ordinal),
            FieldType.Double => reader.GetDouble(ordinal),
            FieldType.Boolean => reader.GetBoolean(ordinal),
            FieldType.Date => DateOnly.ParseExact(reader.GetString(ordinal), "yyyy-MM-dd", CultureInfo.InvariantCulture),
            FieldType.Datetime => reader.GetDateTime(ordinal),
            _ => throw new InvalidOperationException($"Unmapped FieldType '{type}'.")
        };
    }

    private static string NumberPlaceholders(string sql, int expected)
    {
        var numbered = new StringBuilder(sql.Length + expected * 2);
        var ordinal = 0;

        foreach (var character in sql)
        {
            if (character == '?')
            {
                numbered.Append('?').Append(++ordinal);
            }
            else
            {
                numbered.Append(character);
            }
        }

        if (ordinal != expected)
        {
            throw new InvalidOperationException(
                $"Found {ordinal} '?' placeholders but the compiled query carries {expected} parameters.");
        }

        return numbered.ToString();
    }
}
