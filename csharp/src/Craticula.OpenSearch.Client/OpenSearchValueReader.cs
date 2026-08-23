using System.Globalization;
using System.Text.Json;
using Craticula.Core.Schema;

namespace Craticula.OpenSearch.Client;

/// <summary>
/// #1622/#1652/#1653's native-CLR-values obligation, implemented for real: reads one cell through the
/// schema's DECLARED <see cref="FieldType"/> rather than trusting whatever JSON shape a raw hit or
/// aggregation bucket happened to carry it as — mirrors <c>Craticula.Dapper.DapperValueReader</c>'s and
/// <c>Craticula.MongoDB.Driver.MongoValueReader</c>'s own "accept the value unchanged when it already
/// arrived as the target shape, convert/throw loudly otherwise" discipline.
///
/// <para><b>The "wrapper type" hazard those two guard against is genuinely smaller here.</b> A
/// <see cref="JsonElement"/> off <c>System.Text.Json</c> is already fairly native — no
/// <see cref="MongoDB.Bson.BsonValue"/>-style subtype zoo, no ADO.NET provider-specific CLR surprise —
/// but the explicit per-<see cref="FieldType"/> dispatch still matters for two real fidelity questions
/// this reader is the one place that answers: whether a numeric token round-trips through
/// <see cref="decimal"/> without going through <see cref="double"/> first (<see cref="Read"/>'s own
/// <c>_source</c> path, where the compiler's own <c>SearchJson.Encode</c> convention keeps a
/// <c>decimal</c> field's stored JSON number token exact), and how an aggregation METRIC's <c>value</c>
/// — which OpenSearch/Elasticsearch always computes and serializes in double-precision floating point,
/// REGARDLESS of the source field's own mapped type — should read back for a schema that still declares
/// that field <see cref="FieldType.Decimal"/> (see <see cref="ReadMetric"/>'s own remarks; that one is
/// this backend's own accepted divergence, with no counterpart on the other three).</para>
/// </summary>
internal static class OpenSearchValueReader
{
    /// <summary>
    /// Reads <paramref name="key"/> off a hit's <c>_source</c> object as <paramref name="type"/>'s
    /// declared CLR shape, or <see langword="null"/> when the field is absent OR holds a JSON null.
    /// </summary>
    public static object? Read(JsonElement source, string key, FieldType type)
    {
        if (!source.TryGetProperty(key, out var raw) || raw.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return Dispatch(raw, type, key);
    }

    /// <summary>
    /// A grouped level's bucket key — <c>bucket.key.&lt;groupColumn&gt;</c> — read through the SAME
    /// dispatch as an ordinary <c>_source</c> field: a composite source's key is a plain term value
    /// (a keyword sub-field's string, a raw number, ...), not a computed metric, so none of
    /// <see cref="ReadMetric"/>'s double-precision concerns apply to it. <see langword="null"/> for
    /// AR-8/BM-2's null bucket (<c>missing_bucket: true</c>).
    /// </summary>
    public static object? ReadBucketKey(JsonElement bucket, string groupColumn, FieldType type)
    {
        var key = bucket.GetProperty("key").GetProperty(groupColumn);
        return key.ValueKind == JsonValueKind.Null ? null : Dispatch(key, type, groupColumn);
    }

