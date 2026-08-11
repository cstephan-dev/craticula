namespace Craticula.Core.Ir;

/// <summary>
/// Enforces the structural invariants of <c>spec/ir.schema.json</c>'s <c>$defs/sortItem</c> and its
/// containing <c>sort</c> array that the type system alone cannot express: the array, when present,
/// must be non-empty (<c>minItems:1</c> — "absent is the only encoding of no sort", per
/// <c>spec/fixtures/sort-paging/README.md</c>), and every item's <see cref="SortItem.Field"/> must be
/// a non-blank string within <c>fieldName</c>'s 256-character bound. Mirrors
/// <see cref="FilterNodeValidator"/>'s collect-all-then-throw pattern exactly.
/// </summary>
public static class SortItemValidator
{
    /// <summary>
    /// Validates <paramref name="sort"/> against <c>ir.schema.json</c>'s structural invariants. A
    /// <see langword="null"/> <paramref name="sort"/> (no sort at all) is valid — there is nothing to
    /// check. Throws <see cref="SortItemValidationException"/> naming every violation found, not just
    /// the first.
    /// </summary>
    public static void Validate(IReadOnlyList<SortItem>? sort)
    {
        if (sort is null)
        {
            return;
        }

        var errors = new List<string>();

        if (sort.Count == 0)
        {
            errors.Add("sort array must not be empty when present (ir.schema.json sort.minItems:1) — omit it entirely to express no sort.");
        }

        for (var i = 0; i < sort.Count; i++)
        {
            var item = sort[i];
            if (item is null)
            {
                errors.Add($"sort[{i}] must not be null.");
                continue;
            }

            if (string.IsNullOrEmpty(item.Field))
            {
                errors.Add($"sort[{i}].field must not be blank.");
            }
            else if (item.Field.Length > 256)
            {
                errors.Add($"sort[{i}].field '{item.Field[..32]}...' exceeds the 256-character limit (ir.schema.json fieldName.maxLength).");
            }
        }

        if (errors.Count > 0)
        {
            throw new SortItemValidationException(errors);
        }
    }
}
