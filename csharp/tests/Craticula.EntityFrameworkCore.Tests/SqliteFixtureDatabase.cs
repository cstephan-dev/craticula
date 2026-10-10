using System.Globalization;
using System.Text;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Sql;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Craticula.EntityFrameworkCore.Tests;

/// <summary>
/// One SQLite in-memory database, created from a <see cref="GridSchema"/> and seeded with rows written
/// in the test that needs them — and both ways of reading it back: the EF path's
/// <see cref="IQueryable{T}"/> and a raw ADO command running a compiled <see cref="SqlQuery"/>.
///
/// <para><b>One connection, one table, one set of stored bytes.</b> That is what makes a row difference
/// between the two paths attributable: they are not two databases that happen to hold similar data, they
/// are two statements against the same rows. SQLite's in-memory database lives as long as the connection
/// does, so the connection is opened once here and handed to EF rather than described to it by a
/// connection string.</para>
///
/// <para><b>These are NOT <c>dataset.json</c> fixtures, deliberately.</b> The execution-equality tier (#1655, <c>Craticula.Integration.Tests</c>) owns the <c>dataset.json</c> / <c>expectedRows.json</c> format; the datasets in this suite are inline in the test that reasons about them, where the reason each row exists can be read next to it.</para>
/// </summary>
internal sealed class SqliteFixtureDatabase : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly PropertyBagContext context;
    private readonly List<string> executedCommands = [];

    public GridSchema Schema { get; }

    /// <summary>
    /// Every statement EF actually executed against this connection, captured from its own
    /// <c>CommandExecuted</c> event.
    ///
    /// <para><b>Why capture rather than inspect an <see cref="IQueryable{T}"/>.</b> The assertion that
    /// matters most in this suite — an export never emits an <c>OFFSET</c> (EXP-3) — is about what the
    /// export path SENDS, and that path is a closed async iterator: it builds its own chunk queries
    /// internally, one per chunk, and hands back only rows. Reconstructing "the query it probably built"
    /// in a test and asserting on that would assert the reconstruction. Reading EF's log asserts the
    /// statements.</para>
    /// </summary>
    public IReadOnlyList<string> ExecutedCommands => executedCommands;

    /// <summary>How many rows were seeded — the denominator of the "this case is not a tautology" checks
    /// (a filter that returns all of them, or none, proves nothing).</summary>
    public int SeededRowCount { get; }

    private SqliteFixtureDatabase(
        SqliteConnection connection, GridSchema schema, int seededRowCount)
    {
        this.connection = connection;
        Schema = schema;
        SeededRowCount = seededRowCount;
        context = new PropertyBagContext(connection, schema, executedCommands.Add);
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

    /// <summary>The EF path's <c>FROM</c> target: the property-bag entity the adapter's own
    /// <see cref="GridSchemaPropertyBag.Configure"/> registered.</summary>
    public IQueryable<Dictionary<string, object?>> Rows() => GridSchemaPropertyBag.Query(context, Schema);

    /// <summary>
    /// Runs a compiled <see cref="SqlQuery"/> and returns its rows, normalised to the schema's declared
    /// CLR types and to <see cref="GridSchema.FieldOrder"/>.
    ///
    /// <para><b>Why the SQL side is normalised and the EF side is not.</b> A raw
    /// <see cref="SqliteDataReader"/> knows only SQLite's five storage classes: a <c>boolean</c> comes
    /// back as <see cref="long"/> 1/0, a <c>date</c> as a <see cref="string"/>. It has no schema, so it
    /// cannot do otherwise — and a Dapper-style adapter would have to perform exactly this mapping
    /// itself. Reading each column through the declared <see cref="FieldType"/> is therefore the fair
    /// comparison, and it leaves #1653's native-CLR-values obligation asserted where it actually lies:
    /// on the EF side, which is expected to produce those types with no help.</para>
    ///
    /// <para><b>The one adaptation to the compiled text, stated plainly:</b> each <c>?</c> placeholder is
    /// rewritten to SQLite's numbered form <c>?1</c>, <c>?2</c>, … in first-appearance order, because
    /// <c>Microsoft.Data.Sqlite</c> requires every parameter to be named and rejects the bare form
    /// (<i>"ParameterName must be set"</i>). It is the same placeholder family binding the same values in
    /// the same order — the statement's meaning is untouched — and it is positional, so it cannot
    /// reorder the parameter list. Nothing else about the compiled statement is edited; in particular the
    /// clauses, the collations and the paging arithmetic are executed exactly as
    /// <see cref="SqlQueryBuilder"/> emitted them.</para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>> ExecuteCompiled(SqlQuery query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = NumberPlaceholders(query.Sql, query.Parameters.Count);

        for (var index = 0; index < query.Parameters.Count; index++)
        {
            command.Parameters.Add(new SqliteParameter($"?{index + 1}", query.Parameters[index] ?? DBNull.Value));
        }

        using var reader = command.ExecuteReader();

        // The compiled statement is `SELECT *`, so the columns arrive under their PHYSICAL names and in
        // the table's creation order. Mapping them back to FieldOrder is what a host reading this reader
        // would do, and it is what makes the two paths' rows comparable at all.
        var ordinals = new int[Schema.FieldOrder.Count];
        for (var index = 0; index < Schema.FieldOrder.Count; index++)
        {
            ordinals[index] = reader.GetOrdinal(PhysicalColumn(Schema, Schema.FieldOrder[index]));
        }

        var rows = new List<IReadOnlyList<object?>>();
        while (reader.Read())
        {
            var cells = new object?[Schema.FieldOrder.Count];
            for (var index = 0; index < cells.Length; index++)
            {
                cells[index] = ReadDeclared(reader, ordinals[index], Schema.Fields[Schema.FieldOrder[index]].Type);
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>
    /// #1888: reads a compiled GROUPED-LEVEL query back by its own output order — GRP-3's
    /// <c>[groupColumn, ...aggregations]</c> — rather than by <see cref="GridSchema.FieldOrder"/>'s
    /// physical mapping, which <see cref="ExecuteCompiled"/> uses and which does not describe a
    /// grouped level's <c>SELECT</c> list at all: each output row is a bucket, not a physical row, and
    /// the group column keeps its OWN logical name as its alias (<c>"region" AS "region"</c>) while
    /// each aggregation is aliased to its <see cref="Aggregation.OutputName"/> — both read back here
    /// by that alias via <see cref="SqliteDataReader.GetOrdinal(string)"/>, exactly the column names
    /// <see cref="EfCoreGridQuery.FetchPageAsync{TRow}"/>'s own <c>page.Columns</c> carries.
    ///
    /// <para>The group column is read as the SCHEMA's own declared type (its value is one of the
    /// schema's own field values, just bucketed). Each aggregation is read as its SOURCE field's
    /// declared type — <c>sum</c>/<c>avg</c>/<c>min</c>/<c>max</c> all preserve it — <b>except
    /// <c>count</c>, whose SQL result is always an integer regardless of what it counts</b> (AGG-1):
    /// <c>group-aggregation/011</c> counts a <c>string</c> field, so reading it through that field's
    /// own type would call <see cref="SqliteDataReader.GetString(int)"/> on an INTEGER column. Read as
    /// <see cref="int"/> rather than <see cref="long"/> deliberately, to match the boxed CLR type EF's
    /// own <c>int?</c> wrap (<see cref="EfCoreQueryBuilder"/>'s <c>BuildAggregateExpression</c>)
    /// produces — a width difference here would be an artifact of this harness's own two reading
    /// paths, not a fact about either compiler, and the parity assertion must not trip on it.</para>
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>> ExecuteCompiledGroupedLevel(
        SqlQuery query, string groupColumn, IReadOnlyList<Aggregation> aggregations)
    {
        using var command = connection.CreateCommand();
        command.CommandText = NumberPlaceholders(query.Sql, query.Parameters.Count);

        for (var index = 0; index < query.Parameters.Count; index++)
        {
            command.Parameters.Add(new SqliteParameter($"?{index + 1}", query.Parameters[index] ?? DBNull.Value));
        }

        using var reader = command.ExecuteReader();

        var groupOrdinal = reader.GetOrdinal(groupColumn);
        var groupType = Schema.Fields[groupColumn].Type;

        var aggregateOrdinals = new int[aggregations.Count];
        for (var i = 0; i < aggregations.Count; i++)
        {
            aggregateOrdinals[i] = reader.GetOrdinal(aggregations[i].OutputName);
        }

        var rows = new List<IReadOnlyList<object?>>();
        while (reader.Read())
        {
            var cells = new object?[1 + aggregations.Count];
            cells[0] = ReadDeclared(reader, groupOrdinal, groupType);

            for (var i = 0; i < aggregations.Count; i++)
            {
                cells[i + 1] = aggregations[i].Func == AggregationFunc.Count
                    ? (reader.IsDBNull(aggregateOrdinals[i]) ? null : Convert.ToInt32(reader.GetInt64(aggregateOrdinals[i])))
                    : ReadDeclared(reader, aggregateOrdinals[i], Schema.Fields[aggregations[i].Field].Type);
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>
    /// #1888: reads a compiled DRILL-DOWN LEAF query (<c>group-aggregation/005</c>/<c>006</c>) back by
    /// <see cref="GridSchema.FieldOrder"/>'s LOGICAL names, rather than <see cref="ExecuteCompiled"/>'s
    /// physical-column mapping. A leaf's <c>SELECT</c> list is GRP-3's full-field projection, rendered
    /// as an explicit column list under each field's LOGICAL alias (<c>"fiscal_year" AS "fiscalYear"</c>
    /// wherever <c>physical.sql.column</c> differs from the field name, a bare <c>"id"</c> where it does
    /// not) rather than the bare <c>SELECT *</c> the rest of this harness's fixtures compile to — so the
    /// reader's own column names ARE the logical ones already, and re-deriving the physical name to look
    /// them up (as <see cref="ExecuteCompiled"/> does) throws <c>ArgumentOutOfRangeException</c> the
    /// moment a field's physical column differs from its logical name, which is exactly this category's
    /// <c>fiscalYear</c>/<c>fiscal_year</c> pair.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<object?>> ExecuteCompiledLeafProjection(SqlQuery query)
    {
        using var command = connection.CreateCommand();
        command.CommandText = NumberPlaceholders(query.Sql, query.Parameters.Count);

        for (var index = 0; index < query.Parameters.Count; index++)
        {
            command.Parameters.Add(new SqliteParameter($"?{index + 1}", query.Parameters[index] ?? DBNull.Value));
        }

        using var reader = command.ExecuteReader();

        var ordinals = new int[Schema.FieldOrder.Count];
        for (var index = 0; index < Schema.FieldOrder.Count; index++)
        {
            ordinals[index] = reader.GetOrdinal(Schema.FieldOrder[index]);
        }

        var rows = new List<IReadOnlyList<object?>>();
        while (reader.Read())
        {
            var cells = new object?[Schema.FieldOrder.Count];
            for (var index = 0; index < cells.Length; index++)
            {
                cells[index] = ReadDeclared(reader, ordinals[index], Schema.Fields[Schema.FieldOrder[index]].Type);
            }

            rows.Add(cells);
        }

        return rows;
    }

    /// <summary>One scalar count against the seeded table, for the divergence tests: they need to show
    /// that two encodings of the same instant compare differently against the STORED text, which is a fact
    /// about the database rather than about either compiler.</summary>
    public long ScalarCount(string sql, string parameter)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.Add(new SqliteParameter("?1", parameter));
        return Convert.ToInt64(command.ExecuteScalar());
    }

    /// <summary>The generated SQL EF would send for <paramref name="query"/> — used by the tests that
    /// assert what this adapter emits (an <c>OFFSET</c> that must not be there, a collation that must
    /// be). <c>ToQueryString</c> is EF's debug rendering and is never compared byte-for-byte here for the
    /// reason #1644 learned the hard way about moving targets; the assertions are on the presence or
    /// absence of one token.</summary>
    public static string GeneratedSql<T>(IQueryable<T> query) => query.ToQueryString();

    public void Dispose()
    {
        context.Dispose();
        connection.Dispose();
    }

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
        // of what these tests exercise, and a NOT NULL constraint would make a dataset that forgot a
        // null fail at insert instead of at the assertion that cares.
        ddl.Append(')');

        using var command = connection.CreateCommand();
        command.CommandText = ddl.ToString();
        command.ExecuteNonQuery();
    }

    /// <summary>
    /// SQLite's type affinities for the seven declared <see cref="FieldType"/>s, matching what EF Core's
    /// own SQLite provider maps them to — TEXT for <c>date</c>/<c>datetime</c>/<c>decimal</c>, because
    /// SQLite has no native type for any of the three. That absence is not a detail: it is the source of
    /// both divergences this suite documents (see <c>SqliteTypeAffinityDivergenceTests</c>).
    /// </summary>
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
                $"Found {ordinal} '?' placeholders but the compiled query carries {expected} parameters. Either the " +
                "SQLite dialect stopped using positional placeholders, or a '?' now appears inside a string literal " +
                "and this positional rewrite is no longer safe.");
        }

        return numbered.ToString();
    }

    /// <summary>
    /// The <c>DbContext</c> for one schema, configured entirely by the adapter's own
    /// <see cref="GridSchemaPropertyBag.Configure"/> — so what these tests exercise is the model the
    /// shipped code builds, not a hand-written one that happens to agree with it.
    ///
    /// <para>Including <see cref="GridSchemaModelCacheKeyFactory"/>, without which this suite cannot work
    /// at all: one context type serves 51 fixture schemas here, and EF's per-context-TYPE model cache
    /// would hand every case after the first the first case's model. It is registered here rather than
    /// worked around with 51 context classes precisely because a host serving two grids has the identical
    /// problem — see that class for the two errors it produces.</para>
    /// </summary>
    private sealed class PropertyBagContext(
        SqliteConnection connection, GridSchema schema, Action<string> commandSink) : DbContext, IGridSchemaModel
    {
        public GridSchema Schema { get; } = schema;

        protected override void OnConfiguring(DbContextOptionsBuilder options) =>
            options
                .UseSqlite(connection)
                .ReplaceService<IModelCacheKeyFactory, GridSchemaModelCacheKeyFactory>()
                .LogTo(commandSink, [RelationalEventId.CommandExecuted]);

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            GridSchemaPropertyBag.Configure(modelBuilder, Schema);
    }
}
