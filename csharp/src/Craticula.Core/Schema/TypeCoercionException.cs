namespace Craticula.Core.Schema;

/// <summary>
/// SEM-5: one <see cref="Ir.TypedValue"/> slot on some <see cref="Ir.ComparisonNode"/> that could
/// not be coerced into its field's declared <see cref="FieldType"/> — either its variant doesn't
/// already match and it isn't a <see cref="Ir.StringValue"/> to attempt parsing on (e.g. a
/// <see cref="Ir.BooleanValue"/> against a <see cref="FieldType.String"/> field), or it is a
/// <see cref="Ir.StringValue"/> whose content doesn't parse into the target type (e.g. <c>"12.34.56"</c>
/// against <see cref="FieldType.Decimal"/>). <see cref="Found"/> is a human-readable rendering of the
/// offending value (its type tag plus content), not the raw <see cref="Ir.TypedValue"/> itself.
/// <see cref="Reason"/> is a stable machine-readable code — currently always
/// <c>"uncoercible-value"</c> (the only failure mode this validator has) — matching
/// <c>spec/fixtures/adversarial/*/expected/rejection.json</c>'s own <c>reason</c> field, so a caller
/// (or a fixture-driven test) can bind an oracle's expected reason to something this type actually
/// carries, rather than to string content alone.
/// </summary>
public sealed record TypeCoercionViolation(string Field, FieldType DeclaredType, string Found, string Reason);

/// <summary>
/// SEM-5: thrown unconditionally by <see cref="TypeCoercionValidator.Coerce"/> — not gated by
/// <see cref="GridSchemaMode"/>, since SEM-5 states type coercion as an invariant ("never a value
/// passed through untyped or best-effort", "never a silent full-table match"), not a Strict-only
/// rule. Collects every violation found across the whole tree, not just the first — mirroring
/// <see cref="ResourceLimitExceededException"/>'s and <see cref="UnknownFieldException"/>'s "one
/// clean rejection, not a whack-a-mole" precedent.
/// </summary>
public sealed class TypeCoercionException : Exception
{
    public IReadOnlyList<TypeCoercionViolation> Violations { get; }

    public TypeCoercionException(IReadOnlyList<TypeCoercionViolation> violations)
        : base(BuildMessage(violations))
    {
        Violations = violations;
    }

    private static string BuildMessage(IReadOnlyList<TypeCoercionViolation> violations) =>
        $"SEM-5 type coercion failed for {violations.Count} value(s): " +
        string.Join("; ", violations.Select(v =>
            $"field '{v.Field}': {v.Found} cannot be coerced into the declared type '{TypeCoercionValidator.JsonTag(v.DeclaredType)}'"));
}
