namespace Craticula.Core.Ir;

/// <summary>
/// A sort array violates one of <c>spec/ir.schema.json</c>'s <c>$defs/sortItem</c> structural
/// invariants that System.Text.Json's polymorphic deserialization cannot enforce on its own
/// (non-empty <c>sort</c> array, field length bound). Thrown by
/// <see cref="SortItemValidator.Validate"/>, collecting every violation found — mirrors
/// <see cref="FilterNodeValidationException"/>'s "one clean rejection, not a whack-a-mole" pattern.
/// </summary>
public sealed class SortItemValidationException(IReadOnlyList<string> errors)
    : Exception($"Sort array failed {errors.Count} structural check(s): {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
