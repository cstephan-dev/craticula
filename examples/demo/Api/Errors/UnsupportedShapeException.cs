namespace Demo.Api.Errors;

/// <summary>
/// A request shape one provider cannot answer the way PostgreSQL does (today: some grouped requests on OpenSearch). The API
/// answers it with HTTP 400, kind <c>unsupported-shape</c>; the message names the shape and what to use instead. It is never a
/// catch-all: a provider throws it only for a shape the library refuses or that measurably differs for a documented reason.
/// </summary>
public sealed class UnsupportedShapeException(string message, Exception? inner = null) : Exception(message, inner);
