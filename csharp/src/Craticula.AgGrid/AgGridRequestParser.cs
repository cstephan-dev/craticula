using System.Text.Json;
using Craticula.Core.Ir;

namespace Craticula.AgGrid;

/// <summary>
/// AR-2.1/AG-1: parses an AG Grid SSRM <c>IServerSideGetRowsRequest</c> (see
/// <c>spec/fixtures/README.md</c>'s wire-shape reference, grounded in the real
/// <c>github/ag-grid</c> TypeScript source) into <see cref="AgGridParsedRequest"/> — the adapter that
/// isolates every other backend/adapter from AG Grid's own wire quirks. Reads the request via
/// <see cref="JsonElement"/> directly rather than a full-document <see cref="JsonSerializer.Deserialize"/>,
/// since the wire shape itself is not one of <c>ir.schema.json</c>'s own shapes (only the
/// <c>customFilters</c>/<c>groupKeys</c> fragments are) — the two exceptions below reuse
/// <see cref="Craticula.Core.Schema.GridSchemaJson.Options"/> for exactly those two fragments instead
/// of hand-parsing them.
///
/// <para><b>The adapter never performs type coercion (SEM-5's job, downstream).</b> Every scalar
/// filter value (text/number <c>filter</c> — even when the wire value is a JSON <em>number</em> — a
/// date condition's <c>dateFrom</c>, a set condition's <c>values</c> elements) is emitted as a
/// <see cref="StringValue"/> verbatim. <c>groupKeys</c> is the one exception: the wire already
/// carries it pre-typed as <c>{type, value}</c> objects (a real frontend adapter resolves each raw
/// wire string using the group column's declared schema type before building the request this
/// parser consumes), so it deserializes directly as <see cref="TypedValue"/>.</para>
///
/// <para><b>Explicitly rejected, never silently mis-parsed (AG-1/AG-5's Definition of Done):</b> a
/// native <c>inRange</c> condition (no inclusivity-flag wire path — use <c>customFilters</c>), an
/// AG Grid date preset or any other unrecognized condition <c>type</c>/<c>filterType</c>/<c>aggFunc</c>
/// (covers AG-2's date presets and, as a side effect of requiring a recognized <c>filterType</c> per
/// entry, an Advanced Filter model root — no dedicated shape check for it exists, since no ground
/// truth is available to verify one against), <c>pivotMode:true</c>/non-empty <c>pivotCols</c>
/// (AG-5, out of scope), and the distinct <c>setValuesRequest</c> envelope (BM-3, not the row-fetch
/// request this parser handles). See <see cref="AgGridRequestParseException"/> for the two reason
/// codes these rejections carry.</para>
/// </summary>
public static class AgGridRequestParser
{
    /// <summary>
    /// Parses <paramref name="requestJson"/> into an <see cref="AgGridParsedRequest"/>. Runs the same
    /// defense-in-depth structural validation every other public IR entry point in this codebase
    /// runs on its own output (<see cref="FilterNodeValidator"/>, <see cref="SortItemValidator"/>,
    /// <see cref="GroupSpecValidator"/>, <see cref="AggregationValidator"/>, <see cref="PageSpecValidator"/>)
    /// before returning, since this is the very first entry point a request passes through.
    /// </summary>
    public static AgGridParsedRequest Parse(string requestJson)
    {
        using var document = ParseDocument(requestJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new AgGridRequestParseException("malformed-request", "The AG Grid SSRM request root must be a JSON object.");
        }

        if (root.TryGetProperty("setValuesRequest", out _))
        {
            throw new AgGridRequestParseException(
                "malformed-request",
                "'setValuesRequest' is BM-3's distinct set-filter-values envelope, not the row-fetch " +
                "request (out of scope for this adapter) — see spec/fixtures/group-aggregation/README.md.");
        }

        if (root.TryGetProperty("pivotMode", out var pivotModeEl) && pivotModeEl.ValueKind == JsonValueKind.True)
        {
            throw new AgGridRequestParseException(
                "malformed-request", "'pivotMode: true' requests are not supported (AG-5 pivot mode is out of scope for this adapter).");
        }

        if (root.TryGetProperty("pivotCols", out var pivotColsEl)
            && pivotColsEl.ValueKind == JsonValueKind.Array
            && pivotColsEl.GetArrayLength() > 0)
        {
            throw new AgGridRequestParseException(
                "malformed-request", "A non-empty 'pivotCols' is not supported (AG-5 pivot mode is out of scope for this adapter).");
        }

        var nativeFilter = ParseFilterModel(root);
        var customFilters = ParseCustomFilters(root);
        var sort = ParseSortModel(root);
        var (group, aggregations) = ParseGroupAndAggregations(root);
        var page = ParsePage(root);

        FilterNodeValidator.Validate(nativeFilter);
        FilterNodeValidator.Validate(customFilters);
        SortItemValidator.Validate(sort);
        GroupSpecValidator.Validate(group);
        AggregationValidator.Validate(aggregations);
        PageSpecValidator.Validate(page);

        return new AgGridParsedRequest
        {
            NativeFilter = nativeFilter,
            CustomFilters = customFilters,
            Sort = sort,
            Group = group,
            Aggregations = aggregations,
            Page = page
        };
    }

