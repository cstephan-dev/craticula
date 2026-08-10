using System.Text.Json.Serialization;

namespace Craticula.Core.Ir;

/// <summary>
/// AG-3/EXP-2: paging mode, matching <c>spec/ir.schema.json</c>'s <c>$defs/pageSpec</c> — either a
/// bounded row range or the unpaginated "none" mode required for full-export compilation.
/// Deserialized via System.Text.Json polymorphic serialization, discriminated on <c>mode</c>.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "mode")]
[JsonDerivedType(typeof(RangePage), "range")]
[JsonDerivedType(typeof(NonePage), "none")]
public abstract record PageSpec;

/// <summary>
/// AG-3: a bounded row range, mirroring AG Grid SSRM's startRow/endRow. <see cref="EndRow"/> is
/// exclusive, matching the SSRM convention; (<see cref="EndRow"/> - <see cref="StartRow"/>) is the
/// requested page size <see cref="Craticula.Core.Schema.ResourceLimitsValidator"/> checks against
/// SEC-3's max page size.
/// </summary>
public sealed record RangePage : PageSpec
{
    public required int StartRow { get; init; }

    public required int EndRow { get; init; }
}

/// <summary>
/// EXP-2: unpaginated mode — compile the full filtered+sorted artifact with no row-range clause.
/// Exempt from <see cref="Craticula.Core.Schema.ResourceLimitsValidator"/>'s max-page-size check.
/// </summary>
public sealed record NonePage : PageSpec;
