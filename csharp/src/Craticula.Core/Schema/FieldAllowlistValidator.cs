namespace Craticula.Core.Schema;

/// <summary>
/// SC-1/SEC-1: the field allowlist check every colId reference (filter, sort, group, aggregation)
/// must pass before anything else runs. Implemented purely via <see cref="GridSchema.Fields"/>
/// dictionary lookups — no reflection over <see cref="GridSchema"/>'s own properties, and no
/// "map everything" fallback path of any kind.
/// </summary>
public static class FieldAllowlistValidator
{
    /// <summary>
    /// Checks <paramref name="colIds"/> against <paramref name="schema"/>'s field allowlist.
    /// In <see cref="GridSchemaMode.Strict"/>, throws <see cref="UnknownFieldException"/> naming
    /// every unlisted colId found (all of them, not just the first). In
    /// <see cref="GridSchemaMode.Lenient"/>, returns the allowed/stripped split without throwing.
    /// </summary>
    public static AllowlistResult Validate(GridSchema schema, IEnumerable<string> colIds)
    {
        var allowed = new List<string>();
        var stripped = new List<string>();

        foreach (var colId in colIds)
        {
            if (schema.Fields.ContainsKey(colId))
            {
                allowed.Add(colId);
            }
            else
            {
                stripped.Add(colId);
            }
        }

        // Default-deny: throw on anything other than an explicit Lenient opt-out, rather than
        // only on an exact Strict match. SC-1's [DEFAULT] is Strict — a mode value that is
        // neither Strict nor Lenient (unreachable today now that GridSchemaJson.Options rejects
        // out-of-range enum values, but this is the allowlist's own defense-in-depth) must fail
        // closed, not silently strip.
        if (schema.Mode != GridSchemaMode.Lenient && stripped.Count > 0)
        {
            throw new UnknownFieldException(stripped);
        }

        return new AllowlistResult(allowed, stripped);
    }
}