    /// <summary>
    /// Parses the request's raw JSON, converting a malformed-JSON <see cref="JsonException"/> (e.g.
    /// duplicate keys, per <c>spec/fixtures/adversarial/021-duplicate-json-keys-deserializer-fuzz</c>'s
    /// own pinned <c>{"rejectedAt":"requestParsing","reason":"malformed-request"}</c> oracle) into the
    /// same typed <see cref="AgGridRequestParseException"/> every other rejection in this file uses —
    /// a raw <see cref="JsonException"/> escaping here would carry no <see cref="AgGridRequestParseException.Reason"/>
    /// a caller could map to that oracle.
    /// </summary>
    private static JsonDocument ParseDocument(string requestJson)
    {
        try
        {
            return JsonDocument.Parse(requestJson, new JsonDocumentOptions { AllowDuplicateProperties = false });
        }
        catch (JsonException ex)
        {
            throw new AgGridRequestParseException("malformed-request", $"The AG Grid SSRM request is not valid JSON: {ex.Message}", ex);
        }
    }

    // ---- filterModel --------------------------------------------------------------------------

    /// <summary>
    /// Builds the native filter tree from <c>filterModel</c> (a map of colId -> per-column entry):
    /// an <see cref="AndNode"/> across columns when more than one column has a filter, unwrapped when
    /// exactly one, <see langword="null"/> when <c>filterModel</c> is absent/JSON-null/empty.
    /// </summary>
    private static FilterNode? ParseFilterModel(JsonElement root)
    {
        if (!root.TryGetProperty("filterModel", out var filterModelEl) || filterModelEl.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (filterModelEl.ValueKind != JsonValueKind.Object)
        {
            throw new AgGridRequestParseException("malformed-request", "'filterModel' must be a JSON object or null.");
        }

        var columnNodes = new List<FilterNode>();
        foreach (var property in filterModelEl.EnumerateObject())
        {
            var field = property.Name;
            var entry = property.Value;

            if (entry.ValueKind != JsonValueKind.Object)
            {
                throw new AgGridRequestParseException(
                    "malformed-request", $"filterModel['{field}'] must be a JSON object, got {entry.ValueKind}.");
            }

            var node = ParseColumnEntry(field, entry);
            if (node is not null)
            {
                columnNodes.Add(node);
            }
        }

        return columnNodes.Count == 0 ? null : Combine(columnNodes, isAnd: true, "filterModel");
    }

    /// <summary>
    /// Dispatches one <c>filterModel[colId]</c> entry. <c>multi</c> is handled here (never inside
    /// <see cref="ParseConditionOrCombined"/>, since AG Grid's real <c>filterModels</c> stack is
    /// itself never nested); everything else is a single condition or a combined form, handled by
    /// <see cref="ParseConditionOrCombined"/>. Returns <see langword="null"/> only when a <c>multi</c>
    /// entry's every slot is inactive (see below) — this column then contributes nothing to the
    /// overall filter, rather than throwing for a legitimately "no active filter on this column" state.
    /// </summary>
    private static FilterNode? ParseColumnEntry(string field, JsonElement entry)
    {
        var filterType = ReadRequiredString(entry, "filterType", field);

        if (filterType != "multi")
        {
            return ParseConditionOrCombined(field, entry);
        }

        if (!entry.TryGetProperty("filterModels", out var modelsEl) || modelsEl.ValueKind != JsonValueKind.Array)
        {
            throw new AgGridRequestParseException(
                "malformed-request", $"Column '{field}': 'multi' filterType requires a 'filterModels' array.");
        }

        // AG Grid's real MultiFilter carries one slot per stacked child filter UI; an inactive child
        // occupies its slot as JSON null (packages/ag-grid-enterprise's multiFilterUtil.ts) — only
        // when EVERY slot is null does the column have no active filter at all. Each active slot is a
        // full child filter model in its own right (it can itself be a combined form), so it's routed
        // through the same ParseConditionOrCombined dispatch as any other filterModel entry — never
        // ParseSingleCondition directly, or a slot using the conditions/condition1 form would 400.
        var nodes = new List<FilterNode>();
        foreach (var model in modelsEl.EnumerateArray())
        {
            if (model.ValueKind == JsonValueKind.Null)
            {
                continue;
            }

            if (model.ValueKind != JsonValueKind.Object)
            {
                throw new AgGridRequestParseException(
                    "malformed-request", $"Column '{field}': a 'multi' filterModels entry must be an object or null, got {model.ValueKind}.");
            }

            var node = ParseConditionOrCombined(field, model);
            if (node is not null)
            {
                nodes.Add(node);
            }
        }

        return nodes.Count == 0 ? null : Combine(nodes, isAnd: true, field);
    }

    /// <summary>
    /// Dispatches one condition-or-combined-form object that is NOT itself a <c>multi</c> wrapper: a
    /// <c>conditions</c> array or legacy <c>condition1</c>/<c>condition2</c> pair combines via the
    /// entry's own <c>operator</c>, otherwise the entry itself is a single bare condition. Shared by
    /// <see cref="ParseFilterModel"/>'s top-level entries and by each active <c>multi</c> slot in
    /// <see cref="ParseColumnEntry"/>, so both dispatch through the identical combination logic.
    /// </summary>
    private static FilterNode ParseConditionOrCombined(string field, JsonElement entry)
    {
        if (entry.TryGetProperty("conditions", out var conditionsEl))
        {
            if (conditionsEl.ValueKind != JsonValueKind.Array)
            {
                throw new AgGridRequestParseException("malformed-request", $"Column '{field}': 'conditions' must be an array.");
            }

            var nodes = new List<FilterNode>();
            foreach (var condition in conditionsEl.EnumerateArray())
            {
                if (condition.ValueKind != JsonValueKind.Object)
                {
                    throw new AgGridRequestParseException(
                        "malformed-request", $"Column '{field}': a 'conditions' entry must be an object, got {condition.ValueKind}.");
                }

                nodes.Add(ParseSingleCondition(field, condition));
            }

            return CombineByOperator(field, entry, nodes);
        }

        if (entry.TryGetProperty("condition1", out var condition1El))
        {
            if (!entry.TryGetProperty("condition2", out var condition2El))
            {
                throw new AgGridRequestParseException(
                    "malformed-request", $"Column '{field}': legacy combined form has 'condition1' but is missing 'condition2'.");
            }

            var nodes = new List<FilterNode>
            {
                ParseSingleCondition(field, condition1El),
                ParseSingleCondition(field, condition2El)
            };

            return CombineByOperator(field, entry, nodes);
        }

        return ParseSingleCondition(field, entry);
    }

    private static FilterNode CombineByOperator(string field, JsonElement entry, IReadOnlyList<FilterNode> nodes)
    {
        var op = ReadRequiredString(entry, "operator", field);
        return op switch
        {
            "AND" => Combine(nodes, isAnd: true, field),
            "OR" => Combine(nodes, isAnd: false, field),
            _ => throw new AgGridRequestParseException(
                "malformed-request", $"Column '{field}': combined filter has an unrecognized 'operator' value '{op}' (expected 'AND' or 'OR').")
        };
    }

    /// <summary>Combines <paramref name="nodes"/> per AR-5: 2+ wraps in <see cref="AndNode"/>/
    /// <see cref="OrNode"/>, exactly 1 is returned unwrapped (no spurious single-child composite,
    /// mirroring <see cref="FilterTreeMerge.And"/>'s own convention), 0 is a malformed request.</summary>
    private static FilterNode Combine(IReadOnlyList<FilterNode> nodes, bool isAnd, string field)
    {
        if (nodes.Count == 0)
        {
            throw new AgGridRequestParseException("malformed-request", $"Column '{field}': a combined filter has no conditions.");
        }

        if (nodes.Count == 1)
        {
            return nodes[0];
        }

        return isAnd ? new AndNode { Nodes = nodes } : new OrNode { Nodes = nodes };
    }

    /// <summary>
    /// Dispatches one self-describing SINGLE condition object (a bare <c>filterModel</c> entry, or one
    /// element of a <c>conditions</c> array / a legacy <c>condition1</c>/<c>condition2</c> slot) by its
    /// own <c>filterType</c> — never <c>"multi"</c> here, since AG Grid never nests a <c>multi</c>
    /// inside another <c>multi</c>'s stack. Reached only via <see cref="ParseConditionOrCombined"/>,
    /// which is itself what handles a <c>multi</c> slot that turns out to BE a combined form (a
    /// <c>multi</c> slot is a full child filter model, not guaranteed to be a bare single condition).
    /// </summary>
    private static FilterNode ParseSingleCondition(string field, JsonElement condition)
    {
        var filterType = ReadRequiredString(condition, "filterType", field);
        return filterType switch
        {
            "text" => ParseTextCondition(field, condition),
            "number" => ParseNumberCondition(field, condition),
            "date" => ParseDateCondition(field, condition),
            "set" => ParseSetCondition(field, condition),
            _ => throw UnknownOperator(field, filterType, type: null)
        };
    }

    // ---- per-filterType single-condition parsing -----------------------------------------------

    private static ComparisonNode ParseTextCondition(string field, JsonElement condition)
    {
        var type = ReadRequiredString(condition, "type", field);
        var op = type switch
        {
            "equals" => ComparisonOperator.Eq,
            "notEqual" => ComparisonOperator.Neq,
            "contains" => ComparisonOperator.Contains,
            "notContains" => ComparisonOperator.NotContains,
            "startsWith" => ComparisonOperator.StartsWith,
            "endsWith" => ComparisonOperator.EndsWith,
            "blank" => ComparisonOperator.Blank,
            "notBlank" => ComparisonOperator.NotBlank,
            _ => throw UnknownOperator(field, "text", type)
        };

        if (op is ComparisonOperator.Blank or ComparisonOperator.NotBlank)
        {
            return new ComparisonNode { Field = field, Op = op };
        }

        return new ComparisonNode { Field = field, Op = op, Value = ReadStringValue(condition, "filter", field, "text") };
    }

    private static ComparisonNode ParseNumberCondition(string field, JsonElement condition)
    {
        var type = ReadRequiredString(condition, "type", field);
        if (type == "inRange")
        {
            throw InRangeNotSupported(field);
        }

        var op = type switch
        {
            "equals" => ComparisonOperator.Eq,
            "notEqual" => ComparisonOperator.Neq,
            "lessThan" => ComparisonOperator.Lt,
            "lessThanOrEqual" => ComparisonOperator.Lte,
            "greaterThan" => ComparisonOperator.Gt,
            "greaterThanOrEqual" => ComparisonOperator.Gte,
            "blank" => ComparisonOperator.Blank,
            "notBlank" => ComparisonOperator.NotBlank,
            _ => throw UnknownOperator(field, "number", type)
        };

        if (op is ComparisonOperator.Blank or ComparisonOperator.NotBlank)
        {
            return new ComparisonNode { Field = field, Op = op };
        }

        return new ComparisonNode { Field = field, Op = op, Value = ReadNumericStringValue(condition, "filter", field) };
    }

    private static ComparisonNode ParseDateCondition(string field, JsonElement condition)
    {
        var type = ReadRequiredString(condition, "type", field);
        if (type == "inRange")
        {
            throw InRangeNotSupported(field);
        }

        var op = type switch
        {
            "equals" => ComparisonOperator.Eq,
            "notEqual" => ComparisonOperator.Neq,
            "lessThan" => ComparisonOperator.Lt,
            "lessThanOrEqual" => ComparisonOperator.Lte,
            "greaterThan" => ComparisonOperator.Gt,
            "greaterThanOrEqual" => ComparisonOperator.Gte,
            "blank" => ComparisonOperator.Blank,
            "notBlank" => ComparisonOperator.NotBlank,

            // Anything else is an AG-2 date preset ("today", "last7Days", ...) — AG-2 is #1636's
            // scope, not this story's; rejecting every unrecognized 'type' here covers it without a
            // dedicated preset-name check.
            _ => throw UnknownOperator(field, "date", type)
        };

        if (op is ComparisonOperator.Blank or ComparisonOperator.NotBlank)
        {
            return new ComparisonNode { Field = field, Op = op };
        }

        return new ComparisonNode { Field = field, Op = op, Value = ReadStringValue(condition, "dateFrom", field, "date") };
    }

    private static ComparisonNode ParseSetCondition(string field, JsonElement condition)
    {
        if (!condition.TryGetProperty("values", out var valuesEl) || valuesEl.ValueKind == JsonValueKind.Null)
        {
            throw new AgGridRequestParseException(
                "malformed-request", $"Column '{field}': a 'set' filter requires a non-null 'values' array.");
        }

        if (valuesEl.ValueKind != JsonValueKind.Array)
        {
            throw new AgGridRequestParseException(
                "malformed-request", $"Column '{field}': a 'set' filter's 'values' must be an array, got {valuesEl.ValueKind}.");
        }

        var values = new List<TypedValue>();
        foreach (var element in valuesEl.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.String)
            {
                // AG Grid's real SetFilterModelValue also allows a null element (the "(Blanks)"
                // pseudo-value) — no fixture in this suite exercises it, and the plan does not ask
                // for it, so it is rejected here rather than silently mis-mapped to some guessed
                // representation.
                throw new AgGridRequestParseException(
                    "malformed-request", $"Column '{field}': a 'set' filter's 'values' entry must be a string, got {element.ValueKind}.");
            }

            values.Add(new StringValue { Value = element.GetString()! });
        }

        return new ComparisonNode { Field = field, Op = ComparisonOperator.In, Values = values };
    }

