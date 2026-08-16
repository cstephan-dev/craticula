using System.Globalization;
using System.Text.Json.Nodes;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Search;

/// <summary>
/// AR-7/SEM-4: turns a <see cref="TypedValue"/> into the JSON node an OpenSearch/Elasticsearch query
/// embeds, and answers the one related question the DSL asks that the other two backends do not —
/// which <c>format</c> a <c>range</c> spells for a date-typed field.
///
/// <para>Hand-emitted rather than delegated to a client library's serializer (AR-3: this project takes
/// no NuGet dependency), which also means the representations below are chosen here explicitly rather
/// than inherited from whichever of the two vendors' clients happened to be referenced.</para>
/// </summary>
internal static class SearchJson
{
    /// <summary>
    /// Encodes <paramref name="value"/> against its field's DECLARED type — the authority, not the
    /// value's surface shape, exactly as <c>Craticula.Sql.SqlQueryBuilder.EncodeValue</c> and
    /// <c>Craticula.Mongo.MongoExtendedJson.Encode</c> do.
    /// </summary>
    public static JsonNode? Encode(TypedValue value, FieldConfig field, string fieldName)
    {
        RequireDeclaredType(value, field, fieldName);

        return value switch
        {
            StringValue s => JsonValue.Create(s.Value),
            Int64Value i => JsonValue.Create(i.Value),

            // AR-7: the JSON request body is the only channel this DSL has, and it carries no
            // arbitrary-precision numeric type the way MongoDB's Extended JSON carries
            // $numberDecimal — so a `decimal` is written as a JSON number whose TEXT is the IR's own
            // exact decimal literal, parsed rather than round-tripped through a binary float.
            // System.Text.Json preserves the token, so `19.99` stays `19.99` and a 30-digit value
            // stays 30 digits; what the server then does with it is a mapping question (a host
            // needing exact decimal arithmetic maps the field `scaled_float` or `keyword`), which is
            // a PERF-4 index-guidance obligation rather than something the compiler can decide.
            // `spec/fixtures/operators/046`/`047` and `group-aggregation/020` pin the bare number.
            DecimalValue d => ParseNumber(d.Value, fieldName),

            DoubleValue d => JsonValue.Create(d.Value),
            BooleanValue b => JsonValue.Create(b.Value),

            // SEM-4: a dateValue is a CALENDAR date and stays the plain ISO date string it already
            // is; the accompanying `format: "strict_date"` (see FormatFor) is what tells the server
            // to read it as one rather than guessing.
            DateValue d => JsonValue.Create(d.Value),

            // ...while a datetimeValue IS an absolute instant, normalised to UTC. Zone is NEVER
            // emitted: SEM-4's second Must-NOT names ES's own `time_zone` parameter explicitly, and
            // `DatetimeValue.Zone` is provenance recording how the adapter resolved the wire string.
            DatetimeValue dt => JsonValue.Create(NormalizeInstant(dt.Value)),

            // AR-7's nullValue. Bound as a JSON null rather than rewritten into an `exists` check:
            // SEM-2's null handling goes through blank/notBlank, which carry no value at all, so
            // rewriting would be inventing a semantics no spec text or artifact states.
            NullValue => null,

            _ => throw new SearchCompilationException($"Unknown TypedValue type '{value.GetType().Name}'.")
        };
    }

    /// <summary>
    /// SEM-4: the <c>range</c> query's explicit <c>format</c>, pinning the string shape the server
    /// accepts instead of leaving it to the index mapping's own <c>format</c> list — a date field
    /// mapped with a custom format would otherwise reject, or worse silently mis-parse, a boundary
    /// the compiler knows the exact shape of. <see langword="null"/> for every non-date type, where
    /// the parameter has no meaning at all.
    ///
    /// <para>The two names are not interchangeable. <c>strict_date</c> is <c>yyyy-MM-dd</c> and
    /// nothing else, which is exactly what a <c>dateValue</c> is; <c>strict_date_optional_time</c> is
    /// the ISO-8601 instant shape SEM-4 requires a <c>datetimeValue</c> to already be in. Spelling
    /// the latter for a calendar date would accept a time-of-day the IR guarantees is absent, which
    /// is SEM-4's third Must-NOT (never conflate the two) reappearing as a parser setting.</para>
    /// </summary>
    public static string? FormatFor(FieldType type) => type switch
    {
        FieldType.Date => "strict_date",
        FieldType.Datetime => "strict_date_optional_time",
        _ => null
    };

    /// <summary>
    /// SEM-4: a <c>datetime</c> is the absolute instant, normalised to UTC with a trailing <c>Z</c>.
    /// Normalising rather than emitting verbatim is what stops the same instant written
    /// <c>+02:00</c> and written <c>Z</c> from producing two different artifacts.
    ///
    /// <para><b>Load-bearing dependency:</b> this is safe from machine-local-time drift only because
    /// <see cref="SearchQueryBuilder.Build"/> runs <see cref="FilterNodeValidator"/> first, and its
    /// datetime pattern mandates a trailing <c>Z</c> or an explicit offset. Without that guarantee
    /// <see cref="DateTimeOffset.TryParse(string, IFormatProvider, DateTimeStyles, out DateTimeOffset)"/>
    /// would assume the HOST's local zone for a zone-less string, and the same fixture would compile
    /// to a different instant on a different machine. (Deliberately duplicated from
    /// <c>Craticula.Mongo.MongoExtendedJson.NormalizeInstant</c>, which is <see langword="internal"/>
    /// to that assembly: a backend compiler depending on another backend compiler would be a worse
    /// coupling than eleven duplicated lines, and neither may reach into the other.)</para>
    /// </summary>
    public static string NormalizeInstant(string value)
    {
        if (!DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var instant))
        {
            throw new SearchCompilationException(
                $"datetimeValue '{value}' is not an ISO-8601 instant (FilterNodeValidator should have rejected it upstream).");
        }

        var utc = instant.UtcDateTime;
        var format = utc.Ticks % TimeSpan.TicksPerSecond == 0
            ? "yyyy-MM-dd'T'HH:mm:ss'Z'"
            : "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'";

        return utc.ToString(format, CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Re-parses a <see cref="DecimalValue"/>'s exact decimal text as a JSON number token.
    /// <see cref="JsonNode.Parse(string, JsonNodeOptions?, System.Text.Json.JsonDocumentOptions)"/>
    /// keeps the token verbatim, so no binary floating-point value is ever constructed and nothing is
    /// rounded — the point of AR-7 carrying a <c>decimal</c> as a string in the first place.
    /// </summary>
    private static JsonNode ParseNumber(string literal, string fieldName)
    {
        try
        {
            return JsonNode.Parse(literal)
                ?? throw new SearchCompilationException($"decimalValue on field '{fieldName}' parsed to JSON null.");
        }
        catch (System.Text.Json.JsonException exception)
        {
            throw new SearchCompilationException(
                $"decimalValue '{literal}' on field '{fieldName}' is not a JSON number literal " +
                "(FilterNodeValidator should have rejected it upstream).",
                exception);
        }
    }

    /// <summary>
    /// Defense in depth against a caller that skipped SEM-5 coercion, mirroring
    /// <c>MongoExtendedJson.RequireDeclaredType</c>. <see cref="NullValue"/> is accepted against any
    /// declared type, matching <see cref="TypeCoercionValidator"/>'s own rule.
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
            throw new SearchCompilationException(
                $"A '{value.GetType().Name}' cannot be embedded against field '{fieldName}', declared " +
                $"'{field.Type}' — run TypeCoercionValidator (SEM-5) before compiling.");
        }
    }
}
