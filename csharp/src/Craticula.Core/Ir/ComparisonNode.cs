namespace Craticula.Core.Ir;

/// <summary>
/// AR-6's closed 17-operator set, spanning all 4 <c>comparisonNode</c> variants (unary/binary/set/
/// range). Members are PascalCase so <see cref="Craticula.Core.Schema.GridSchemaJson.Options"/>'s
/// CamelCase <c>JsonStringEnumConverter</c> round-trips them against <c>ir.schema.json</c>'s
/// lowercase enum values (e.g. <see cref="True"/> -&gt; "true", <see cref="StartsWith"/> -&gt;
/// "startsWith") — "true"/"false" can't be C# identifiers as-is, hence the capitalized members.
/// </summary>
public enum ComparisonOperator
{
    // Unary (no value): blank/notBlank null-checks (SEM-2), true/false boolean shorthand (AR-6).
    Blank,
    NotBlank,
    True,
    False,

    // Binary (single value).
    Eq,
    Neq,
    Gt,
    Gte,
    Lt,
    Lte,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,

    // Set (value array).
    In,
    NotIn,

    // Range (from/to pair with inclusivity flags).
    InRange
}

/// <summary>
/// AR-5/AR-6: a leaf comparison against a single field, matching <c>ir.schema.json</c>'s
/// <c>comparisonNode</c>. The schema splits this into 4 <c>oneOf</c> sub-variants (unary/binary/
/// set/range) sharing <c>type:"comparison"</c> but differing by <c>op</c> and value shape — modeled
/// here as ONE concrete type with nullable slots per variant shape (<see cref="Value"/> for binary,
/// <see cref="Values"/> for set, <see cref="From"/>/<see cref="To"/>/inclusivity flags for range,
/// none of the above for unary) rather than 4 separate <c>JsonDerivedType</c> variants, since System
/// .Text.Json's polymorphic discriminator can only map ONE concrete type per discriminator value
/// ("comparison") — mirrors the same pragmatic "one record, nullable slots" choice already used for
/// <c>PhysicalMapping.SqlMapping</c>'s <c>oneOf</c>.
/// </summary>
public sealed record ComparisonNode : FilterNode
{
    public required string Field { get; init; }

    public required ComparisonOperator Op { get; init; }

    /// <summary>Binary comparisons' single value (eq, gt, contains, ...).</summary>
    public TypedValue? Value { get; init; }

    /// <summary>Set comparisons' value array (in, notIn) — SEC-3's max-in-values limit applies here.</summary>
    public IReadOnlyList<TypedValue>? Values { get; init; }

    /// <summary>Range comparison's (inRange) lower bound.</summary>
    public TypedValue? From { get; init; }

    /// <summary>Range comparison's (inRange) upper bound.</summary>
    public TypedValue? To { get; init; }

    /// <summary>Whether the <see cref="From"/> boundary is inclusive (SEM-4).</summary>
    public bool? FromInclusive { get; init; }

    /// <summary>Whether the <see cref="To"/> boundary is inclusive (SEM-4).</summary>
    public bool? ToInclusive { get; init; }
}
