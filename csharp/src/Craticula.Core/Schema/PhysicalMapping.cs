namespace Craticula.Core.Schema;

/// <summary>
/// SC-4: the SQL physical mapping shapes — plain column, qualified column (<c>t.col</c>), or a raw
/// SQL expression escape hatch, always declared server-side, never client-influenced. The JSON
/// schema models these as a <c>oneOf</c> of three mutually-exclusive single-property objects;
/// here that is modeled pragmatically as one record with three nullable properties, of which
/// exactly one is expected to be non-null.
/// </summary>
public sealed record SqlMapping
{
    public string? Column { get; init; }

    public string? QualifiedColumn { get; init; }

    public string? Expression { get; init; }

    /// <summary>
    /// Enforces the meta-schema's <c>oneOf</c>: exactly one of the three shapes must be set.
    /// Called from <see cref="GridSchema.Parse"/> — not in the record's own constructor, since
    /// System.Text.Json populates properties via init-accessors after construction, so a
    /// constructor-time check would run before any property has been set.
    /// </summary>
    internal bool IsValid =>
        (Column is not null ? 1 : 0) + (QualifiedColumn is not null ? 1 : 0) + (Expression is not null ? 1 : 0) == 1;
}

/// <summary>SC-4 nested document path mapping, e.g. <c>"address.city"</c>.</summary>
public sealed record MongoMapping
{
    public required string Path { get; init; }
}

/// <summary>
/// SC-4/SC-3 search-backend mapping: the analyzed field plus its optional exact-match
/// (<see cref="KeywordSubField"/>) and case-insensitive-ordering (<see cref="NormalizedSubField"/>)
/// sub-fields.
/// </summary>
public sealed record SearchMapping
{
    public required string Field { get; init; }

    public string? KeywordSubField { get; init; }

    public string? NormalizedSubField { get; init; }
}

/// <summary>SC-4: per-backend physical mapping. A field need only declare mappings for the
/// backends the fixture actually targets.</summary>
public sealed record PhysicalMapping
{
    public SqlMapping? Sql { get; init; }

    public MongoMapping? Mongo { get; init; }

    public SearchMapping? Search { get; init; }
}
