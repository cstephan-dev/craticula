namespace Craticula.Core.Ir;

/// <summary>
/// Enforces the structural invariants of <c>spec/ir.schema.json</c>'s <c>$defs/setValuesSpec</c> that
/// C#'s type system alone cannot express — <see cref="SetValuesSpec.ColId"/> must be a non-blank
/// <c>fieldName</c> within its 256-character bound, <see cref="SetValuesSpec.SearchPrefix"/> (when
/// present) a non-blank string within <c>stringValue</c>'s own 8192-character bound, and
/// <see cref="SetValuesSpec.Limit"/> (when present) positive. Mirrors
/// <see cref="GroupSpecValidator"/>'s collect-all-then-throw shape exactly.
/// </summary>
public static class SetValuesSpecValidator
{
    /// <summary>
    /// Validates <paramref name="setValues"/>. A <see langword="null"/> spec (the request is not a
    /// distinct-values query at all) is valid — there is nothing to check. Throws
    /// <see cref="SetValuesSpecValidationException"/> naming every violation found, not just the first.
    /// </summary>
    public static void Validate(SetValuesSpec? setValues)
    {
        if (setValues is null)
        {
            return;
        }

        var errors = new List<string>();

        if (string.IsNullOrEmpty(setValues.ColId))
        {
            errors.Add("setValuesSpec.colId must not be blank.");
        }
        else if (setValues.ColId.Length > 256)
        {
            errors.Add($"setValuesSpec.colId '{setValues.ColId[..32]}...' exceeds the 256-character limit (ir.schema.json fieldName.maxLength).");
        }

        if (setValues.SearchPrefix is { } prefix)
        {
            // An EMPTY prefix is rejected rather than silently read as "no prefix": it would compile
            // to a predicate matching every row, i.e. a clause with no effect but real cost, and the
            // request has an unambiguous way to say "the search box is empty" — omit the property.
            // Same reasoning as groupCols' blank check: a structurally meaningless value is a
            // malformed request, not something to normalise on the client's behalf.
            if (prefix.Length == 0)
            {
                errors.Add("setValuesSpec.searchPrefix must not be blank — omit the property when the set filter's search box is empty.");
            }
            else if (prefix.Length > 8192)
            {
                errors.Add(
                    $"setValuesSpec.searchPrefix is {prefix.Length} characters, exceeding the 8192-character limit " +
                    "(ir.schema.json stringValue.value.maxLength — a searchPrefix compiles into exactly one).");
            }
        }

        // A non-positive cap is not "no cap": it is a request that can never return a value. SEC-3's
        // own ResourceLimits rejects the same shape for the same reason, so the two ends of the
        // resolution in ResourceLimitsValidator.ResolveSetValuesLimit agree on what a limit may be.
        if (setValues.Limit is { } limit && limit <= 0)
        {
            errors.Add($"setValuesSpec.limit is {limit}, but a requested cap must be a positive integer (ir.schema.json setValuesSpec.limit.minimum).");
        }

        if (errors.Count > 0)
        {
            throw new SetValuesSpecValidationException(errors);
        }
    }
}
