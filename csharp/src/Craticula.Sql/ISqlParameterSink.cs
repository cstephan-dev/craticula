namespace Craticula.Sql;

/// <summary>
/// The narrow contract a dialect uses to bind a value: append it to the query's ordered parameter
/// list and get back the placeholder text to splice into the SQL. Binding and placeholder rendering
/// are one operation on purpose — a dialect that formatted a placeholder without appending (or vice
/// versa) would silently desynchronize <c>sql</c> from <c>params</c>, which is exactly the class of
/// bug SEC-2 (every user value is a parameter, never inlined) depends on not happening.
/// </summary>
public interface ISqlParameterSink
{
    /// <summary>Appends <paramref name="value"/> to the parameter list and returns the placeholder
    /// text for it (<c>$1</c>, <c>@p1</c>, <c>?</c>, ... per dialect).</summary>
    string Bind(object? value);
}

/// <summary>
/// The concrete <see cref="ISqlParameterSink"/> the builder threads through one compilation.
/// Placeholder text comes from the dialect (<see cref="ISqlDialect.ParameterPlaceholder"/>), so the
/// ordinal bookkeeping lives here once instead of in every dialect.
/// </summary>
internal sealed class SqlParameterBag(ISqlDialect dialect) : ISqlParameterSink
{
    private readonly List<object?> _values = [];

    public IReadOnlyList<object?> Values => _values;

    public string Bind(object? value)
    {
        _values.Add(value);
        return dialect.ParameterPlaceholder(_values.Count - 1);
    }
}
