using System.Text.Json.Serialization;

namespace Craticula.Core.Ir;

/// <summary>
/// AR-7: a fully-typed IR value, matching <c>spec/ir.schema.json</c>'s <c>$defs/typedValue</c>
/// one-for-one. Every value is tagged with its declared <c>type</c>; deserialized via System.Text.
/// Json polymorphic serialization (discriminated on <c>type</c>) using
/// <see cref="Craticula.Core.Schema.GridSchemaJson.Options"/>, so no hand-written parsing code is
/// needed here, consistent with that options class's own "no hand-written parsing code" rationale.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(StringValue), "string")]
[JsonDerivedType(typeof(Int64Value), "int64")]
[JsonDerivedType(typeof(DecimalValue), "decimal")]
[JsonDerivedType(typeof(DoubleValue), "double")]
[JsonDerivedType(typeof(BooleanValue), "boolean")]
[JsonDerivedType(typeof(DateValue), "date")]
[JsonDerivedType(typeof(DatetimeValue), "datetime")]
[JsonDerivedType(typeof(NullValue), "null")]
public abstract record TypedValue;

public sealed record StringValue : TypedValue
{
    public required string Value { get; init; }
}

/// <summary>64-bit integer (AR-7 distinguishes int64 from decimal/double, never a generic "number").</summary>
public sealed record Int64Value : TypedValue
{
    public required long Value { get; init; }
}

/// <summary>
/// AR-7: arbitrary-precision decimal. Carried as a numeric string (not a JSON number), matching
/// <c>ir.schema.json</c>'s <c>decimalValue</c> exactly, to avoid float round-tripping loss.
/// </summary>
public sealed record DecimalValue : TypedValue
{
    public required string Value { get; init; }
}

/// <summary>IEEE-754 double-precision float (AR-7).</summary>
public sealed record DoubleValue : TypedValue
{
    public required double Value { get; init; }
}

public sealed record BooleanValue : TypedValue
{
    public required bool Value { get; init; }
}

/// <summary>AR-7: local date, no time-of-day or zone component (ISO-8601 date string).</summary>
public sealed record DateValue : TypedValue
{
    public required string Value { get; init; }
}

/// <summary>
/// AR-7/SEM-4: an instant plus its interpretation zone. <see cref="Value"/> is an ISO-8601
/// timestamp with an explicit offset/Z; <see cref="Zone"/> is the IANA time zone id used to resolve
/// the instant.
/// </summary>
public sealed record DatetimeValue : TypedValue
{
    public required string Value { get; init; }

    public required string Zone { get; init; }
}

/// <summary>
/// AR-7: the null value. Distinct from the blank/notBlank operators, which are a separate SEM-2
/// null-handling concern at the comparison-node level rather than a value.
/// </summary>
public sealed record NullValue : TypedValue;
