namespace Demo.Api.Seed;

/// <summary>One synthetic order. Nullable members mirror the nullable columns / BSON nulls.
/// OrderedAt is UTC at millisecond precision (SEM-4 fixture rule: no engine may truncate it differently).</summary>
public sealed record OrderRow(
    long Id,
    string OrderNo,
    string? Customer,
    string? Country,
    string? Category,
    string Status,
    decimal? Amount,
    long? Quantity,
    DateTime OrderedAt,
    DateOnly? ShippedOn,
    bool IsPriority);