    /// <summary>
    /// One aggregation's own <c>{outputName}.value</c> — AGG-6's null-group answer
    /// (<see langword="null"/> for <c>avg</c>/<c>min</c>/<c>max</c>, <c>0</c> for <c>sum</c>) reaches
    /// here as a JSON <c>null</c>/<c>0</c> respectively, already satisfied natively by the engine (see
    /// <c>spec/semantics.md</c>'s own AGG-6 cross-link for this backend).
    ///
    /// <para><b>Load-bearing, backend-specific fact this method exists to make explicit rather than
    /// silently correct-by-luck.</b> Every metric aggregation in AR-8's closed set —
    /// <c>sum</c>/<c>min</c>/<c>max</c>/<c>avg</c>/<c>value_count</c> — computes and serializes its
    /// <c>value</c> in DOUBLE-PRECISION FLOATING POINT on this backend, unconditionally, regardless of
    /// the source field's own mapped type. MongoDB's equivalent divergence (<c>MongoValueReader</c>'s own
    /// remarks) is narrower: only <c>$avg</c> promotes away from the source type, and only when the
    /// underlying documents happen to store a <c>NumberDecimal</c>. Here EVERY metric over EVERY numeric
    /// field goes through <see cref="double"/> at the ENGINE itself, before this reader ever sees the
    /// response — so declaring the output <see cref="FieldType.Decimal"/> (this package's own
    /// <c>ResolveAggregationType</c>, matching every sibling's declared-output-type convention) cannot
    /// recover precision that was already lost server-side; <see cref="Convert.ToDecimal(double, IFormatProvider)"/>
    /// converts the double FAITHFULLY, it does not un-lose anything. A test dataset built from values
    /// that happen to be exact in binary floating point (multiples of a power-of-two fraction, e.g.
    /// <c>0.25</c>/<c>0.50</c>/<c>0.75</c>) round-trips perfectly for exactly that reason — it is not
    /// evidence this reader preserves arbitrary decimal precision, only that THOSE particular values
    /// have no rounding to lose.</para>
    ///
    /// <para><see cref="FieldType.Int64"/> (<c>count</c>, or <c>sum</c>/<c>min</c>/<c>max</c> over an
    /// int64 field) goes through the same double read then narrows via a checked cast — safe for any
    /// magnitude a grid's own aggregate total would plausibly reach, and consistent with reading every
    /// numeric metric uniformly rather than special-casing <c>value_count</c>'s own (also
    /// engine-dependent) integer-looking JSON spelling.</para>
    ///
    /// <para><see cref="FieldType.Date"/>/<see cref="FieldType.Datetime"/> (a <c>min</c>/<c>max</c> over
    /// a date-mapped field) reads <c>{outputName}.value_as_string</c> instead of the epoch-millisecond
    /// <c>value</c>, since a raw epoch-millisecond double is not itself SEM-4's calendar-date/instant
    /// shape. <b>Not a fixed ISO-8601 string, despite what this doc previously claimed</b>: the engine
    /// renders <c>value_as_string</c> using the FIELD MAPPING'S OWN declared <c>format</c> (e.g.
    /// <c>strict_date</c>, <c>strict_date_optional_time</c>, or whatever a host's own mapping declares),
    /// not a fixed spelling — <see cref="ToDateOnly"/>'s hard-coded <c>"yyyy-MM-dd"</c> parse only
    /// matches because this suite's own <c>OpenSearchFixtureDatabase.BuildFieldMapping</c> always chooses
    /// <c>"format": "strict_date"</c> for a <see cref="FieldType.Date"/> field. A differently-mapped
    /// index (or the engine's own DEFAULT <c>strict_date_optional_time</c>) throws a
    /// <see cref="FormatException"/> here — this method's own widened <c>try</c> below at least catches
    /// and truncates it now, rather than letting it escape unhandled with the untruncated value in its
    /// own message (review finding), but the underlying format mismatch itself is not fixed by this
    /// story.</para>
    /// </summary>
    public static object? ReadMetric(JsonElement bucket, string outputName, FieldType type)
    {
        try
        {
            var metric = bucket.GetProperty(outputName);

            if (type is FieldType.Date or FieldType.Datetime)
            {
                if (!metric.TryGetProperty("value_as_string", out var formatted) || formatted.ValueKind == JsonValueKind.Null)
                {
                    return null;
                }

                return type == FieldType.Date ? ToDateOnly(formatted, outputName) : ToDateTime(formatted, outputName);
            }

            var value = metric.GetProperty("value");
            if (value.ValueKind == JsonValueKind.Null)
            {
                return null;
            }

            return type switch
            {
                FieldType.Int64 => checked((long)value.GetDouble()),
                FieldType.Double => value.GetDouble(),
                FieldType.Decimal => Convert.ToDecimal(value.GetDouble(), CultureInfo.InvariantCulture),
                _ => throw new OpenSearchClientCompilationException(
                    $"Aggregation '{outputName}' resolved to metric type {type}, but only numeric or date/datetime " +
                    "outputs are defined for a metric aggregation's value.")
            };
        }
        // Mirrors Dispatch's own filter (review finding: this arm previously caught
        // OverflowException/InvalidCastException — the latter unreachable here, since nothing in this
        // method casts a reference type, while InvalidOperationException (JsonElement.GetDouble()/
        // GetProperty() on the wrong ValueKind) and FormatException (ToDateOnly/ToDateTime's own
        // ParseExact) — the two exceptions this method's own arms can actually throw — escaped
        // uncaught, with an untruncated message, exactly the gap already closed twice before on Dapper
        // (#1646) and Mongo (#1647)). Also review finding: the property-access and date-parsing
        // statements used to sit OUTSIDE the try entirely — widened to cover the whole method so a bad
        // metric shape is caught here rather than escaping raw.
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // Review finding: the raw ex was previously attached as innerException, and since .NET Core
            // 3.0 a framework parse exception (FormatException/OverflowException/...) quotes the FULL,
            // untruncated input in ITS OWN message — defeating this method's own 32-character truncation
            // the moment anything logs the exception chain rather than only the top message. No inner
            // exception is attached here for that reason (defense in depth, not a redesign).
            throw new OpenSearchClientCompilationException(
                $"Aggregation '{outputName}' could not be read as {type} — {ex.GetType().Name}.");
        }
    }

    private static object Dispatch(JsonElement raw, FieldType type, string key)
    {
        try
        {
            return type switch
            {
                FieldType.String => ToStringValue(raw, key),
                FieldType.Int64 => ToInt64(raw, key),
                FieldType.Decimal => ToDecimal(raw, key),
                FieldType.Double => ToDouble(raw, key),
                FieldType.Boolean => ToBoolean(raw, key),
                FieldType.Date => ToDateOnly(raw, key),
                FieldType.Datetime => ToDateTime(raw, key),
                _ => throw new OpenSearchClientCompilationException($"Unmapped FieldType '{type}'.")
            };
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            // No inner exception attached — review finding: since .NET Core 3.0, a framework parse
            // exception (FormatException/OverflowException/...) quotes the FULL, untruncated input in
            // ITS OWN message, defeating this method's own truncation the moment anything logs the
            // exception chain rather than only the top message.
            throw new OpenSearchClientCompilationException(
                $"Field '{key}' value '{Truncate(raw)}' ({raw.ValueKind}) could not be read as {type} — {ex.GetType().Name}.");
        }
    }

