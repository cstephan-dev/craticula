namespace Craticula.Core.Ir;

/// <summary>
/// A filter tree violates one of <c>spec/ir.schema.json</c>'s structural invariants that System.
/// Text.Json's polymorphic deserialization cannot enforce on its own (a comparison node's
/// per-operator "exactly these slots, not those", non-empty arrays, no-null elements, string
/// length/pattern bounds). Thrown by <see cref="FilterNodeValidator.Validate"/> — distinct from
/// <see cref="Craticula.Core.Schema.ResourceLimitExceededException"/>, which reports a
/// well-formed-but-too-large tree, not a malformed one. Collects every violation found, mirroring
/// <see cref="Craticula.Core.Schema.FieldAllowlistValidator"/>'s "one clean rejection, not a
/// whack-a-mole" precedent.
/// </summary>
public sealed class FilterNodeValidationException(IReadOnlyList<string> errors)
    : Exception($"Filter tree failed {errors.Count} structural check(s): {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