    private static AgGridRequestParseException InRangeNotSupported(string field) => new(
        "unknown-operator",
        $"Column '{field}': native 'inRange' carries no boundary-inclusivity flags (SEM-4) — use the 'customFilters' extension instead.");

    private static AgGridRequestParseException UnknownOperator(string field, string filterType, string? type) => new(
        "unknown-operator",
        type is null
            ? $"Column '{field}': unrecognized filterType '{filterType}' (AG-1 supports text/number/date/set/multi)."
            : $"Column '{field}': '{filterType}' filterType does not recognize condition type '{type}' (AR-6's closed operator set).");

    // ---- customFilters (OQ-11) -------------------------------------------------------------------

    private static FilterNode? ParseCustomFilters(JsonElement root)
    {
        if (!root.TryGetProperty("customFilters", out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<FilterNode>(el.GetRawText(), Craticula.Core.Schema.GridSchemaJson.Options)
                ?? throw new AgGridRequestParseException("malformed-request", "'customFilters' deserialized to null.");
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            // STJ throws JsonException for an unrecognized 'type' discriminator and
            // NotSupportedException when the discriminator is absent entirely — both mean the same
            // thing here: 'customFilters' doesn't conform to the filterNode wire shape.
            throw new AgGridRequestParseException("malformed-request", $"'customFilters' is not a valid filterNode: {ex.Message}", ex);
        }
    }

    // ---- sortModel ----------------------------------------------------------------------------

    private static IReadOnlyList<SortItem>? ParseSortModel(JsonElement root)
    {
        if (!root.TryGetProperty("sortModel", out var sortModelEl) || sortModelEl.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (sortModelEl.ValueKind != JsonValueKind.Array)
        {
            throw new AgGridRequestParseException("malformed-request", "'sortModel' must be an array.");
        }

        var items = new List<SortItem>();
        foreach (var element in sortModelEl.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new AgGridRequestParseException(
                    "malformed-request", $"A 'sortModel' entry must be an object, got {element.ValueKind}.");
            }

            var colId = ReadRequiredString(element, "colId", "<sortModel>");
            var sortRaw = ReadRequiredString(element, "sort", colId);
            var direction = sortRaw switch
            {
                "asc" => SortDirection.Asc,
                "desc" => SortDirection.Desc,
                _ => throw new AgGridRequestParseException(
                    "malformed-request",
                    $"sortModel entry for colId '{colId}' has an unrecognized 'sort' value '{sortRaw}' (expected 'asc' or 'desc').")
            };

            // SEM-3: nullsPosition is always explicit in the IR, never left to a backend's default —
            // AG Grid's own SortModelItem carries no null-placement signal at all, so the adapter
            // fills it itself via #1634's shared SemanticsResolver rule (never re-derived ad hoc here).
            items.Add(new SortItem
            {
                Field = colId,
                Direction = direction,
                NullsPosition = Craticula.Core.Schema.SemanticsResolver.DefaultNullsPosition(direction)
            });
        }

        return items.Count == 0 ? null : items;
    }

    // ---- rowGroupCols / groupKeys / valueCols --------------------------------------------------

    private readonly record struct ColumnVo(string Id, string Field, string? AggFunc);

    private static (GroupSpec? Group, IReadOnlyList<Aggregation>? Aggregations) ParseGroupAndAggregations(JsonElement root)
    {
        var groupCols = ReadColumnVoList(root, "rowGroupCols");
        var valueCols = ReadColumnVoList(root, "valueCols");
        var groupKeys = ParseGroupKeys(root);

        GroupSpec? group = null;
        if (groupCols.Count > 0)
        {
            group = new GroupSpec
            {
                GroupCols = groupCols.Select(vo => vo.Field).ToList(),
                GroupKeys = groupKeys
            };
        }
        else if (groupKeys.Count > 0)
        {
            // A drill-down key with no group columns to drill into is meaningless — and silently
            // dropping it would WIDEN the query (BM-2: a group key restricts the result set to that
            // bucket), the opposite of AG-1/AG-5's "explicitly rejected, never silently mis-parsed".
            throw new AgGridRequestParseException(
                "malformed-request", "'groupKeys' is non-empty but 'rowGroupCols' is empty — a drill-down key requires a group column to drill into.");
        }

        IReadOnlyList<Aggregation>? aggregations = null;
        if (valueCols.Count > 0)
        {
            aggregations = valueCols.Select(vo =>
            {
                if (vo.AggFunc is null)
                {
                    throw new AgGridRequestParseException(
                        "malformed-request", $"valueCols entry for field '{vo.Field}' is missing 'aggFunc'.");
                }

                return new Aggregation
                {
                    Field = vo.Field,
                    Func = ParseAggFunc(vo.AggFunc, vo.Field),
                    Alias = vo.Id != vo.Field ? vo.Id : null
                };
            }).ToList();
        }

        return (group, aggregations);
    }

    /// <summary>Reads a <c>ColumnVO[]</c> property (<c>rowGroupCols</c>/<c>valueCols</c>), using each
    /// entry's <c>field</c> (never <c>id</c>/<c>displayName</c>) per the plan. Absent/JSON-null is an
    /// empty list, matching how AG Grid always sends <c>[]</c> rather than omitting these arrays.</summary>
    private static IReadOnlyList<ColumnVo> ReadColumnVoList(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var arrayEl) || arrayEl.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (arrayEl.ValueKind != JsonValueKind.Array)
        {
            throw new AgGridRequestParseException("malformed-request", $"'{propertyName}' must be an array.");
        }

        var result = new List<ColumnVo>();
        foreach (var element in arrayEl.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                throw new AgGridRequestParseException(
                    "malformed-request", $"A '{propertyName}' entry must be an object, got {element.ValueKind}.");
            }

            var id = ReadRequiredString(element, "id", propertyName);

            // AG Grid's own ColumnVO declares 'field' optional (a valueGetter-backed column has no
            // plain field path) — 'id' is always present and is what GridSchema's allowlist keys on
            // anyway, so it's the correct fallback rather than rejecting a legitimate request.
            var field = element.TryGetProperty("field", out var fieldEl) && fieldEl.ValueKind == JsonValueKind.String
                ? fieldEl.GetString()!
                : id;

            var aggFunc = element.TryGetProperty("aggFunc", out var aggFuncEl) && aggFuncEl.ValueKind == JsonValueKind.String
                ? aggFuncEl.GetString()
                : null;

            result.Add(new ColumnVo(id, field, aggFunc));
        }

