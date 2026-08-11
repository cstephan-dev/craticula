using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>SC-2: per-field declaration in the <see cref="GridSchema"/> allowlist.</summary>
public sealed record FieldConfig
{
    public required FieldType Type { get; init; }

    public bool Filterable { get; init; } = true;

    public bool Sortable { get; init; } = true;

    public bool Groupable { get; init; }

    /// <summary>Subset of AR-8's closed v1 aggregation functions (<see cref="AggregationFunc"/>,
    /// matching <c>ir.schema.json</c>'s <c>$defs/aggregationFunc</c>) this field may be aggregated
    /// with. Empty means not aggregatable. Uses the <c>Craticula.Core.Ir</c> enum directly — this
    /// namespace already depends on <c>Ir</c> pervasively (<see cref="GridFilterPipeline"/>,
    /// <see cref="TypeCoercionValidator"/>, and others all reference IR types), so a second,
    /// independently-driftable copy of this same closed 5-value set would only add a duplication
    /// risk with no layering benefit.</summary>
    public IReadOnlyList<AggregationFunc> Aggregatable { get; init; } = [];

    /// <summary>SC-4: per-backend physical mapping. A field need only declare mappings for the
    /// backends the fixture actually targets.</summary>
    public PhysicalMapping? Physical { get; init; }

    /// <summary>SC-3: only meaningful when <see cref="Type"/> is <see cref="FieldType.String"/>.</summary>
    public TextOptions? Text { get; init; }

    /// <summary>EXP-1: whether this field is included by default in a CSV/TSV export when the
    /// request's exportColumns is absent.</summary>
    public bool Exportable { get; init; } = true;

    /// <summary>EXP-1: the export header text for this field, falling back to the colId when absent.</summary>
    public string? Label { get; init; }
}
