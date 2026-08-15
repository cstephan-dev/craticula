namespace Craticula.Core.Ir;

/// <summary>
/// Enforces the structural invariants of <c>spec/ir.schema.json</c>'s <c>$defs/groupSpec</c> that
/// C#'s type system alone cannot express: <see cref="GroupSpec.GroupCols"/> must be non-empty
/// (<c>minItems:1</c>) and every entry must be a non-blank <c>fieldName</c> within its 256-character
/// bound — mirrors <see cref="SortItemValidator"/>'s per-name field-length-bound pattern exactly, and
/// <see cref="GroupSpec.GroupKeys"/>'s elements must not be null. Mirrors
/// <see cref="FilterNodeValidator"/>/<see cref="SortItemValidator"/>'s collect-all-then-throw pattern.
///
/// <para><b>And the arity cap</b> — <c>groupCols.length &lt;= <see cref="MaxGroupCols"/></c>, which
/// bounds <c>groupKeys</c> transitively through GRP-1 below. See <see cref="MaxGroupCols"/> for why a
/// structural cap belongs here rather than in <see cref="Craticula.Core.Schema.ResourceLimits"/>.</para>
///
/// <para><b>GRP-1's arity rule lives here too</b> — <c>groupKeys.length &lt;= groupCols.length</c> —
/// even though it is the one invariant in this file that <c>ir.schema.json</c> does not state, for
/// the simple reason that JSON Schema cannot compare two arrays' lengths. GRP-1 states it as a
/// normative property of the <em>request</em>, so this validator is its only correct home: every
/// backend compiler already runs it, and leaving the check in one of them would make the Mongo and
/// Search compilers re-derive it independently. The failure it prevents is silent, not loud — a
/// compiler that consumed only as many keys as fit would return a <em>different bucket's</em> rows
/// under the label the client asked for.</para>
/// </summary>
public static class GroupSpecValidator
{
    /// <summary>
    /// The structural cap on <see cref="GroupSpec.GroupCols"/>, mirroring <c>ir.schema.json</c>'s own
    /// <c>groupSpec.groupCols.maxItems</c> — and, through GRP-1, on <see cref="GroupSpec.GroupKeys"/>
    /// too.
    ///
    /// <para><b>Why a fixed number here and not a <see cref="Craticula.Core.Schema.ResourceLimits"/>
    /// entry.</b> This is a structural <em>amplification</em> cap, the same register as
    /// <c>fieldName.maxLength: 256</c> and <c>stringValue.value.maxLength: 8192</c> — deliberately not
    /// one of SEC-3's named, host-configurable limits, which <c>ir.schema.json</c>'s root description
    /// says must never be encoded as a schema bound because they have to vary per host. It exists
    /// because <see cref="GroupSpec"/> is as client-controlled as a filter tree and every path that
    /// reaches a compiler must be closed, not only the one through
    /// <see cref="Craticula.Core.Schema.GridFilterPipeline"/>: a host may hand a <see cref="GroupSpec"/>
    /// straight to a compiler, and then nothing at all would bound N.</para>
    ///
    /// <para><b>Why 64.</b> It is SEC-3's own default <c>maxLeaves</c>, and the two measure the same
    /// thing here: GRP-5 compiles each consumed key into exactly one leaf predicate, and GRP-1 bounds
    /// <c>groupKeys</c> by <c>groupCols</c> — so a <see cref="GroupSpec"/> that passes this cap can
    /// never, on its own, exceed the default leaf budget. A host that configured a <em>smaller</em>
    /// <see cref="Craticula.Core.Schema.ResourceLimits.MaxLeaves"/> still gets its tighter answer from
    /// <see cref="Craticula.Core.Schema.ResourceLimitsValidator"/> wherever that runs; this is the
    /// floor under it, not a replacement for it. (A grid with 64 row-group columns is already far past
    /// anything a UI can display — the cap is there to bound amplification, not to constrain a
    /// legitimate request.)</para>
    /// </summary>
    public const int MaxGroupCols = 64;

    /// <summary>
    /// Validates <paramref name="group"/> against <c>ir.schema.json</c>'s structural invariants. A
    /// <see langword="null"/> <paramref name="group"/> (the request is not grouped at all) is valid —
    /// there is nothing to check. Throws <see cref="GroupSpecValidationException"/> naming every
    /// violation found, not just the first.
    /// </summary>
    public static void Validate(GroupSpec? group)
    {
        if (group is null)
        {
            return;
        }

        var errors = new List<string>();

        if (group.GroupCols is null || group.GroupCols.Count == 0)
        {
            errors.Add("groupSpec.groupCols must not be empty (ir.schema.json groupCols.minItems:1).");
        }
        else
        {
            if (group.GroupCols.Count > MaxGroupCols)
            {
                errors.Add(
                    $"groupSpec.groupCols declares {group.GroupCols.Count} column(s), exceeding the structural " +
                    $"limit of {MaxGroupCols} (ir.schema.json groupCols.maxItems).");
            }

            for (var i = 0; i < group.GroupCols.Count; i++)
            {
                var col = group.GroupCols[i];
                if (string.IsNullOrEmpty(col))
                {
                    errors.Add($"groupSpec.groupCols[{i}] must not be blank.");
                }
                else if (col.Length > 256)
                {
                    errors.Add($"groupSpec.groupCols[{i}] '{col[..32]}...' exceeds the 256-character limit (ir.schema.json fieldName.maxLength).");
                }
            }
        }

        if (group.GroupKeys is null)
        {
            errors.Add("groupSpec.groupKeys must not be null (ir.schema.json requires the array, possibly empty).");
        }
        else
        {
            for (var i = 0; i < group.GroupKeys.Count; i++)
            {
                if (group.GroupKeys[i] is null)
                {
                    errors.Add($"groupSpec.groupKeys[{i}] must not be null.");
                }
            }
        }

        // GRP-1: only checked when both arrays are themselves well-formed — "5 keys against 0
        // columns" would otherwise be reported on top of "groupCols must not be empty", which is the
        // same defect said twice.
        if (group.GroupCols is { Count: > 0 } cols && group.GroupKeys is { } keys && keys.Count > cols.Count)
        {
            errors.Add(
                $"groupSpec.groupKeys consumes {keys.Count} key(s) but groupCols declares only {cols.Count} " +
                "column(s) (GRP-1) — BM-2's drill-down consumes groupCols in order, so there is no level for the surplus key(s).");
        }

        if (errors.Count > 0)
        {
            throw new GroupSpecValidationException(errors);
        }
    }
}
