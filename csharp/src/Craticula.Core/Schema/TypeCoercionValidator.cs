using System.Globalization;
using System.Text.Json;
using Craticula.Core.Ir;

namespace Craticula.Core.Schema;

/// <summary>
/// SEM-5: coerces every <see cref="TypedValue"/> slot (<c>value</c>/<c>values</c>/<c>from</c>/<c>to</c>)
/// on every <see cref="ComparisonNode"/> in a tree against the field's declared <see cref="FieldType"/>
/// (looked up in <see cref="GridSchema.Fields"/>). A variant that already matches the declared type
/// passes through unchanged; a <see cref="StringValue"/> against a different declared type is parsed
/// into that type's variant (mirrors <c>ir.schema.json</c>'s own decimal/date/datetime shapes, reusing
/// <see cref="FilterNodeValidator"/>'s patterns); a <see cref="NullValue"/> always passes regardless
/// of declared type; any other mismatch, or an unparseable string, is a violation. Runs
/// unconditionally (not <see cref="GridSchemaMode"/>-gated — see <see cref="TypeCoercionException"/>'s
/// own doc comment) and collects every violation before throwing, rather than failing on the first.
///
/// <see cref="ValidateDeclaredTypes"/> is the same rule minus the string-parsing branch, for a tree
/// whose corrected form could never reach a compiler — see its own doc for the one caller and why.
///
/// <see cref="FilterNode"/>/<see cref="ComparisonNode"/> are immutable records, so coercion that
/// actually changes a value rebuilds the tree from that point up via <c>with</c> — but only where a
/// value actually changed, by reference-comparing each slot's coerced result against its original.
/// </summary>
public static class TypeCoercionValidator
{
    /// <summary>
    /// Coerces every comparison in <paramref name="filter"/> against <paramref name="schema"/>'s
    /// declared field types. A <see langword="null"/> <paramref name="filter"/> passes through
    /// unchanged (nothing to coerce). Runs <see cref="FilterNodeValidator.Validate"/> first — this is
    /// a public entry point in its own right (not only reached via <see cref="GridFilterPipeline"/>),
    /// so it makes the same defense-in-depth guarantee as <see cref="ResourceLimitsValidator.Validate"/>
    /// and <see cref="FilterTreeMerge.CustomFilterMerge"/> rather than trusting a well-formed tree by
    /// convention. Assumes every <see cref="ComparisonNode.Field"/> already passed
    /// <see cref="FieldAllowlistValidator"/> (true by construction in <see cref="GridFilterPipeline"/>,
    /// which runs coercion after allowlist) — a field absent from <see cref="GridSchema.Fields"/> is
    /// therefore treated as a precondition violation, not a SEM-5 coercion failure.
    /// </summary>
    public static FilterNode? Coerce(FilterNode? filter, GridSchema schema)
    {
        FilterNodeValidator.Validate(filter);

        if (filter is null)
        {
            return null;
        }

        var violations = new List<TypeCoercionViolation>();
        var coerced = CoerceNode(filter, schema, violations);

        if (violations.Count > 0)
        {
            throw new TypeCoercionException(violations);
        }

        return coerced;
    }

