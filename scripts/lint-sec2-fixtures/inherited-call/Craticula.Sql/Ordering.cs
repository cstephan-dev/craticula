namespace Fixture.Sql;

public interface IDialect
{
    string QuoteIdentifier(string identifier);
}

public class OrderingBase
{
    protected static string Order(string column) => $"ORDER BY {column}";

    public static string Safe(IDialect dialect) => Order(dialect.QuoteIdentifier("amount"));
}

public sealed class UserOrdering : OrderingBase
{
    // An overload of its own does not hide the inherited Order from the lint.
    private static string Order(string column, bool descending) => descending ? "DESC" : "ASC";

    public static int Unsafe(string userColumn) => Order(userColumn).Length;
}

public sealed class FrameworkOrdering : SomeFrameworkBase
{
    // Order resolves to nothing the lint can place (the base is not scanned): a call site of every Order in scope.
    public static int Unsafe(string userColumn) => Order(userColumn).Length;
}
