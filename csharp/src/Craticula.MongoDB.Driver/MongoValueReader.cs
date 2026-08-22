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
    /// method noticed it</b> — mirroring <c>DapperValueReader.Read</c>'s own reasoning: the parsing
    /// methods this file calls (<see cref="Convert.ToInt64(object?, IFormatProvider?)"/> and its
    /// siblings, <see cref="DateOnly.ParseExact(string, string, IFormatProvider?)"/>,
    /// <see cref="DateTime.Parse(string, IFormatProvider?, DateTimeStyles)"/>) embed the UNTRUNCATED
    /// value in their own exception message, which <see cref="Truncate"/> exists to bound. Deliberately
    /// NOT the driver's own <see cref="BsonValue.ToInt64"/>/<c>ToDecimal</c>/<c>ToDouble</c>/<c>ToBoolean</c>
    /// family — see <see cref="ToInt64"/>/<see cref="ToBoolean"/>'s own remarks for why those are
    /// coercions that silently produce a wrong value instead of throwing.</para>
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
                FieldType.Int64 => ToInt64(raw),
                FieldType.Decimal => ToDecimal(raw),
                FieldType.Double => ToDouble(raw),
                FieldType.Boolean => ToBoolean(raw),
                FieldType.Date => ToDateOnly(raw),
                FieldType.Datetime => ToDateTime(raw),
                _ => throw new MongoDriverCompilationException($"Unmapped FieldType '{type}'.")
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidCastException
                                        or NotSupportedException or ArgumentOutOfRangeException)
        {
            throw new MongoDriverCompilationException(
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
        _ => throw new MongoDriverCompilationException(
            $"Document value '{Truncate(raw)}' ({raw.BsonType}) could not be read as a string (FieldType.String).")
    };

    /// <summary>
    /// AR-7's integer shape. Deliberately does NOT call <see cref="BsonValue.ToInt64"/>: that method is
    /// a MongoDB COERCION, not a conversion — it SATURATES to <see cref="long.MaxValue"/>/<see cref="long.MinValue"/>
    /// on an out-of-range <see cref="BsonDouble"/> and maps <see cref="double.NaN"/> to <c>0</c>, rather
    /// than throwing, which is exactly the silent-wrong-answer this reader's own doc says it exists to
    /// prevent (measured against MongoDB.Bson 3.11.0 — review found this, not assumed). Routing the
    /// extracted native value through <see cref="Convert.ToInt64(object?, IFormatProvider?)"/> instead
    /// gives the identical loud-overflow behavior <c>Craticula.Dapper.DapperValueReader.ToInt64</c>
    /// already has for the equivalent ADO.NET shape.
    /// </summary>
    private static long ToInt64(BsonValue raw) => raw switch
    {
        BsonInt64 i => i.Value,
        BsonInt32 i => i.Value,
        BsonDouble d => Convert.ToInt64(d.Value, CultureInfo.InvariantCulture),
        BsonDecimal128 d => Convert.ToInt64(Decimal128.ToDecimal(d.Value), CultureInfo.InvariantCulture),
        BsonString s => Convert.ToInt64(s.Value, CultureInfo.InvariantCulture),
        _ => throw new MongoDriverCompilationException(
            $"Document value '{Truncate(raw)}' ({raw.BsonType}) could not be read as an integer (FieldType.Int64).")
    };

    /// <summary>
    /// AR-7's <c>decimal</c> shape — see <see cref="ToInt64"/>'s own remarks on why this does not call
    /// <see cref="BsonValue.ToDecimal"/> directly: the same coercion-not-conversion risk applies to
    /// every numeric arm, not only the integer one.
    /// </summary>
    private static decimal ToDecimal(BsonValue raw) => raw switch
    {
        BsonDecimal128 d => Decimal128.ToDecimal(d.Value),
        BsonDouble d => Convert.ToDecimal(d.Value, CultureInfo.InvariantCulture),
        BsonInt64 i => i.Value,
        BsonInt32 i => i.Value,
        BsonString s => Convert.ToDecimal(s.Value, CultureInfo.InvariantCulture),
        _ => throw new MongoDriverCompilationException(
            $"Document value '{Truncate(raw)}' ({raw.BsonType}) could not be read as a decimal (FieldType.Decimal).")
    };

    /// <summary>
    /// The <see cref="BsonDecimal128"/> arm goes through <see cref="Decimal128.ToDouble(Decimal128)"/>
    /// DIRECTLY rather than <see cref="Decimal128.ToDecimal(Decimal128)"/> then
    /// <see cref="Convert.ToDouble(decimal)"/> — review found the two-step route re-introduces exactly
    /// the class of bug this rewrite exists to remove, by narrowing the domain to <see cref="decimal"/>'s
    /// (~1e±28) before ever reaching <see cref="double"/>'s (~1.8e308): a value like <c>1E-40</c> silently
    /// underflows to <c>0</c> instead of the double it can represent exactly, and a value like <c>1E30</c>
    /// throws <see cref="OverflowException"/> from the decimal step even though it fits comfortably in a
    /// double. `$avg`/`$sum` over a non-decimal source (<c>MongoGridQuery.ResolveAggregationType</c>
    /// widens those to <see cref="FieldType.Double"/>) genuinely returns a <see cref="BsonDecimal128"/>
    /// the moment any input document stores a <c>NumberDecimal</c>, so this is reachable in ordinary use,
    /// not only via a mis-typed document.
    /// </summary>
    private static double ToDouble(BsonValue raw) => raw switch
    {
        BsonDouble d => d.Value,
        BsonInt64 i => i.Value,
        BsonInt32 i => i.Value,
        BsonDecimal128 d => Decimal128.ToDouble(d.Value),
        BsonString s => Convert.ToDouble(s.Value, CultureInfo.InvariantCulture),
        _ => throw new MongoDriverCompilationException(
            $"Document value '{Truncate(raw)}' ({raw.BsonType}) could not be read as a double (FieldType.Double).")
    };

    /// <summary>
    /// Deliberately does NOT call <see cref="BsonValue.ToBoolean"/>: that method implements MongoDB
    /// query-language TRUTHINESS (<c>default: return true</c>) rather than a boolean conversion, so it
    /// is a TOTAL function that can never throw — a <see cref="FieldType.Boolean"/> field holding the
    /// STRING <c>"false"</c> (a common shape for a collection seeded from CSV/JSON) reads back as
    /// <see langword="true"/>, silently, with no exception this method's own <c>catch</c> could ever
    /// observe. Explicit and narrow instead: a native <see cref="BsonBoolean"/> unchanged, or a string
    /// that <see cref="bool.TryParse(string?, out bool)"/> itself accepts — everything else throws
    /// loudly, matching every other arm in this file.
    /// </summary>
    private static bool ToBoolean(BsonValue raw) => raw switch
    {
        BsonBoolean b => b.Value,
        BsonString s when bool.TryParse(s.Value, out var parsed) => parsed,
        _ => throw new MongoDriverCompilationException(
            $"Document value '{Truncate(raw)}' ({raw.BsonType}) could not be read as a boolean (FieldType.Boolean).")
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
        _ => throw new MongoDriverCompilationException(
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
        _ => throw new MongoDriverCompilationException(
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
