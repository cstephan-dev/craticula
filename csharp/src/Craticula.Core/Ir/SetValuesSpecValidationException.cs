namespace Craticula.Core.Ir;

/// <summary>
/// A <see cref="SetValuesSpec"/> violates one of <c>spec/ir.schema.json</c>'s
/// <c>$defs/setValuesSpec</c> structural invariants that System.Text.Json's deserialization cannot
/// enforce on its own (a blank/overlong <c>colId</c>, a blank/overlong <c>searchPrefix</c>, a
/// non-positive <c>limit</c>). Thrown by <see cref="SetValuesSpecValidator.Validate"/>, collecting
/// every violation found — mirrors <see cref="GroupSpecValidationException"/>'s "one clean rejection,
/// not a whack-a-mole" pattern.
/// </summary>
public sealed class SetValuesSpecValidationException(IReadOnlyList<string> errors)
    : Exception($"SetValuesSpec failed {errors.Count} structural check(s): {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
