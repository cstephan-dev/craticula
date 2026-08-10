namespace Craticula.Core.Schema;

/// <summary>
/// A <see cref="GridSchema"/> document violates one of <c>spec/grid-schema.schema.json</c>'s
/// invariants that JSON Schema expresses but C#'s type system and System.Text.Json cannot enforce
/// on their own (non-empty collections, a field referencing another field by name, "exactly one of
/// N" shapes). Thrown by <see cref="GridSchema.Parse"/> — distinct from
/// <see cref="UnknownFieldException"/>, which reports an allowlist violation in a *request*, not a
/// self-consistency problem in the schema document itself.
/// </summary>
public sealed class GridSchemaValidationException(string message) : Exception(message);
