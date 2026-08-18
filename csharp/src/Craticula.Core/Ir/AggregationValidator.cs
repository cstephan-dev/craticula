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
/// alias-as-injection-vector across every compiler — and no two aggregations resolve to the same
/// GRP-4 <see cref="Aggregation.OutputName"/>. Mirrors <see cref="SortItemValidator"/>'s
/// collect-all-then-throw pattern exactly.
///
/// <para><b>Why output-name uniqueness is checked HERE rather than in each compiler (#1886).</b>
/// Two of the three compilers already refused it, for the same reason and in nearly the same words:
/// a Mongo <c>$group</c> stage and an Elasticsearch <c>aggs</c> container are both JSON objects, so a
/// repeated key is <em>last-wins</em> rather than an error and one accumulator vanishes from a
/// syntactically perfect query. <c>Craticula.Sql</c> had no such guard at all: it emits
/// <c>SELECT SUM("x") AS "n", AVG("x") AS "n"</c>, which every engine accepts, and the grid then
/// reads whichever column the driver hands back first. The condition is a property of the
/// <em>request</em> — AGG-2 derives the name from <c>valueCols[].id</c>, which is client-supplied —
/// so it belongs in the shared validator every entry point already runs, where all four backends
/// (the EF Core adapter included) inherit it at once. The two per-compiler guards are deliberately
/// KEPT as defence in depth for a host that builds IR directly; they are no longer reachable from an
/// AG Grid request.</para>
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

        // Keyed on the GRP-4 output name, holding the indices that produced it — so the message names
        // WHICH two valueCols collided rather than only that something did. Ordinal on purpose: the
        // name lands in query text as an identifier, and no backend here folds identifier case.
        var byOutputName = new Dictionary<string, List<int>>(StringComparer.Ordinal);

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

            if (!byOutputName.TryGetValue(aggregation.OutputName, out var indices))
            {
                byOutputName[aggregation.OutputName] = indices = [];
            }

            indices.Add(i);
        }

        // Ordered by the first colliding index rather than by the dictionary's own enumeration, which
        // .NET does not specify: the message lists every violation, so its ORDER is part of what a
        // caller sees, and an unspecified one makes the same request produce two different strings.
        foreach (var (outputName, indices) in byOutputName
                     .Where(entry => entry.Value.Count > 1)
                     .OrderBy(entry => entry.Value[0]))
        {
            errors.Add(
                $"aggregations[{string.Join(", ", indices)}] all resolve to the output name " +
                $"'{FilterNodeValidator.Truncate(outputName)}' (GRP-4: the alias when set, else " +
                "<field>_<func>). One output column cannot carry two aggregates — a $group stage and " +
                "an ES aggs container are documents, so the later one silently replaces the earlier, " +
                "and a SQL SELECT list simply names the column twice.");
        }

        if (errors.Count > 0)
        {
            throw new AggregationValidationException(errors);
        }
    }
}
