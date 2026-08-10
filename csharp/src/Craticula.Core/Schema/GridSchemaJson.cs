using System.Text.Json;
using System.Text.Json.Serialization;

namespace Craticula.Core.Schema;

/// <summary>
/// Shared <see cref="JsonSerializerOptions"/> for (de)serializing the <c>Craticula.Core.Schema</c>
/// types against <c>spec/grid-schema.schema.json</c>'s camelCase property/enum-value shape, so no
/// hand-written parsing code is needed anywhere these types are read from JSON.
///
/// Also used to (de)serialize <c>Craticula.Core.Ir</c> types (<see cref="Craticula.Core.Ir.FilterNode"/>,
/// <see cref="Craticula.Core.Ir.TypedValue"/>, <see cref="Craticula.Core.Ir.PageSpec"/>) against
/// <c>spec/ir.schema.json</c> — the same camelCase/fail-closed policy applies, since
/// <c>grid-schema.schema.json</c> itself <c>$ref</c>s into <c>ir.schema.json</c> for
/// <c>staticFilter</c>; kept as one shared options instance rather than a parallel class to avoid
/// duplicating this fail-closed configuration.
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
///   - AllowOutOfOrderMetadataProperties = true: System.Text.Json's polymorphic deserialization
///     (used by Ir.FilterNode/TypedValue/PageSpec) otherwise requires the "type"/"mode"
///     discriminator to be the FIRST JSON property — a requirement JSON Schema (and JSON itself)
///     never imposes, so without this a valid IR document from a port that serializes keys
///     alphabetically (or any other order) would be silently rejected. This is a correctness fix,
///     not a laxness — it does not change what shape is accepted, only which key order is.
///   - MaxDepth = 64, set explicitly rather than left at System.Text.Json's own undocumented
///     default (which happens to also be 64): a coarse, parse-time backstop against a JSON
///     document nested deep enough to threaten the parser itself, independent of and prior to
///     ResourceLimitsValidator's own SEC-3 max-filter-tree-depth check (default 8), which runs on
///     the already-built object graph. The two numbers are not the same budget — this one exists so
///     a pathological JSON document can't exhaust the deserializer before any application-level
///     validator gets a chance to run; SEC-3's tree-depth limit is the actual per-request policy.
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
        AllowOutOfOrderMetadataProperties = true,
        MaxDepth = 64,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase, allowIntegerValues: false) }
    };
}
