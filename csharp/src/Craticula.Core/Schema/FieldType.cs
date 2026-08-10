namespace Craticula.Core.Schema;

/// <summary>
/// The declared type used by the AG Grid adapter to coerce this field's wire values into a
/// typedValue (SEM-5) — matches the type tags in <c>ir.schema.json</c>'s <c>$defs/typedValue</c>
/// one-for-one.
/// </summary>
public enum FieldType
{
    String,
    Int64,
    Decimal,
    Double,
    Boolean,
    Date,
    Datetime
}