    private static string ToStringValue(JsonElement raw, string key) => raw.ValueKind switch
    {
        JsonValueKind.String => raw.GetString()!,
        JsonValueKind.Number => raw.GetRawText(),
        _ => throw new OpenSearchClientCompilationException(
            $"Field '{key}' value '{Truncate(raw)}' ({raw.ValueKind}) could not be read as a string (FieldType.String).")
    };

    private static long ToInt64(JsonElement raw, string key) => raw.ValueKind switch
    {
        JsonValueKind.Number => raw.GetInt64(),
        JsonValueKind.String => long.Parse(raw.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new OpenSearchClientCompilationException(
            $"Field '{key}' value '{Truncate(raw)}' ({raw.ValueKind}) could not be read as an integer (FieldType.Int64).")
    };

    /// <summary>
    /// AR-7's <c>decimal</c> shape off <c>_source</c> — safe to read EXACTLY here (unlike
    /// <see cref="ReadMetric"/>'s own arm) because <c>Craticula.Search.SearchJson.Encode</c> writes a
    /// <c>decimal</c> field's compiled comparisons as the IR's own exact decimal TEXT re-parsed as a JSON
    /// number token, never rounded through a binary float — and this reader's own test fixtures seed
    /// <c>_source</c> the identical way, so <see cref="JsonElement.GetDecimal"/> (which parses the raw
    /// UTF-8 number token directly into a <see cref="decimal"/>, never bouncing through
    /// <see cref="double"/>) round-trips it exactly.
    /// </summary>
    private static decimal ToDecimal(JsonElement raw, string key) => raw.ValueKind switch
    {
        JsonValueKind.Number => raw.GetDecimal(),
        JsonValueKind.String => decimal.Parse(raw.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new OpenSearchClientCompilationException(
            $"Field '{key}' value '{Truncate(raw)}' ({raw.ValueKind}) could not be read as a decimal (FieldType.Decimal).")
    };

    private static double ToDouble(JsonElement raw, string key) => raw.ValueKind switch
    {
        JsonValueKind.Number => raw.GetDouble(),
        JsonValueKind.String => double.Parse(raw.GetString()!, CultureInfo.InvariantCulture),
        _ => throw new OpenSearchClientCompilationException(
            $"Field '{key}' value '{Truncate(raw)}' ({raw.ValueKind}) could not be read as a double (FieldType.Double).")
    };

    private static bool ToBoolean(JsonElement raw, string key) => raw.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(raw.GetString(), out var parsed) => parsed,
        _ => throw new OpenSearchClientCompilationException(
            $"Field '{key}' value '{Truncate(raw)}' ({raw.ValueKind}) could not be read as a boolean (FieldType.Boolean).")
    };

    /// <summary>
    /// SEM-4's calendar date — <c>Craticula.Search.SearchJson.Encode</c>'s own convention: a
    /// <c>dateValue</c> compiles a comparison against the plain ISO <c>yyyy-MM-dd</c> string this reader
    /// expects a <c>date</c>-declared field to be indexed as.
    /// </summary>
    private static DateOnly ToDateOnly(JsonElement raw, string key) => raw.ValueKind switch
    {
        JsonValueKind.String => DateOnly.ParseExact(raw.GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture),
        _ => throw new OpenSearchClientCompilationException(
            $"Field '{key}' value '{Truncate(raw)}' ({raw.ValueKind}) could not be read as a date (FieldType.Date).")
    };

    /// <summary>
    /// SEM-4's absolute instant — <c>SearchJson.Encode</c>'s own convention: a <c>datetimeValue</c>
    /// compiles against an ISO-8601 instant string normalised to UTC, which this reader accepts and
    /// re-normalises defensively (a document seeded through a different path could carry an explicit
    /// offset).
    /// </summary>
    private static DateTime ToDateTime(JsonElement raw, string key) => raw.ValueKind switch
    {
        JsonValueKind.String => DateTime.Parse(
            raw.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal),
        _ => throw new OpenSearchClientCompilationException(
            $"Field '{key}' value '{Truncate(raw)}' ({raw.ValueKind}) could not be read as a datetime (FieldType.Datetime).")
    };

    /// <summary>
    /// Bounds how much of a raw value reaches an exception message a host will typically log — mirrors
    /// <c>DapperValueReader.Truncate</c>/<c>MongoValueReader.Truncate</c>'s own 32-character bound and
    /// their own provenance distinction: this echoes a value already IN THE INDEX (or a computed
    /// aggregation output), which the caller's own request may have deliberately excluded from what it
    /// asked to see.
    /// </summary>
    internal static string Truncate(JsonElement value)
    {
        var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.GetRawText();
        return text.Length <= 32 ? text : text[..32] + "...";
    }
}