    /// <summary>
    /// SEM-5, without the string-parsing branch: requires every <see cref="TypedValue"/> slot in
    /// <paramref name="filter"/> to ALREADY carry its field's declared variant, reporting anything
    /// else as the same <c>uncoercible-value</c> violation <see cref="Coerce"/> raises. A
    /// <see cref="NullValue"/> passes against any declared type, matching <see cref="Coerce"/>'s own
    /// rule. Nothing is rewritten and nothing is returned — this is a check, not a transformation.
    ///
    /// <para><b>Why a strict variant exists at all, and where it is used.</b> A tree that a caller
    /// cannot receive back cannot be coerced: <see cref="GridFilterPipeline.Process"/> deliberately
    /// returns the user's filter alone, so a corrected value inside the synthesised <c>groupKeys</c>
    /// drill-down (<see cref="DrillDownFilter.Synthesize"/>) would be discarded and the compiler would
    /// still re-synthesise the original — leaving exactly the caller-precondition failure the check is
    /// there to prevent. That is not a loss, because the group path never needed the parsing branch:
    /// <c>ir.schema.json</c>'s <c>groupSpec.groupKeys</c> carries each key <em>pre-typed</em> as a
    /// <c>typedValue</c> precisely because "it is a filter value like any other, so it must be typed
    /// the same way", and the AG Grid adapter passes it through verbatim (the one documented exception
    /// to "the adapter never coerces"). A variant mismatch there is a wrongly-typed request, not an
    /// untyped one, so it is a client rejection at the <c>typeCoercion</c> stage rather than a value to
    /// fix up. A <em>filter</em> value is the opposite case — AG Grid's wire is untyped and the adapter
    /// emits every scalar as a <see cref="StringValue"/> — which is why <see cref="Coerce"/>, not this,
    /// governs it.</para>
    /// </summary>
    public static void ValidateDeclaredTypes(FilterNode? filter, GridSchema schema)
    {
        FilterNodeValidator.Validate(filter);

        if (filter is null)
        {
            return;
        }

        var violations = new List<TypeCoercionViolation>();
        Check(filter, schema, violations);

        if (violations.Count > 0)
        {
            throw new TypeCoercionException(violations);
        }

        static void Check(FilterNode node, GridSchema schema, List<TypeCoercionViolation> violations)
        {
            switch (node)
            {
                case CompositeFilterNode composite:
                    foreach (var child in composite.Nodes)
                    {
                        Check(child, schema, violations);
                    }

                    return;

                case NotNode not:
                    Check(not.Node, schema, violations);
                    return;

                case ComparisonNode comparison:
                {
                    if (!schema.Fields.TryGetValue(comparison.Field, out var fieldConfig))
                    {
                        throw new KeyNotFoundException(
                            $"TypeCoercionValidator assumes '{comparison.Field}' already passed FieldAllowlistValidator " +
                            "(coercion runs after allowlist in GridFilterPipeline) — it is not present in GridSchema.Fields.");
                    }

                    foreach (var value in Slots(comparison))
                    {
                        if (value is not NullValue && !Matches(value, fieldConfig.Type))
                        {
                            violations.Add(new TypeCoercionViolation(
                                comparison.Field, fieldConfig.Type, Describe(value), Reason: "uncoercible-value"));
                        }
                    }

                    return;
                }

                default:
                    throw new NotSupportedException($"Unknown FilterNode type '{node.GetType()}'.");
            }
        }

        static IEnumerable<TypedValue> Slots(ComparisonNode node)
        {
            if (node.Value is not null)
            {
                yield return node.Value;
            }

            if (node.From is not null)
            {
                yield return node.From;
            }

            if (node.To is not null)
            {
                yield return node.To;
            }

            foreach (var value in node.Values ?? [])
            {
                yield return value;
            }
        }
    }

    private static FilterNode CoerceNode(FilterNode node, GridSchema schema, List<TypeCoercionViolation> violations)
    {
        switch (node)
        {
            case CompositeFilterNode composite:
            {
                List<FilterNode>? newNodes = null;
                for (var i = 0; i < composite.Nodes.Count; i++)
                {
                    var child = composite.Nodes[i];
                    var coercedChild = CoerceNode(child, schema, violations);
                    if (newNodes is null && !ReferenceEquals(coercedChild, child))
                    {
                        newNodes = new List<FilterNode>(composite.Nodes);
                    }

                    if (newNodes is not null)
                    {
                        newNodes[i] = coercedChild;
                    }
                }

                return newNodes is null ? composite : composite with { Nodes = newNodes };
            }

            case NotNode not:
            {
                var coercedInner = CoerceNode(not.Node, schema, violations);
                return ReferenceEquals(coercedInner, not.Node) ? not : not with { Node = coercedInner };
            }

            case ComparisonNode comparison:
                return CoerceComparison(comparison, schema, violations);

            default:
                throw new NotSupportedException($"Unknown FilterNode type '{node.GetType()}'.");
        }
    }

