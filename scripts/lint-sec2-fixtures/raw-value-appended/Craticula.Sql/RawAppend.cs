using System.Text;

namespace Fixture.Sql;

public static class RawAppend
{
    // A StringBuilder is query text too: an unquoted sort column from the request is appended as is.
    public static string OrderBy(string sortColumn)
    {
        var sql = new StringBuilder("SELECT * FROM \"orders\" ORDER BY ");
        sql.Append(sortColumn);
        return sql.ToString();
    }
}
