using System.Data.Common;
using Craticula.Sql;
using Microsoft.Data.SqlClient;

namespace Demo.Api.Grid;

/// <summary>
/// How a compiled mssql statement reaches Microsoft.Data.SqlClient, for the set-values call. The compiler writes named
/// placeholders <c>@p1..@pn</c>, so the text is sent as it is and the parameters are bound under those names in order: what
/// <c>Craticula.Dapper</c> does for its own queries (its binder is internal, so this is the minimum of it).
/// A decimal, date or datetime value arrives from the compiler as a string and is bound as a string, like the adapter does:
/// SQL Server converts it to the column's type on comparison.
/// </summary>
public static class SqlServerBinding
{
    /// <summary>A command for the compiled query. The mssql dialect flattens set membership into scalars, so a list parameter is not
    /// expected; null becomes SQL NULL.</summary>
    public static DbCommand CreateCommand(DbConnection connection, SqlQuery query)
    {
        var command = new SqlCommand(query.Sql, (SqlConnection)connection);
        for (var index = 0; index < query.Parameters.Count; index++)
        {
            var value = query.Parameters[index];
            if (value is IReadOnlyList<object?>)
            {
                throw new NotSupportedException("The mssql dialect flattens set membership into scalars; a list parameter is not expected.");
            }

            command.Parameters.Add(new SqlParameter($"@p{index + 1}", value ?? DBNull.Value));
        }

        return command;
    }
}
