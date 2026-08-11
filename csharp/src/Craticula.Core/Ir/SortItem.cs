namespace Craticula.Core.Ir;

/// <summary>
/// AR-9: a sort key's direction, matching <c>spec/ir.schema.json</c>'s <c>$defs/sortDirection</c>.
/// Members are PascalCase so <see cref="Craticula.Core.Schema.GridSchemaJson.Options"/>'s CamelCase
/// <c>JsonStringEnumConverter</c> round-trips them against the schema's lowercase enum values
/// (<see cref="Asc"/> -&gt; "asc", <see cref="Desc"/> -&gt; "desc") — same pattern as
/// <see cref="ComparisonOperator"/>.
/// </summary>
public enum SortDirection
{
    Asc,
    Desc
}

/// <summary>
/// AR-9/SEM-3: explicit null placement for a sort key, matching <c>spec/ir.schema.json</c>'s
/// <c>$defs/nullsPosition</c> — always carried explicitly on every <see cref="SortItem"/>, never
/// inferred from <see cref="SortDirection"/> by a compiler (SEM-3).
/// </summary>
public enum NullsPosition
{
    First,
    Last
}

/// <summary>
/// AR-9: a single sort key, matching <c>spec/ir.schema.json</c>'s <c>$defs/sortItem</c>.
/// <see cref="NullsPosition"/> is always explicit in the IR (SEM-3) — never left to a backend's
/// default ordering, and never re-derived by a compiler from <see cref="Direction"/>.
/// </summary>
public sealed record SortItem
{
    public required string Field { get; init; }

    public required SortDirection Direction { get; init; }

    public required NullsPosition NullsPosition { get; init; }
}
