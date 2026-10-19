using System.Data.Common;
using Craticula.Sql;
using MySqlConnector;

namespace Demo.Api.Grid;

/// <summary>
/// How a compiled mysql statement reaches MySqlConnector, for the set-values call. The compiler writes a bare <c>?</c> for every
/// parameter, a genuinely positional marker the driver binds by ORDER whatever the parameter is called, so the text is sent as it
/// is and the parameters are added under the order-preserving names <c>p0..pn</c>: what <c>Craticula.Dapper</c> does for its own
/// queries (its binder is internal, so this is the minimum of it).
/// A decimal, date or datetime value arrives from the compiler as a string and is bound as a string, like the adapter does:
/// MySQL converts it to the column's type on comparison.
/// </summary>
public static class MySqlBinding
{
    /// <summary>A command for the compiled query. The mysql dialect flattens set membership into scalars, so a list parameter is not
    /// expected; null becomes SQL NULL.</summary>
    public static DbCommand CreateCommand(DbConnection connection, SqlQuery query)
    {
        var command = new MySqlCommand(query.Sql, (MySqlConnection)connection);
        for (var index = 0; index < query.Parameters.Count; index++)
        {
            var value = query.Parameters[index];
            if (value is IReadOnlyList<object?>)
            {
                throw new NotSupportedException("The mysql dialect flattens set membership into scalars; a list parameter is not expected.");
            }

            command.Parameters.Add(new MySqlParameter($"p{index}", value ?? DBNull.Value));
        }

        return command;
    }
}
