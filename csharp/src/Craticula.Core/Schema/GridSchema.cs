using System.Text.Json;
using System.Text.Json.Serialization;
using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>
/// SC-1..SC-5: the host-declared field allowlist and per-field configuration every compilation
/// requires — the security and typing boundary the rest of the compiler depends on.
/// </summary>
public sealed record GridSchema
{
    /// <summary>[DEFAULT] SC-1: Strict throws on an unlisted colId; Lenient silently strips it.</summary>
    public GridSchemaMode Mode { get; init; } = GridSchemaMode.Strict;

    /// <summary>
    /// The physical collection every compiled query reads from: the SQL table or view named in
    /// <c>FROM</c>, the MongoDB collection, the Search index. Host-declared and never
    /// client-influenced, like the per-field <see cref="SqlMapping"/> beside it — no AG Grid request
    /// carries it and no compiler can invent it. Must be non-blank.
    /// </summary>
    public required string Source { get; init; }

    /// <summary>SEM-6: the field appended as a trailing sort tiebreaker whenever the request's own
    /// sort doesn't already end in a unique key. Must itself be a key present in <see cref="Fields"/>.</summary>
    public required string StableKey { get; init; }

    /// <summary>
    /// SC-5: a host-authored IR filter fragment ANDed into every compiled query (the
    /// tenancy/soft-delete hook). The JSON schema $refs into <c>ir.schema.json#/$defs/filterNode</c>,
    /// modeled here as the real <see cref="FilterNode"/> IR AST (#1632).
    /// </summary>
    public FilterNode? StaticFilter { get; init; }

    /// <summary>SC-2: the exhaustive allowlist, keyed by logical colId. Must be non-empty.</summary>
    public required IReadOnlyDictionary<string, FieldConfig> Fields { get; init; }

    /// <summary>
    /// GRP-3: the colIds of <see cref="Fields"/> in the order the schema DECLARES them — the order a
    /// grouping request's drill-down leaf projects its columns in, and therefore the order the
    /// compiled column list is compared byte-for-byte against
    /// (<c>spec/fixtures/group-aggregation/005</c>, <c>006</c>).
    ///
    /// <para><b>Why this is not just <c>Fields.Keys</c>.</b> <see cref="Parse"/> materialises
    /// <see cref="Fields"/> as a plain <see cref="Dictionary{TKey,TValue}"/>, whose enumeration order
    /// .NET explicitly does not specify. An insert-only dictionary happens to enumerate in insertion
    /// order today, which is why the fixtures pass — but that is an implementation detail of the
    /// current runtime, not a contract, and "declaration order" is a normative rule a port must be
    /// able to reproduce. <see cref="Parse"/> therefore reads the order out of the JSON document
    /// itself, where it is unambiguous.</para>
    ///
    /// <para>A <see cref="GridSchema"/> built directly in code (rather than parsed) has no JSON
    /// declaration order to read, so this falls back to <see cref="Fields"/>'s own enumeration —
    /// the only source available, and the same one that was being relied on implicitly before.
    /// <see cref="System.Text.Json.Serialization.JsonIgnoreAttribute"/> keeps it out of the wire
    /// contract entirely: <c>grid-schema.schema.json</c> declares no such property, and
    /// <see cref="GridSchemaJson.Options"/>'s <c>UnmappedMemberHandling.Disallow</c> would otherwise
    /// have to be relaxed for a field no host should ever supply.</para>
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<string> FieldOrder
    {
        get => _fieldOrder ?? [.. Fields.Keys];
        init => _fieldOrder = value;
    }

    private readonly IReadOnlyList<string>? _fieldOrder;

