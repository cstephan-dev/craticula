using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>
/// SC-5: ANDs a fully-processed (merged, allowlisted, limit-checked, coerced) filter tree with
/// <see cref="GridSchema.StaticFilter"/> — the tenancy/soft-delete hook every compiled query must
/// carry. Runs last in <see cref="GridFilterPipeline"/>, after coercion, per
/// <c>docs/architecture/c4-components-core-ir.md</c>'s own note that the static fragment is
/// host-authored — it is never itself subject to allowlist, resource-limit, or coercion checks.
/// <see cref="Inject"/> does re-run <see cref="FilterNodeValidator.Validate"/> on
/// <see cref="GridSchema.StaticFilter"/> as a defense-in-depth check: <see cref="GridSchema.Parse"/>
/// already validates it once when a schema is deserialized from JSON, but <see cref="GridSchema"/>
/// is a plain record a caller can also construct directly (as this file's own tests do), bypassing
/// <c>Parse</c> entirely — this is this class's own version of the same "don't trust a well-formed
/// tree by convention" precedent already applied in <see cref="ResourceLimitsValidator.Validate"/>
/// and <see cref="TypeCoercionValidator.Coerce"/>.
/// </summary>
public static class StaticFilterInjector
{
    public static FilterNode? Inject(FilterNode? filter, GridSchema schema)
    {
        FilterNodeValidator.Validate(schema.StaticFilter);
        return FilterTreeMerge.And(filter, schema.StaticFilter);
    }
}
