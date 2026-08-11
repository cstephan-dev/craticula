using System.Text.RegularExpressions;

namespace Craticula.Core.Ir;

/// <summary>
/// Enforces the structural invariants of <c>spec/ir.schema.json</c>'s <c>aggregations</c> array and
/// <c>$defs/aggregation</c> that C#'s type system alone cannot express: the array, when present, must
/// be non-empty (<c>minItems:1</c> — "absent is the only encoding of no aggregations", the same
/// canonical-form reason <c>sort</c> gets from <see cref="SortItemValidator"/>), every
/// <see cref="Aggregation.Field"/> must be a non-blank <c>fieldName</c> within its 256-character
/// bound, and a present <see cref="Aggregation.Alias"/> must match <c>ir.schema.json</c>'s
/// <c>aggregation.alias</c> pattern exactly (<c>^[A-Za-z_][A-Za-z0-9_]*$</c>, 1-64 characters) — an
/// alias lands in generated query text as an identifier, so this is the only guard against
/// alias-as-injection-vector across every compiler. Mirrors <see cref="SortItemValidator"/>'s
/// collect-all-then-throw pattern exactly.
/// </summary>
public static class AggregationValidator
{
    // Copied verbatim from spec/ir.schema.json's $defs/aggregation.alias pattern.
    private static readonly Regex AliasPattern = new(@"^[A-Za-z_][A-Za-z0-9_]*$");

    /// <summary>
    /// Validates <paramref name="aggregations"/> against <c>ir.schema.json</c>'s structural
    /// invariants. A <see langword="null"/> <paramref name="aggregations"/> (no aggregations at all)
    /// is valid — there is nothing to check. Throws <see cref="AggregationValidationException"/>
    /// naming every violation found, not just the first.
    /// </summary>
    public static void Validate(IReadOnlyList<Aggregation>? aggregations)
    {
        if (aggregations is null)
        {
            return;
        }

        var errors = new List<string>();

        if (aggregations.Count == 0)
        {
            errors.Add("aggregations array must not be empty when present (ir.schema.json aggregations.minItems:1) — omit it entirely to express no aggregations.");
        }

        for (var i = 0; i < aggregations.Count; i++)
        {
            var aggregation = aggregations[i];
            if (aggregation is null)
            {
                errors.Add($"aggregations[{i}] must not be null.");
                continue;
            }

            if (string.IsNullOrEmpty(aggregation.Field))
            {
                errors.Add($"aggregations[{i}].field must not be blank.");
            }
            else if (aggregation.Field.Length > 256)
            {
                errors.Add($"aggregations[{i}].field '{aggregation.Field[..32]}...' exceeds the 256-character limit (ir.schema.json fieldName.maxLength).");
            }

            if (aggregation.Alias is not null &&
                (aggregation.Alias.Length is 0 or > 64 || !AliasPattern.IsMatch(aggregation.Alias)))
            {
                errors.Add($"aggregations[{i}].alias '{FilterNodeValidator.Truncate(aggregation.Alias)}' does not match ir.schema.json's identifier pattern (^[A-Za-z_][A-Za-z0-9_]*$) or its 1-64 character length bound.");
            }
        }

        if (errors.Count > 0)
        {
            throw new AggregationValidationException(errors);
        }
    }
}