    /// <summary>
    /// Deserializes a <see cref="GridSchema"/> from JSON matching
    /// <c>spec/grid-schema.schema.json</c>, using <see cref="GridSchemaJson.Options"/>, then checks
    /// the invariants the JSON Schema states but neither C#'s type system nor System.Text.Json can
    /// enforce on their own. <see cref="GridSchemaJson.Options"/> already rejects malformed shapes
    /// (unmapped properties, duplicate keys, null where non-nullable, out-of-range enum values) —
    /// the checks here are the ones that survive valid-shaped-but-still-invalid JSON:
    /// <c>fields</c> non-empty (minProperties:1), no null <see cref="FieldConfig"/> entries,
    /// <see cref="Source"/> non-blank (minLength:1),
    /// <see cref="StableKey"/> non-blank (minLength:1) and present in <see cref="Fields"/>,
    /// every <see cref="SqlMapping"/>'s "exactly one of column/qualifiedColumn/expression",
    /// every <see cref="FormatOptions.DatePattern"/> being a pattern a formatter can actually apply
    /// (EXP-5 — see <see cref="FormatOptions.IsValid"/> for why that is checked here rather than when
    /// the first date cell is written), and
    /// (via <see cref="FilterNodeValidator"/>) every structural invariant of a present
    /// <see cref="StaticFilter"/> tree.
    /// </summary>
    public static GridSchema Parse(string json)
    {
        var schema = JsonSerializer.Deserialize<GridSchema>(json, GridSchemaJson.Options)
            ?? throw new JsonException("GridSchema JSON deserialized to null.");

        schema = schema with { FieldOrder = ReadDeclarationOrder(json) };

        FilterNodeValidator.Validate(schema.StaticFilter);

        if (schema.Fields.Count == 0)
        {
            throw new GridSchemaValidationException("GridSchema.fields must declare at least one field.");
        }

        foreach (var (colId, config) in schema.Fields)
        {
            if (config is null)
            {
                throw new GridSchemaValidationException($"GridSchema.fields['{colId}'] must not be null.");
            }

            if (config.Physical?.Sql is { IsValid: false })
            {
                throw new GridSchemaValidationException(
                    $"GridSchema.fields['{colId}'].physical.sql must set exactly one of column, qualifiedColumn, or expression.");
            }

            if (config.Format is { IsValid: false } format)
            {
                throw new GridSchemaValidationException(
                    $"GridSchema.fields['{colId}'].format.datePattern '{format.DatePattern}' cannot format a " +
                    "date value (EXP-5). It must be a non-blank pattern in the LDML field letters every port " +
                    "shares (yyyy, MM, dd, HH, mm, ss and literal separators), e.g. 'dd/MM/yyyy'. Rejected here " +
                    "rather than at the first date cell, which a streaming export cannot recover from.");
            }
        }

        if (string.IsNullOrWhiteSpace(schema.Source))
        {
            throw new GridSchemaValidationException("GridSchema.source must not be blank.");
        }

        if (string.IsNullOrWhiteSpace(schema.StableKey))
        {
            throw new GridSchemaValidationException("GridSchema.stableKey must not be blank.");
        }

        if (!schema.Fields.ContainsKey(schema.StableKey))
        {
            throw new GridSchemaValidationException(
                $"GridSchema.stableKey '{schema.StableKey}' must itself be a key present in fields.");
        }

        return schema;
    }

    /// <summary>
    /// GRP-3: the <c>fields</c> object's property names in document order, which is the one place
    /// "declaration order" is actually stated. A second pass over JSON that
    /// <see cref="JsonSerializer.Deserialize{TValue}(string, JsonSerializerOptions)"/> has already
    /// accepted, so nothing here needs to re-report a malformed document — but the duplicate-key
    /// setting is repeated rather than left at its permissive default, so this parse can never
    /// disagree with <see cref="GridSchemaJson.Options"/> about how many properties there are.
    /// </summary>
    private static IReadOnlyList<string> ReadDeclarationOrder(string json)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowDuplicateProperties = false });

        var order = new List<string>();
        foreach (var field in document.RootElement.GetProperty("fields").EnumerateObject())
        {
            order.Add(field.Name);
        }

        return order;
    }
}
