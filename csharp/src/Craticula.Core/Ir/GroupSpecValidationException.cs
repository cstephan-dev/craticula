namespace Craticula.Core.Ir;

/// <summary>
/// A <see cref="GroupSpec"/> violates one of <c>spec/ir.schema.json</c>'s <c>$defs/groupSpec</c>
/// structural invariants that System.Text.Json's deserialization cannot enforce on its own
/// (non-empty <c>groupCols</c>, field length bound, no null elements). Thrown by
/// <see cref="GroupSpecValidator.Validate"/>, collecting every violation found — mirrors
/// <see cref="SortItemValidationException"/>'s "one clean rejection, not a whack-a-mole" pattern.
/// </summary>
public sealed class GroupSpecValidationException(IReadOnlyList<string> errors)
    : Exception($"GroupSpec failed {errors.Count} structural check(s): {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
