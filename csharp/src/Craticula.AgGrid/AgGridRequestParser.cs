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
/// <para><b>AG-2 date presets ARE resolved here (#1636), not rejected.</b> A <c>date</c> condition
/// whose <c>type</c> is one of AG Grid's 22 presets (<c>today</c>, <c>last7Days</c>, ...) is turned
/// into an absolute half-open <c>inRange</c> against an injected clock + zone by
/// <see cref="DatePresetResolver"/> — see <see cref="AgGridParseOptions"/> for the injection points
/// and that resolver's own doc for the resolution table and its SEM-4 rationale. A compiler therefore
/// never sees an unresolved preset. Everything else in the <c>type</c> slot that isn't in AR-6's
/// closed operator set is still rejected exactly as before.</para>
///
/// <para><b>Explicitly rejected, never silently mis-parsed (AG-1/AG-5's Definition of Done):</b> a
/// native <c>inRange</c> condition (no inclusivity-flag wire path — use <c>customFilters</c>), any
/// unrecognized condition <c>type</c>/<c>filterType</c>/<c>aggFunc</c> (as a side effect of requiring
/// a recognized <c>filterType</c> per entry, this also covers an Advanced Filter model root — no
/// dedicated shape check for it exists, since no ground truth is available to verify one against), a
/// date preset the request gives the adapter no way to type (no <see cref="AgGridParseOptions.Schema"/>,
/// or a field that is neither <c>date</c> nor <c>datetime</c>), an unresolvable
/// <c>requestContext.zone</c>, <c>pivotMode:true</c>/non-empty <c>pivotCols</c> (AG-5, out of scope),
/// and the distinct <c>setValuesRequest</c> envelope (BM-3, not the row-fetch request this parser
/// handles). See <see cref="AgGridRequestParseException"/> for the two reason codes these rejections
/// carry.</para>
/// </summary>
public static class AgGridRequestParser
{
    /// <summary>
    /// Parses <paramref name="requestJson"/> into an <see cref="AgGridParsedRequest"/>. Runs the same
    /// defense-in-depth structural validation every other public IR entry point in this codebase
    /// runs on its own output (<see cref="FilterNodeValidator"/>, <see cref="SortItemValidator"/>,
    /// <see cref="GroupSpecValidator"/>, <see cref="AggregationValidator"/>, <see cref="PageSpecValidator"/>)
    /// before returning, since this is the very first entry point a request passes through.
    ///
    /// <para><paramref name="options"/> (AG-2, #1636) supplies the clock/zone/week-start/schema a date
    /// preset needs. It is optional: a request carrying no preset never reads any of it, so every
    /// pre-existing call site keeps its exact previous behavior.</para>
    /// </summary>
    public static AgGridParsedRequest Parse(string requestJson, AgGridParseOptions? options = null)
    {
        options ??= AgGridParseOptions.Default;

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

        var nativeFilter = ParseFilterModel(root, DateResolutionContext.From(root, options));
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

    // ---- requestContext / date-preset resolution context (AG-2) ---------------------------------

    /// <summary>
    /// Everything <see cref="ParseDateCondition"/> needs to resolve an AG-2 date preset, threaded
    /// through the <c>filterModel</c> dispatch chain as one parameter rather than four. Built once per
    /// request by <see cref="From"/>.
    /// </summary>
    private readonly record struct DateResolutionContext(AgGridParseOptions Options, TimeZoneInfo Zone, DateTimeOffset Now)
    {
        /// <summary>
        /// Resolves the request's effective zone and "now".
        ///
        /// <para><b>Zone</b>: the request's own <c>requestContext.zone</c> (the documented fixture-suite
        /// extension — see <c>spec/fixtures/README.md</c>) wins over
        /// <see cref="AgGridParseOptions.DefaultZone"/>, and an id that does not resolve is a clean
        /// rejection, never a silent fallback (a fallback would quietly answer with a different day's
        /// rows than the grid shows). Resolved EAGERLY, for every request rather than only for requests
        /// carrying a preset: a request that declares a zone the server cannot resolve is malformed
        /// whether or not this particular filter happens to depend on it, and failing at the same point
        /// every time is far easier to diagnose than failing only on some filter shapes.</para>
        ///
        /// <para><b>Now</b>: always <see cref="AgGridParseOptions.Clock"/>, never the wire. The fixture
        /// suite pins a <c>requestContext.now</c> alongside <c>zone</c> so preset cases are reproducible
        /// (see <c>spec/fixtures/date-presets/README.md</c>), but that is an instruction to the
        /// conformance harness about what to set its injected clock to — deliberately NOT a field this
        /// parser reads. Honoring it would let any client dictate what "last 7 days" means, turning a
        /// server-side, auditable clock into a client-supplied parameter for no gain: a real frontend
        /// has no reason to send one.</para>
        /// </summary>
        public static DateResolutionContext From(JsonElement root, AgGridParseOptions options) =>
            new(options, ResolveZone(root, options), options.Clock.GetUtcNow());

        private static TimeZoneInfo ResolveZone(JsonElement root, AgGridParseOptions options)
        {
            if (!root.TryGetProperty("requestContext", out var contextEl) || contextEl.ValueKind == JsonValueKind.Null)
            {
                return options.DefaultZone;
            }

            if (contextEl.ValueKind != JsonValueKind.Object)
            {
                throw new AgGridRequestParseException(
                    "malformed-request", $"'requestContext' must be a JSON object or null, got {contextEl.ValueKind}.");
            }

            if (!contextEl.TryGetProperty("zone", out var zoneEl) || zoneEl.ValueKind == JsonValueKind.Null)
            {
                return options.DefaultZone;
            }

            if (zoneEl.ValueKind != JsonValueKind.String)
            {
                throw new AgGridRequestParseException(
                    "malformed-request", $"'requestContext.zone' must be a string IANA time zone id, got {zoneEl.ValueKind}.");
            }

            var zoneId = zoneEl.GetString()!;

            // Shape- and denylist-check BEFORE resolving, because TimeZoneInfo.FindSystemTimeZoneById
            // accepts considerably more than IANA ids and two classes of accepted-but-wrong input are
            // reachable straight from the wire:
            //
            //   * a Windows id ("Romance Standard Time") resolves on .NET 6+/Linux and keeps its
            //     Windows spelling in TimeZoneInfo.Id, which would then be minted verbatim into
            //     DatetimeValue.Zone and fail FilterNodeValidator's IANA-shape check — turning a
            //     malformed *request* into a structural-invariant failure with no Reason a host could
            //     map onto rejection.schema.json's requestParsing stage (SEC-6 wants one clean,
            //     reason-coded rejection);
            //   * the tzdata artifacts 'localtime', 'Factory' and 'posixrules' resolve AND satisfy the
            //     IANA shape, so the shape check alone cannot catch them. 'localtime' is the worse of
            //     the three: it resolves to whatever zone the SERVER process is configured for, which
            //     would let a client silently make every preset boundary resolve in the server's zone
            //     — exactly the "must NOT assume a fixed server or database default timezone" that
            //     SEM-4 forbids, and undetectable downstream since the id itself looks well-formed.
            if (!FilterNodeValidator.IsIanaZoneIdShape(zoneId) || NonZoneTzdataArtifacts.Contains(zoneId))
            {
                throw new AgGridRequestParseException(
                    "malformed-request",
                    $"'requestContext.zone' must be an IANA time zone id such as 'UTC' or 'Europe/Paris', got '{zoneId}'.");
            }

            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(zoneId);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw new AgGridRequestParseException(
                    "malformed-request",
                    $"'requestContext.zone' names a time zone id this host cannot resolve: '{zoneId}' " +
                    $"(expected an IANA id such as 'UTC' or 'Europe/Paris').",
                    ex);
            }
        }

        /// <summary>Entries in a tzdata install that resolve and look IANA-shaped but are not zones:
        /// <c>localtime</c> (the server's own configured zone), <c>Factory</c> (the deliberately-unset
        /// placeholder) and <c>posixrules</c> (a legacy POSIX-TZ artifact). Compared ordinally and
        /// case-insensitively, since a case variant would resolve on a case-insensitive filesystem.</summary>
        private static readonly HashSet<string> NonZoneTzdataArtifacts =
            new(["localtime", "Factory", "posixrules"], StringComparer.OrdinalIgnoreCase);
    }

