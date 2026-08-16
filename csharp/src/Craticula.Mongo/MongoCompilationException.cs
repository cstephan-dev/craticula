namespace Craticula.Mongo;

/// <summary>
/// A request the MongoDB compiler cannot compile: an IR/schema shape that is structurally valid (it
/// already passed <c>FilterNodeValidator</c>/<c>GridSchema.Parse</c>) but that this compiler has no
/// defined rendering for. The exact counterpart of <c>Craticula.Sql.SqlCompilationException</c>, and
/// distinct from the upstream validation exceptions (<c>FilterNodeValidationException</c>,
/// <c>TypeCoercionException</c>, ...) which reject a malformed or disallowed <em>request</em>: this
/// one always signals a gap in the compiler's own coverage, so a host mapping it to an HTTP status
/// should treat it as a 500-class condition rather than a client error.
/// </summary>
public sealed class MongoCompilationException : Exception
{
    public MongoCompilationException(string message) : base(message)
    {
    }

    public MongoCompilationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