    private static ComparisonNode CoerceComparison(ComparisonNode node, GridSchema schema, List<TypeCoercionViolation> violations)
    {
        if (!schema.Fields.TryGetValue(node.Field, out var fieldConfig))
        {
            throw new KeyNotFoundException(
                $"TypeCoercionValidator assumes '{node.Field}' already passed FieldAllowlistValidator " +
                "(coercion runs after allowlist in GridFilterPipeline) — it is not present in GridSchema.Fields.");
        }

        var declaredType = fieldConfig.Type;

        var value = node.Value is null ? null : CoerceValue(node.Value, declaredType, node.Field, violations);
        var from = node.From is null ? null : CoerceValue(node.From, declaredType, node.Field, violations);
        var to = node.To is null ? null : CoerceValue(node.To, declaredType, node.Field, violations);
        var values = CoerceValues(node.Values, declaredType, node.Field, violations);

        var changed = !ReferenceEquals(value, node.Value)
            || !ReferenceEquals(from, node.From)
            || !ReferenceEquals(to, node.To)
            || !ReferenceEquals(values, node.Values);

        return changed ? node with { Value = value, From = from, To = to, Values = values } : node;
    }

    private static IReadOnlyList<TypedValue>? CoerceValues(
        IReadOnlyList<TypedValue>? values, FieldType declaredType, string field, List<TypeCoercionViolation> violations)
    {
        if (values is null)
        {
            return null;
        }

        List<TypedValue>? newValues = null;
        for (var i = 0; i < values.Count; i++)
        {
            var original = values[i];
            var coerced = CoerceValue(original, declaredType, field, violations);
            if (newValues is null && !ReferenceEquals(coerced, original))
            {
                newValues = new List<TypedValue>(values);
            }

            if (newValues is not null)
            {
                newValues[i] = coerced;
            }
        }

        return newValues ?? values;
    }

    /// <summary>
    /// Coerces one <see cref="TypedValue"/> slot: already matching <paramref name="declaredType"/> or
    /// a <see cref="NullValue"/> pass through unchanged; a <see cref="StringValue"/> against a
    /// different declared type is parsed (appending a violation on failure); any other mismatch is a
    /// violation outright. Returns the original <paramref name="value"/> on violation — the result is
    /// never used once <see cref="Coerce"/> throws, but keeps this a total function.
    /// </summary>
    private static TypedValue CoerceValue(TypedValue value, FieldType declaredType, string field, List<TypeCoercionViolation> violations)
    {
        if (value is NullValue || Matches(value, declaredType))
        {
            return value;
        }

        if (value is StringValue stringValue)
        {
            var parsed = TryParse(stringValue.Value, declaredType);
            if (parsed is not null)
            {
                return parsed;
            }
        }

        violations.Add(new TypeCoercionViolation(field, declaredType, Describe(value), Reason: "uncoercible-value"));
        return value;
    }

    private static bool Matches(TypedValue value, FieldType declaredType) => (value, declaredType) switch
    {
        (StringValue, FieldType.String) => true,
        (Int64Value, FieldType.Int64) => true,
        (DecimalValue, FieldType.Decimal) => true,
        (DoubleValue, FieldType.Double) => true,
        (BooleanValue, FieldType.Boolean) => true,
        (DateValue, FieldType.Date) => true,
        (DatetimeValue, FieldType.Datetime) => true,
        _ => false
    };

    private static TypedValue? TryParse(string raw, FieldType declaredType) => declaredType switch
    {
        FieldType.Int64 => long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var int64Value)
            ? new Int64Value { Value = int64Value }
            : null,

        // AR-7: decimal is carried as a numeric string, never a JSON number — reparsing it into a
        // C# floating type would reintroduce the round-tripping loss AR-7 exists to avoid, so the
        // "parse" here is exactly FilterNodeValidator's own shape check (pattern AND the same
        // maxLength:128 cap ir.schema.json's decimalValue enforces — a syntactically-valid but
        // 500-digit string must be rejected here exactly as it would be if sent as a decimalValue
        // directly, not smuggled in as a coercible stringValue), not a numeric conversion.
        FieldType.Decimal => FilterNodeValidator.DecimalPattern.IsMatch(raw) && raw.Length <= 128
            ? new DecimalValue { Value = raw }
            : null,

