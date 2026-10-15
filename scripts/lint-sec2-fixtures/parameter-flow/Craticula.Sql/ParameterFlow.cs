namespace Fixture.Sql;

public interface IDialect
{
    string QuoteIdentifier(string identifier);
}

public interface IParameterSink
{
    string Bind(object? value);
}

public sealed record Leaf(string Field, object? Value);

public static class ParameterFlow
{
    // Safe or not depending on its callers: the lint checks what every call site passes.
    private static string Equal(string quotedColumn, string placeholder) => $"{quotedColumn} = {placeholder}";

    public static string Quoted(Leaf leaf, IDialect dialect, IParameterSink parameters) =>
        Equal(dialect.QuoteIdentifier(leaf.Field), parameters.Bind(leaf.Value));

    public static string Unquoted(Leaf leaf, IParameterSink parameters) =>
        Equal(leaf.Field, parameters.Bind(leaf.Value));
}
