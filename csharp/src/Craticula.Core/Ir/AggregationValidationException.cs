namespace Craticula.Core.Ir;

/// <summary>
/// An aggregations array violates one of <c>spec/ir.schema.json</c>'s <c>$defs/aggregation</c>
/// structural invariants that System.Text.Json's deserialization cannot enforce on its own
/// (non-empty array, field length bound, the alias identifier pattern). Thrown by
/// <see cref="AggregationValidator.Validate"/>, collecting every violation found — mirrors
/// <see cref="SortItemValidationException"/>'s "one clean rejection, not a whack-a-mole" pattern.
/// </summary>
public sealed class AggregationValidationException(IReadOnlyList<string> errors)
    : Exception($"Aggregations array failed {errors.Count} structural check(s): {string.Join("; ", errors)}")
{
    public IReadOnlyList<string> Errors { get; } = errors;
}
