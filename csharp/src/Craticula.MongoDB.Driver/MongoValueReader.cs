using System.Globalization;
using Craticula.Core.Schema;
using MongoDB.Bson;

namespace Craticula.MongoDB.Driver;

/// <summary>
/// #1622/#1652/#1653's native-CLR-values obligation, implemented for real: reads one cell through the
/// schema's DECLARED <see cref="FieldType"/> rather than trusting whatever <see cref="BsonValue"/>
/// subtype the driver happened to hand back for it — mirrors <c>Craticula.Dapper.DapperValueReader</c>'s
/// own "accept the value unchanged when it already arrived as the target shape, convert/throw loudly
/// otherwise" discipline, but over a BIGGER surface.
///
/// <para><b>Why the surface is bigger than Dapper's.</b> Reading a raw <see cref="BsonDocument"/> means
/// EVERY field arrives as some <see cref="BsonValue"/> subtype (<see cref="BsonInt64"/>,
/// <see cref="BsonInt32"/>, <see cref="BsonDecimal128"/>, <see cref="BsonDouble"/>,
/// <see cref="BsonBoolean"/>, <see cref="BsonString"/>, <see cref="BsonDateTime"/>,
/// <see cref="BsonNull"/>, <see cref="BsonObjectId"/>, ...) rather than Dapper's "native CLR type is
/// the common case, wrapper is the exception" — an ADO.NET provider mostly hands back plain
/// <see cref="long"/>/<see cref="string"/>/<see cref="decimal"/> already.</para>
///
/// <para><b>Two conventions this reader depends on, both set by
/// <c>Craticula.Mongo.MongoExtendedJson.Encode</c> on the COMPILER side, and this reader's own
/// materialization has to agree with them for a comparison to even be meaningful</b> (a stored value in
/// the wrong BSON shape compares wrong against a compiled filter long before this reader ever runs):
/// a <c>date</c> field is a plain ISO <c>yyyy-MM-dd</c> STRING (never a BSON Date — SEM-4's calendar
/// date has no instant to promote to one), while a <c>datetime</c> field is a real BSON Date (SEM-4's
/// absolute instant). This reader accepts the OTHER shape too, defensively, for a document seeded
/// through a different path — but a schema's own dataset should match the convention its filters will
/// be compiled against.</para>
/// </summary>
internal static class MongoValueReader
{
    /// <summary>
    /// Reads <paramref name="key"/> off <paramref name="document"/> as <paramref name="type"/>'s
    /// declared CLR shape, or <see langword="null"/> when the field is absent OR holds a BSON null —
    /// MongoDB's schemaless documents make "missing" a real, distinct possibility Dapper's fixed-column
    /// ADO.NET rows never present, and both collapse to the same CLR <see langword="null"/> a grid cell
    /// cannot tell apart anyway.
    ///
    /// <para><b>Every conversion failure is caught and re-thrown here, not left to whichever framework
    /// method noticed it</b> — mirroring <c>DapperValueReader.Read</c>'s own reasoning: parsing methods
    /// and the driver's own <see cref="BsonValue.ToInt64"/>/<c>ToDecimal</c>/<c>ToDouble</c> family embed
    /// the UNTRUNCATED value in their own exception message, which <see cref="Truncate"/> exists to
    /// bound.</para>
    /// </summary>
    public static object? Read(BsonDocument document, string key, FieldType type)
    {
        if (!document.TryGetValue(key, out var raw) || raw.IsBsonNull)
        {
            return null;
        }

        try
        {
            return type switch
            {
                FieldType.String => ToStringValue(raw),
                FieldType.Int64 => raw.ToInt64(),
                FieldType.Decimal => raw.ToDecimal(),
                FieldType.Double => raw.ToDouble(),
                FieldType.Boolean => raw.ToBoolean(),
                FieldType.Date => ToDateOnly(raw),
                FieldType.Datetime => ToDateTime(raw),
                _ => throw new MongoCompilationException($"Unmapped FieldType '{type}'.")
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException or NotSupportedException)
        {
            throw new MongoCompilationException(
                $"Document field '{key}' value '{Truncate(raw)}' ({raw.BsonType}) could not be read as {type} — " +
                $"{ex.GetType().Name}.",
                ex);
        }
    }

