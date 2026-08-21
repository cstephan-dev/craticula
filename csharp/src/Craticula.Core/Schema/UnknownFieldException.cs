namespace Craticula.Core.Schema;

/// <summary>
/// SC-1/SC-2: one colId reference that failed <see cref="FieldAllowlistValidator"/> — either it is
/// absent from <see cref="GridSchema.Fields"/> entirely (SC-1), or it IS present but the field's
/// declared capability flag forbids the use it was put to (SC-2). <see cref="DisplayText"/> is the
/// existing human-readable rendering (bare colId for a genuinely unknown one; colId plus a short
/// clause for everything else) — unchanged by #1872 so every prior string-based assertion keeps
/// matching. <see cref="Reason"/> is the stable machine-readable code, matching
/// <c>spec/fixtures/adversarial/*/expected/rejection.json</c>'s own <c>reason</c> field, mirroring
/// <see cref="TypeCoercionViolation.Reason"/>'s precedent for why one is needed at all: a caller (or
/// a fixture-driven test) can then bind an oracle's expected reason to something this type actually
/// carries, rather than to parsing <see cref="DisplayText"/>.
///
/// <para><b>Only two reasons are distinguished today</b> — <c>"unknown-field"</c> (SC-1: the colId
/// itself is not allowlisted) and <c>"field-not-filterable"</c> (SC-2, #1872: allowlisted, but
/// <see cref="FieldConfig.Filterable"/> is <see langword="false"/>) — because those are the only two
/// values <c>rejection.schema.json</c>'s closed <c>reason</c> enum defines for the <c>allowlist</c>
/// stage. <see cref="FieldAllowlistValidator.ValidateStructuralColumns"/>'s pre-#1872
/// <see cref="FieldConfig.Groupable"/>/<see cref="FieldConfig.Aggregatable"/> capability failures are
/// tagged <c>"unknown-field"</c> too, the same bucket they have always been in — no fixture has ever
/// distinguished them, and giving them their own codes is a separate, unscoped decision for whichever
/// story adds that fixture coverage.</para>
/// </summary>
public sealed record AllowlistViolation(string ColId, string DisplayText, string Reason);

/// <summary>
/// SC-1/SC-2: thrown in <see cref="GridSchemaMode.Strict"/> mode (unconditionally from
/// <see cref="FieldAllowlistValidator.ValidateStructuralColumns"/>, since no mode may drop those
/// colIds) when one or more referenced colIds fail the field allowlist check — either absent from the
/// <see cref="GridSchema"/>'s field allowlist entirely, or present but forbidden by an SC-2 capability
/// flag for the use requested. See <see cref="AllowlistViolation"/> for how the two are told apart.
/// </summary>
public sealed class UnknownFieldException : Exception
{
    public IReadOnlyList<string> ColIds { get; }

    public IReadOnlyList<AllowlistViolation> Violations { get; }

    public UnknownFieldException(IReadOnlyList<AllowlistViolation> violations)
        : base(BuildMessage(violations))
    {
        Violations = violations;
        ColIds = violations.Select(v => v.DisplayText).ToList();
    }

    private static string BuildMessage(IReadOnlyList<AllowlistViolation> violations) =>
        $"Field allowlist check failed (SC-1/SC-2) for {violations.Count} colId(s): " +
        string.Join(", ", violations.Select(v => v.DisplayText));
}
