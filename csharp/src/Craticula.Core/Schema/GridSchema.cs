using System.Text.Json;

namespace Craticula.Core.Schema;

/// <summary>
/// SC-1..SC-5: the host-declared field allowlist and per-field configuration every compilation
/// requires — the security and typing boundary the rest of the compiler depends on.
/// </summary>
public sealed record GridSchema
{
    /// <summary>[DEFAULT] SC-1: Strict throws on an unlisted colId; Lenient silently strips it.</summary>
    public GridSchemaMode Mode { get; init; } = GridSchemaMode.Strict;

    /// <summary>SEM-6: the field appended as a trailing sort tiebreaker whenever the request's own
    /// sort doesn't already end in a unique key. Must itself be a key present in <see cref="Fields"/>.</summary>
    public required string StableKey { get; init; }

    /// <summary>
    /// SC-5: a host-authored IR filter fragment ANDed into every compiled query (the
    /// tenancy/soft-delete hook). The JSON schema $refs into <c>ir.schema.json#/$defs/filterNode</c>,
    /// but the C# IR AST does not exist yet (see #1631's scope decision 4) — represented as a raw
    /// <see cref="JsonElement"/> placeholder until a story that walks the IR needs a typed model.
    /// </summary>
    public JsonElement? StaticFilter { get; init; }

    /// <summary>SC-2: the exhaustive allowlist, keyed by logical colId. Must be non-empty.</summary>
    public required IReadOnlyDictionary<string, FieldConfig> Fields { get; init; }

    /// <summary>
    /// Deserializes a <see cref="GridSchema"/> from JSON matching
    /// <c>spec/grid-schema.schema.json</c>, using <see cref="GridSchemaJson.Options"/>, then checks
    /// the invariants the JSON Schema states but neither C#'s type system nor System.Text.Json can
    /// enforce on their own. <see cref="GridSchemaJson.Options"/> already rejects malformed shapes
    /// (unmapped properties, duplicate keys, null where non-nullable, out-of-range enum values) —
    /// the checks here are the ones that survive valid-shaped-but-still-invalid JSON:
    /// <c>fields</c> non-empty (minProperties:1), no null <see cref="FieldConfig"/> entries,
    /// <see cref="StableKey"/> non-blank (minLength:1) and present in <see cref="Fields"/>, and
    /// every <see cref="SqlMapping"/>'s "exactly one of column/qualifiedColumn/expression".
    /// </summary>
    public static GridSchema Parse(string json)
    {
        var schema = JsonSerializer.Deserialize<GridSchema>(json, GridSchemaJson.Options)
            ?? throw new JsonException("GridSchema JSON deserialized to null.");

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
}