        return result;
    }

    /// <summary>
    /// Deserializes <c>groupKeys</c> directly as <see cref="TypedValue"/> — the wire already carries
    /// each key pre-typed as a <c>{type, value}</c> object (confirmed against the real
    /// <c>spec/fixtures/group-aggregation/*/agGridRequest.json</c> fixtures, not just the fixtures
    /// README's simplified <c>string[]</c> annotation), so no hand-written resolution is needed here,
    /// the same "no hand-written parsing code" rationale <see cref="TypedValue"/>'s own doc gives.
    /// </summary>
    private static IReadOnlyList<TypedValue> ParseGroupKeys(JsonElement root)
    {
        if (!root.TryGetProperty("groupKeys", out var groupKeysEl) || groupKeysEl.ValueKind == JsonValueKind.Null)
        {
            return [];
        }

        if (groupKeysEl.ValueKind != JsonValueKind.Array)
        {
            throw new AgGridRequestParseException("malformed-request", "'groupKeys' must be an array.");
        }

        var keys = new List<TypedValue>();
        foreach (var element in groupKeysEl.EnumerateArray())
        {
            try
            {
                keys.Add(JsonSerializer.Deserialize<TypedValue>(element.GetRawText(), Craticula.Core.Schema.GridSchemaJson.Options)
                    ?? throw new AgGridRequestParseException("malformed-request", "A 'groupKeys' entry deserialized to null."));
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException)
            {
                throw new AgGridRequestParseException("malformed-request", $"A 'groupKeys' entry is not a valid typedValue: {ex.Message}", ex);
            }
        }

        return keys;
    }

    private static AggregationFunc ParseAggFunc(string raw, string field) => raw switch
    {
        "sum" => AggregationFunc.Sum,
        "min" => AggregationFunc.Min,
        "max" => AggregationFunc.Max,
        "avg" => AggregationFunc.Avg,
        "count" => AggregationFunc.Count,
        _ => throw new AgGridRequestParseException(
            "unknown-operator",
            $"valueCols entry for field '{field}' has an unrecognized aggFunc '{raw}' (AR-8's closed v1 function set is sum/min/max/avg/count).")
    };

    // ---- startRow / endRow ---------------------------------------------------------------------

    private static PageSpec ParsePage(JsonElement root)
    {
        var startRow = ReadOptionalInt(root, "startRow");
        var endRow = ReadOptionalInt(root, "endRow");

        return (startRow, endRow) switch
        {
            ({ } s, { } e) => new RangePage { StartRow = s, EndRow = e },
            (null, null) => new NonePage(),
            _ => throw new AgGridRequestParseException(
                "malformed-request",
                $"'startRow'/'endRow' must both be present or both be null (got startRow={Render(startRow)}, endRow={Render(endRow)}).")
        };
    }

    private static string Render(int? value) => value?.ToString() ?? "null";

    private static int? ReadOptionalInt(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var el) || el.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out var value))
        {
            throw new AgGridRequestParseException(
                "malformed-request", $"'{propertyName}' must be a 32-bit integer or null, got '{el.GetRawText()}'.");
        }

        return value;
    }

    // ---- shared JsonElement helpers -------------------------------------------------------------

    private static string ReadRequiredString(JsonElement obj, string propertyName, string field)
    {
        if (!obj.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            throw new AgGridRequestParseException(
                "malformed-request", $"Column '{field}': expected a string '{propertyName}' property.");
        }

        return prop.GetString()!;
    }

    private static StringValue ReadStringValue(JsonElement condition, string propertyName, string field, string filterTypeLabel)
    {
        if (!condition.TryGetProperty(propertyName, out var prop) || prop.ValueKind != JsonValueKind.String)
        {
            var found = condition.TryGetProperty(propertyName, out var actual) ? actual.ValueKind.ToString() : "no property";
            throw new AgGridRequestParseException(
                "malformed-request",
                $"Column '{field}' ('{filterTypeLabel}' filter): expected a string '{propertyName}' value, got {found}.");
        }

        return new StringValue { Value = prop.GetString()! };
    }

    /// <summary>
    /// Reads a <c>number</c> filter's scalar property and carries it forward as a
    /// <see cref="StringValue"/>. Accepts BOTH a JSON number (the well-formed wire shape — carried
    /// via the literal's own raw text, never parsed through a floating type first, so it can't
    /// introduce the float round-tripping loss <see cref="Craticula.Core.Ir.DecimalValue"/>'s own doc
    /// warns against) AND a JSON string: two real conformance fixtures
    /// (<c>spec/fixtures/adversarial/012-non-numeric-string-int64-field</c>,
    /// <c>015-malformed-decimal-string-decimal-field</c>) deliberately send an adversarial STRING
    /// value against a <c>number</c> filterType, expecting rejection at the <c>typeCoercion</c> stage
    /// (<see cref="Craticula.Core.Schema.TypeCoercionValidator"/>, SEM-5) — not here at
    /// <c>requestParsing</c>. Rejecting a string here would 400 at the wrong stage for a request the
    /// conformance suite pins as valid-shaped-but-uncoercible. AR-7 coercion into the field's actual
    /// declared type happens later, downstream — never here, for either representation.
    /// </summary>
    private static StringValue ReadNumericStringValue(JsonElement condition, string propertyName, string field)
    {
        if (!condition.TryGetProperty(propertyName, out var prop))
        {
            throw new AgGridRequestParseException(
                "malformed-request", $"Column '{field}' ('number' filter): expected a numeric or string '{propertyName}' value, got no property.");
        }

        return prop.ValueKind switch
        {
            JsonValueKind.Number => new StringValue { Value = prop.GetRawText() },
            JsonValueKind.String => new StringValue { Value = prop.GetString()! },
            _ => throw new AgGridRequestParseException(
                "malformed-request", $"Column '{field}' ('number' filter): expected a numeric or string '{propertyName}' value, got {prop.ValueKind}.")
        };
    }
}
