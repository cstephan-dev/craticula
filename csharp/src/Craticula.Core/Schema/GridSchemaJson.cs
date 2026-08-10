using System.Text.Json;
using System.Text.Json.Serialization;

namespace Craticula.Core.Schema;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for (de)serializing the <c>Craticula.Core.Schema</c>
/// types against <c>spec/grid-schema.schema.json</c>'s camelCase property/enum-value shape, so no
/// hand-written parsing code is needed anywhere these types are read from JSON.
///
/// GridSchema is the security/typing boundary (SC-1, SEC-1) — these options fail CLOSED to match
/// the meta-schema's own strictness, since a lax deserializer that silently accepts what the
/// schema forbids reopens exactly the hole the schema exists to close:
///   - UnmappedMemberHandling.Disallow: the meta-schema sets additionalProperties:false in 7
///     places; a typo'd property name (e.g. "filterible") must fail loudly, not silently keep
///     the permissive default for the property that was actually meant.
///   - AllowDuplicateProperties = false: a duplicated JSON key must be rejected, not resolved
///     last-wins — see spec/fixtures/adversarial/021-duplicate-json-keys-deserializer-fuzz.
///   - RespectNullableAnnotations = true: "fields": null / "stableKey": null must fail at parse
///     time with a clear JsonException, not produce a null non-nullable property that NREs far
///     from the cause.
///   - JsonStringEnumConverter(allowIntegerValues: false): the meta-schema pins exact lowercase
///     string enum values (e.g. "strict"/"lenient"); a bare integer carries no defined meaning
///     against that set and must not silently coerce into an arbitrary enum member. Note this
///     converter's read path is inherently case-insensitive on the string form ("STRICT" resolves
///     to Strict) — there is no public option to change that short of a fully custom converter;
///     accepted, since a mis-cased-but-recognizable string is a convenience, not the kind of
///     undefined-input hole an integer value is.
/// Verified against all 158 spec/fixtures/**/schema.json — every one still deserializes cleanly
/// with these options; the strictness only rejects documents the meta-schema already forbids.
/// </summary>
public static class GridSchemaJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
}
