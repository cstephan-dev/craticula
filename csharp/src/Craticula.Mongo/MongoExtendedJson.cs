using System.Globalization;
using System.Text.Json.Nodes;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Mongo;

/// <summary>
/// AR-7/SEM-4: turns a <see cref="TypedValue"/> into the MongoDB Extended JSON node a pipeline
/// embeds. Hand-emitted rather than delegated to a driver's BSON serializer (AR-3: this project
/// takes no NuGet dependency), which also means the two representations SEM-4 pins are chosen here
/// explicitly instead of falling out of some driver's default mapping.
/// </summary>
internal static class MongoExtendedJson
{
    /// <summary>
    /// Encodes <paramref name="value"/> against its field's DECLARED type — the authority, not the
    /// value's surface shape, exactly as <c>Craticula.Sql.SqlQueryBuilder.EncodeValue</c> does.
    /// </summary>
    public static JsonNode? Encode(TypedValue value, FieldConfig field, string fieldName)
    {
        RequireDeclaredType(value, field, fieldName);

        return value switch
        {
            StringValue s => JsonValue.Create(s.Value),
            Int64Value i => JsonValue.Create(i.Value),

            // AR-7: a decimal keeps its exact decimal text. Extended JSON's $numberDecimal is a
            // Decimal128 literal whose payload is a STRING for precisely this reason — routing it
            // through a JSON number (an IEEE double) would lose precision silently, and `decimal`
            // and `double` are otherwise the same wire shape distinguished only by the schema.
            DecimalValue d => new JsonObject { ["$numberDecimal"] = JsonValue.Create(d.Value) },

            DoubleValue d => JsonValue.Create(d.Value),
            BooleanValue b => JsonValue.Create(b.Value),

            // SEM-4, pinned: a dateValue is a CALENDAR date and compiles to the plain ISO string,
            // never a BSON Date. Every BSON Date is an instant, so representing a zone-less calendar
            // date as one requires promoting it to "midnight in some zone" — a question a value that
            // was never an instant has no answer to. Lexicographic ordering of `YYYY-MM-DD` already
            // matches chronological ordering, so $gte/$lt work with no promotion and no ambiguity.
            DateValue d => JsonValue.Create(d.Value),

            // ...while a datetimeValue IS an instant and compiles to a BSON Date. $date requires a
            // full instant with an explicit offset/Z, never a bare calendar date.
            DatetimeValue dt => new JsonObject { ["$date"] = JsonValue.Create(NormalizeInstant(dt.Value)) },

            // AR-7's nullValue. Bound as a JSON null rather than rewritten into an existence check:
            // SEM-2's null handling goes through blank/notBlank, which carry no value at all, so
            // rewriting would be inventing a semantics no spec text or artifact states.
            NullValue => null,

            _ => throw new MongoCompilationException($"Unknown TypedValue type '{value.GetType().Name}'.")
        };
    }

    /// <summary>
    /// SEM-4: a <c>datetime</c> is the absolute instant, normalised to UTC with a trailing <c>Z</c>,
    /// and <see cref="DatetimeValue.Zone"/> is NOT carried into the pipeline — it is provenance
    /// recording how the adapter resolved the wire string, never an instruction to the database
    /// (SEM-4's second Must-NOT: no <c>$dateTrunc</c> timezone, no zone clause of any kind).
    /// Normalising rather than emitting verbatim is what stops the same instant written
    /// <c>+02:00</c> and written <c>Z</c> from producing two different artifacts.
    ///
    /// <para><b>Load-bearing dependency:</b> this is safe from machine-local-time drift only because
    /// <see cref="MongoPipelineBuilder.Build"/> runs <see cref="FilterNodeValidator"/> first, and its
    /// datetime pattern mandates a trailing <c>Z</c> or an explicit offset. Without that guarantee
    /// <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
    /// would assume the HOST's local zone for a zone-less string, and the same fixture would compile
    /// to a different instant on a different machine.</para>
    /// </summary>
    public static string NormalizeInstant(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant))
        {
            throw new MongoCompilationException(
                $"datetimeValue '{value}' is not an ISO-8601 instant (FilterNodeValidator should have rejected it upstream).");
        }

        var utc = instant.UtcDateTime;
        var format = utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-dd'T'HH:mm:ss'Z'"
            : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

        return utc.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Defense in depth against a caller that skipped SEM-5 coercion. A variant that disagrees with
    /// the declared type is exactly the <c>decimal</c>-vs-<c>double</c> divergence
    /// <see cref="Encode"/> exists to prevent — <c>{"$numberDecimal": "19.99"}</c> and <c>19.99</c>
    /// are different documents — so it fails loudly here instead of producing a plausible-looking
    /// artifact. <see cref="NullValue"/> is accepted against any declared type, matching
    /// <see cref="TypeCoercionValidator"/>'s own rule.
    /// </summary>
    private static void RequireDeclaredType(TypedValue value, FieldConfig field, string fieldName)
    {
        var matches = (value, field.Type) switch
        {
            (NullValue, _) => true,
            (StringValue, FieldType.String) => true,
            (Int64Value, FieldType.Int64) => true,
            (DecimalValue, FieldType.Decimal) => true,
            (DoubleValue, FieldType.Double) => true,
            (BooleanValue, FieldType.Boolean) => true,
            (DateValue, FieldType.Date) => true,
            (DatetimeValue, FieldType.Datetime) => true,
            _ => false
        };

        if (!matches)
        {
            throw new MongoCompilationException(
                $"A '{value.GetType().Name}' cannot be embedded against field '{fieldName}', declared " +
                $"'{field.Type}' — run TypeCoercionValidator (SEM-5) before compiling.");
        }
    }
}