        // NumberStyles.Float alone accepts "NaN"/"Infinity"/"-Infinity" and silently overflows a
        // huge exponent (e.g. "1e400") to +/-Infinity rather than failing — none of which
        // ir.schema.json's doubleValue ("type": "number") can represent, and none of which a
        // compiler can encode as a real comparison value, so double.IsFinite is required on top of
        // TryParse succeeding.
        FieldType.Double => double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue)
            && double.IsFinite(doubleValue)
            ? new DoubleValue { Value = doubleValue }
            : null,

        FieldType.Boolean => bool.TryParse(raw, out var boolValue)
            ? new BooleanValue { Value = boolValue }
            : null,

        FieldType.Date => TryParseDate(raw),
        FieldType.Datetime => TryParseDatetime(raw),

        // No reverse coercion into a string: SEM-5 only ever parses a StringValue INTO another
        // type, never turns a non-string value into a string representation of itself.
        _ => null
    };

    /// <summary>
    /// A <see cref="DateValue"/> requires both <c>ir.schema.json</c>'s <c>YYYY-MM-DD</c> shape (the
    /// regex) AND an actual valid calendar date — the regex alone accepts a syntactically-shaped but
    /// semantically-impossible string like <c>"2026-13-45"</c> (month 13, day 45), which
    /// <c>spec/fixtures/adversarial/013-malformed-date-string-date-field</c> pins as a rejection.
    /// </summary>
    private static DateValue? TryParseDate(string raw) =>
        FilterNodeValidator.DatePattern.IsMatch(raw) &&
        DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            ? new DateValue { Value = raw }
            : null;

    /// <summary>
    /// A <see cref="DatetimeValue"/> requires both the ISO-8601-instant shape (the regex) AND an
    /// actual valid instant, for the same calendar-validity reason as <see cref="TryParseDate"/>.
    /// <see cref="DatetimeValue.Zone"/> defaults to <c>"UTC"</c>: per SEM-4, <c>zone</c> is
    /// provenance metadata only (which zone resolved the original wire string into this absolute
    /// instant) and is never read by a compiler — a raw wire string coerced here carries no zone
    /// provenance of its own to record, so <c>"UTC"</c> is a defensible, always-valid placeholder
    /// rather than a semantics-affecting choice.
    /// </summary>
    private static DatetimeValue? TryParseDatetime(string raw) =>
        FilterNodeValidator.IsIsoInstant(raw)
            ? new DatetimeValue { Value = raw, Zone = "UTC" }
            : null;

    // Truncated (via FilterNodeValidator.Truncate) exactly like that file's own error messages do:
    // this text is client-facing (surfaced through TypeCoercionException, typically mapped to a
    // 400-class response), and the violation count is bounded only by SEC-3's MaxLeaves * MaxInValues
    // (up to 64,000 by default) — an untruncated stringValue (up to 8192 chars each) would let a
    // single oversized-but-otherwise-legal request balloon the rejection message accordingly.
    private static string Describe(TypedValue value) => value switch
    {
        StringValue s => $"stringValue('{FilterNodeValidator.Truncate(s.Value)}')",
        Int64Value i => $"int64Value({i.Value.ToString(CultureInfo.InvariantCulture)})",
        DecimalValue d => $"decimalValue('{FilterNodeValidator.Truncate(d.Value)}')",
        DoubleValue d => $"doubleValue({d.Value.ToString(CultureInfo.InvariantCulture)})",
        BooleanValue b => $"booleanValue({(b.Value ? "true" : "false")})",
        DateValue d => $"dateValue('{FilterNodeValidator.Truncate(d.Value)}')",
        DatetimeValue d => $"datetimeValue('{FilterNodeValidator.Truncate(d.Value)}')",
        NullValue => "nullValue",
        _ => value.GetType().Name
    };

    internal static string JsonTag(FieldType type) => JsonNamingPolicy.CamelCase.ConvertName(type.ToString());
}
