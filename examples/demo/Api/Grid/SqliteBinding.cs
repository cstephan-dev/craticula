using System.Data.Common;
using System.Text;
using Craticula.Sql;
using Microsoft.Data.Sqlite;

namespace Demo.Api.Grid;

/// <summary>
/// How a compiled sqlite statement reaches Microsoft.Data.Sqlite, for the set-values call. The compiler writes a bare <c>?</c>
/// and the driver rejects it, so each marker becomes <c>?1</c>, <c>?2</c>, ... and the parameters are bound under those names:
/// the same rewrite <c>Craticula.Dapper</c> does for its own queries (its binder is internal, so this is the minimum of it).
/// Counting the markers against the parameter list is what makes the text scan safe: every user value is a parameter (SEC-2),
/// so a stray <c>?</c> cannot sit inside a literal.
/// </summary>
public static class SqliteBinding
{
    /// <summary>The compiled text with <c>?</c> numbered in order. Throws when the marker count differs from <paramref name="expected"/>.</summary>
    public static string NumberPlaceholders(string sql, int expected)
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

        return ordinal == expected
            ? numbered.ToString()
            : throw new InvalidOperationException(
                $"Found {ordinal} '?' placeholders in the compiled SQLite statement but the query carries {expected} parameters.");
    }

    /// <summary>A command for the compiled query: numbered text, parameters named <c>?1..?n</c>. A set-membership list is not
    /// expected (the sqlite dialect flattens it into scalars); null becomes SQL NULL.</summary>
    public static DbCommand CreateCommand(DbConnection connection, SqlQuery query)
    {
        var command = new SqliteCommand(NumberPlaceholders(query.Sql, query.Parameters.Count), (SqliteConnection)connection);
        for (var index = 0; index < query.Parameters.Count; index++)
        {
            var value = query.Parameters[index];
            if (value is IReadOnlyList<object?>)
            {
                throw new NotSupportedException("The sqlite dialect flattens set membership into scalars; a list parameter is not expected.");
            }

            command.Parameters.Add(new SqliteParameter($"?{index + 1}", value ?? DBNull.Value));
        }

        return command;
    }
}