    // ---- filterModel --------------------------------------------------------------------------

    /// <summary>
    /// Builds the native filter tree from <c>filterModel</c> (a map of colId -> per-column entry):
    /// an <see cref="AndNode"/> across columns when more than one column has a filter, unwrapped when
    /// exactly one, <see langword="null"/> when <c>filterModel</c> is absent/JSON-null/empty.
    /// </summary>
    private static FilterNode? ParseFilterModel(JsonElement root, DateResolutionContext context)
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

            var node = ParseColumnEntry(field, entry, context);
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
    private static FilterNode? ParseColumnEntry(string field, JsonElement entry, DateResolutionContext context)
    {
        var filterType = ReadRequiredString(entry, "filterType", field);

        if (filterType != "multi")
        {
            return ParseConditionOrCombined(field, entry, context);
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

            var node = ParseConditionOrCombined(field, model, context);
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
    private static FilterNode ParseConditionOrCombined(string field, JsonElement entry, DateResolutionContext context)
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

                nodes.Add(ParseSingleCondition(field, condition, context));
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
                ParseSingleCondition(field, condition1El, context),
                ParseSingleCondition(field, condition2El, context)
            };

            return CombineByOperator(field, entry, nodes);
        }

        return ParseSingleCondition(field, entry, context);
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
    private static FilterNode ParseSingleCondition(string field, JsonElement condition, DateResolutionContext context)
    {
        var filterType = ReadRequiredString(condition, "filterType", field);
        return filterType switch
        {
            "text" => ParseTextCondition(field, condition),
            "number" => ParseNumberCondition(field, condition),
            "date" => ParseDateCondition(field, condition, context),
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

    private static ComparisonNode ParseDateCondition(string field, JsonElement condition, DateResolutionContext context)
    {
        var type = ReadRequiredString(condition, "type", field);
        if (type == "inRange")
        {
            throw InRangeNotSupported(field);
        }

        // AG-2 (#1636): a preset arrives in this very same 'type' slot, with dateFrom/dateTo both
        // absent (AG Grid's zeroInputTypes). Checked BEFORE the operator switch so the switch's
        // default arm keeps its original meaning — a genuinely unknown condition type — and still
        // rejects it exactly as it did before presets were supported.
        if (DatePresetResolver.TryResolve(type, context.Now, context.Zone, context.Options.FirstDayOfWeek, out var range))
        {
            return ResolveDatePreset(field, type, range, context);
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
            _ => throw UnknownOperator(field, "date", type)
        };

        if (op is ComparisonOperator.Blank or ComparisonOperator.NotBlank)
        {
            return new ComparisonNode { Field = field, Op = op };
        }

        return new ComparisonNode { Field = field, Op = op, Value = ReadStringValue(condition, "dateFrom", field, "date") };
    }

    /// <summary>
    /// AG-2: turns an already-resolved preset <paramref name="range"/> into the half-open
    /// <c>inRange</c> node AG Grid's own spec table describes — <c>fromInclusive: true</c>,
    /// <c>toInclusive: false</c>, both flags explicit on the node per SEM-4. These two values are NOT
    /// a guessed AG Grid default (which is precisely what SEM-4 and <c>spec/fixtures/README.md</c>
    /// forbid, and why a NATIVE <c>inRange</c> is still rejected a few lines above): they are read
    /// from AG Grid's published <c>[from, to)</c> preset spec — see
    /// <see cref="DatePresetResolver"/>'s doc comment for the full distinction before considering
    /// "fixing" this into a rejection.
    ///
    /// <para>The bounds' typed representation comes from the field's schema-declared type
    /// (<see cref="DatePresetResolver.ToTypedBounds"/>), so both rejections below are about the
    /// adapter having no honest way to type a value it is GENERATING — not about it declining to
    /// coerce a client value, which remains this parser's rule everywhere else.</para>
    /// </summary>
    private static ComparisonNode ResolveDatePreset(
        string field, string preset, DatePresetRange range, DateResolutionContext context)
    {
        if (context.Options.Schema is not { } schema)
        {
            throw new AgGridRequestParseException(
                "malformed-request",
                $"Column '{field}': the date preset '{preset}' cannot be resolved because no GridSchema was supplied — " +
                $"pass AgGridParseOptions.Schema so the field's declared type (date vs datetime) can select the " +
                $"resolved bounds' typedValue representation (SEM-4).");
        }

        if (!schema.Fields.TryGetValue(field, out var fieldConfig))
        {
            // Deliberately NOT an SC-1 allowlist decision (that stays FieldAllowlistValidator's job,
            // downstream, where a Lenient schema can still strip the node instead of throwing): this
            // is the narrower statement that a preset the adapter must TYPE cannot be typed at all
            // when the field it targets is undeclared. Silently emitting a StringValue instead would
            // reintroduce SEM-4's forbidden local-date-to-instant promotion one layer down.
            throw new AgGridRequestParseException(
                "malformed-request",
                $"Column '{field}': the date preset '{preset}' targets a field the GridSchema does not declare, " +
                $"so its resolved bounds cannot be typed (SEM-4 requires the field's declared type).");
        }

        try
        {
            var (from, to) = DatePresetResolver.ToTypedBounds(range, fieldConfig.Type, context.Zone);

            return new ComparisonNode
            {
                Field = field,
                Op = ComparisonOperator.InRange,
                From = from,
                To = to,
                FromInclusive = true,
                ToInclusive = false
            };
        }
        catch (ArgumentOutOfRangeException ex)
        {
            throw new AgGridRequestParseException(
                "malformed-request",
                $"Column '{field}': the date preset '{preset}' targets a field declared as " +
                $"'{fieldConfig.Type}' — a preset only resolves against a 'date' or 'datetime' field.",
                ex);
        }
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
