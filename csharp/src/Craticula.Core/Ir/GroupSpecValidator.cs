namespace Craticula.Core.Ir;

/// <summary>
/// Enforces the structural invariants of <c>spec/ir.schema.json</c>'s <c>$defs/groupSpec</c> that
/// C#'s type system alone cannot express: <see cref="GroupSpec.GroupCols"/> must be non-empty
/// (<c>minItems:1</c>) and every entry must be a non-blank <c>fieldName</c> within its 256-character
/// bound — mirrors <see cref="SortItemValidator"/>'s per-name field-length-bound pattern exactly, and
/// <see cref="GroupSpec.GroupKeys"/>'s elements must not be null. Mirrors
/// <see cref="FilterNodeValidator"/>/<see cref="SortItemValidator"/>'s collect-all-then-throw pattern.
/// </summary>
public static class GroupSpecValidator
{
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

        if (errors.Count > 0)
        {
            throw new GroupSpecValidationException(errors);
        }
    }
}