    /// <summary>
    /// A <see cref="BsonString"/> unchanged, or a <see cref="BsonObjectId"/> converted to its 24-character
    /// hex form — the one case the plan itself flags as a "verify a fixture actually needs this before
    /// over-building" possibility: a schema field whose <c>physical.mongo.path</c> is the document's own
    /// <c>_id</c> and whose declared <see cref="FieldType"/> is <see cref="FieldType.String"/> would seed
    /// an <see cref="ObjectId"/>-typed <c>_id</c> rather than a plain string one. This suite's own
    /// <c>_id</c>-mapped fixture (<c>page-export/008</c>) declares that field <see cref="FieldType.Int64"/>
    /// instead, so this arm is defensive rather than currently exercised — cheap to keep, since it costs
    /// nothing beyond the branch, and it is the conversion Dapper's own precedent has no counterpart for
    /// at all (ADO.NET has no notion of an object id).
    /// </summary>
    private static string ToStringValue(BsonValue raw) => raw switch
    {
        BsonString s => s.Value,
        BsonObjectId oid => oid.Value.ToString(),
        _ => throw new MongoCompilationException(
            $"Document value '{Truncate(raw)}' ({raw.BsonType}) could not be read as a string (FieldType.String).")
    };

    /// <summary>
    /// SEM-4's calendar date. The primary shape is the plain ISO string
    /// <c>Craticula.Mongo.MongoExtendedJson.Encode</c> compiles a <c>dateValue</c> filter bound against
    /// — a zone-less calendar date has no instant to promote to "midnight in some zone", so a document
    /// stored that way is what a compiled <c>$gte</c>/<c>$lt</c> actually compares against. A real BSON
    /// Date is accepted too (its own UTC calendar date), for a document seeded through a path that chose
    /// the richer type.
    /// </summary>
    private static DateOnly ToDateOnly(BsonValue raw) => raw switch
    {
        BsonString s => DateOnly.ParseExact(s.Value, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        BsonDateTime dt => DateOnly.FromDateTime(dt.ToUniversalTime()),
        _ => throw new MongoCompilationException(
            $"Document value '{Truncate(raw)}' ({raw.BsonType}) could not be read as a date (FieldType.Date).")
    };

    /// <summary>
    /// SEM-4's absolute instant, stored as a real BSON Date — <c>MongoExtendedJson.Encode</c>'s
    /// <c>{"$date": ...}</c> compiles a <c>datetimeValue</c> comparison against exactly that BSON type.
    /// Always normalized to UTC, matching the compiler's own "instant normalised to UTC" convention. A
    /// plain ISO-8601 instant string is accepted too, for a document seeded through a text-affinity path.
    /// </summary>
    private static DateTime ToDateTime(BsonValue raw) => raw switch
    {
        BsonDateTime dt => DateTime.SpecifyKind(dt.ToUniversalTime(), DateTimeKind.Utc),
        BsonString s => DateTime.Parse(
            s.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        _ => throw new MongoCompilationException(
            $"Document value '{Truncate(raw)}' ({raw.BsonType}) could not be read as a datetime (FieldType.Datetime).")
    };

    /// <summary>
    /// Bounds how much of a raw DOCUMENT value reaches an exception message a host will typically log —
    /// mirroring <c>DapperValueReader.Truncate</c>'s own 32-character bound and its own provenance
    /// distinction: this echoes a value already IN THE DATABASE, which the caller's own projection may
    /// have deliberately excluded from what it asked to see.
    /// </summary>
    internal static string Truncate(BsonValue value)
    {
        var text = value.ToString() ?? string.Empty;
        return text.Length <= 32 ? text : text[..32] + "...";
    }
}
