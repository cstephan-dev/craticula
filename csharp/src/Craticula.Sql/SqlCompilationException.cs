namespace Craticula.Sql;

/// <summary>
/// A request the SQL compiler cannot compile: an IR/schema shape that is structurally valid (it
/// already passed <c>FilterNodeValidator</c>/<c>GridSchema.Parse</c>) but that this compiler has no
/// defined rendering for. Distinct from the upstream validation exceptions
/// (<c>FilterNodeValidationException</c>, <c>TypeCoercionException</c>, ...), which reject a
/// malformed or disallowed <em>request</em>; this one always signals a gap in the compiler's own
/// coverage, so a host mapping it to an HTTP status should treat it as a 500-class condition rather
/// than a client error.
///
/// <para>The two <c>SqlMapping</c> shapes deliberately not implemented in #1638
/// (<c>qualifiedColumn</c> and <c>expression</c>) are reported through this type: 752/752 field
/// declarations in <c>spec/fixtures/</c> use the plain <c>column</c> form, so there is no oracle for
/// the other two and implementing them would be designing blind. A clean, typed rejection is what
/// keeps that gap visible instead of guessing at a rendering nobody has verified.</para>
/// </summary>
public sealed class SqlCompilationException : Exception
{
    public SqlCompilationException(string message) : base(message)
    {
    }

    public SqlCompilationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
