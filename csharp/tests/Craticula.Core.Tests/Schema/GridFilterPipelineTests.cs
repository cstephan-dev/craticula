using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Hand-built tests for <see cref="GridFilterPipeline"/> — pins the DoD's actual ask: the linear
/// order <c>customFilters merge -&gt; allowlist -&gt; limits -&gt; coercion -&gt; static injection</c>,
/// each stage's exception surfacing when it is the one that should fire, and the end-to-end happy
/// path producing a fully merged/coerced/statically-injected tree. Each individual stage's own
/// behavior is already exhaustively tested by its own validator's test class; these tests only
/// confirm the wiring and ordering.
/// </summary>
public class GridFilterPipelineTests
{
    private static readonly PageSpec NoPage = new NonePage();
    private static readonly ResourceLimits DefaultLimits = new();

    private static GridSchema MakeSchema(FilterNode? staticFilter = null, GridSchemaMode mode = GridSchemaMode.Strict) => new()
    {
        Mode = mode,
        Source = "orders",
        StableKey = "id",
        StaticFilter = staticFilter,
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.Int64 },
            ["region"] = new() { Type = FieldType.String },
            ["quantity"] = new() { Type = FieldType.Int64 },
        }
    };

    private static ComparisonNode Eq(string field, TypedValue value) => new()
    {
        Field = field,
        Op = ComparisonOperator.Eq,
        Value = value,
    };

    [Fact]
    public void Happy_Path_Merges_Allowlists_Coerces_And_Injects_The_StaticFilter()
    {
        var native = Eq("region", new StringValue { Value = "west" });
        var customFilters = Eq("quantity", new StringValue { Value = "10" }); // needs coercion to Int64Value
        var staticFilter = Eq("id", new Int64Value { Value = 1 });
        var schema = MakeSchema(staticFilter);

        var result = Assert.IsType<AndNode>(GridFilterPipeline.Process(native, customFilters, NoPage, schema, DefaultLimits));

        // Outer AND is the static-injection wrap: [mergedAndCoerced, staticFilter].
        Assert.Equal(2, result.Nodes.Count);
        Assert.Same(staticFilter, result.Nodes[1]);

        var mergedAndCoerced = Assert.IsType<AndNode>(result.Nodes[0]);
        Assert.Same(native, mergedAndCoerced.Nodes[0]);
        var coercedQuantity = Assert.IsType<ComparisonNode>(mergedAndCoerced.Nodes[1]);
        Assert.Equal(10L, Assert.IsType<Int64Value>(coercedQuantity.Value).Value);
    }

    [Fact]
    public void Returns_Null_When_No_Filter_And_No_StaticFilter_Are_Present() =>
        Assert.Null(GridFilterPipeline.Process(null, null, NoPage, MakeSchema(), DefaultLimits));

    [Fact]
    public void Unknown_Field_Throws_UnknownFieldException_Before_Limits_Or_Coercion_Run()
    {
        var customFilters = Eq("secretNotes", new StringValue { Value = "irrelevant" });

        Assert.Throws<UnknownFieldException>(
            () => GridFilterPipeline.Process(null, customFilters, NoPage, MakeSchema(), DefaultLimits));
    }

    [Fact]
    public void Oversized_Tree_Throws_ResourceLimitExceededException_Before_Coercion_Runs()
    {
        // Depth 9 (one beyond the SEC-3 default max of 8) built from allowlisted, correctly-typed
        // leaves — this would pass allowlist and coercion cleanly, proving limits gates first.
        FilterNode deepTree = Eq("id", new Int64Value { Value = 1 });
        for (var i = 1; i < 9; i++)
        {
            deepTree = new NotNode { Node = deepTree };
        }

        Assert.Throws<ResourceLimitExceededException>(
            () => GridFilterPipeline.Process(null, deepTree, NoPage, MakeSchema(), DefaultLimits));
    }

    [Fact]
    public void Uncoercible_Value_Throws_TypeCoercionException()
    {
        var customFilters = Eq("id", new StringValue { Value = "not-a-number" });

        Assert.Throws<TypeCoercionException>(
            () => GridFilterPipeline.Process(null, customFilters, NoPage, MakeSchema(), DefaultLimits));
    }

    [Fact]
    public void Malformed_CustomFilters_Throws_FilterNodeValidationException_Before_Allowlist_Runs()
    {
        var malformed = new ComparisonNode { Field = "id", Op = ComparisonOperator.Eq }; // 'eq' requires 'value'

        Assert.Throws<FilterNodeValidationException>(
            () => GridFilterPipeline.Process(null, malformed, NoPage, MakeSchema(), DefaultLimits));
    }

    [Fact]
    public void Lenient_Mode_Strips_An_Unknown_Field_Instead_Of_Throwing()
    {
        var schema = MakeSchema(mode: GridSchemaMode.Lenient);
        var native = Eq("region", new StringValue { Value = "west" });
        var customFilters = Eq("secretNotes", new StringValue { Value = "irrelevant" });

        var result = Assert.IsType<ComparisonNode>(
            GridFilterPipeline.Process(native, customFilters, NoPage, schema, DefaultLimits));

        // The AND merge degenerates to just the surviving native leaf once the unknown-field
        // customFilters leaf is stripped — proving the stripped field never reaches coercion.
        Assert.Same(native, result);
    }

    [Fact]
    public void Lenient_Mode_Stripping_Every_Field_In_An_And_Yields_No_Filter_At_All()
    {
        var schema = MakeSchema(mode: GridSchemaMode.Lenient);
        var customFilters = Eq("secretNotes", new StringValue { Value = "irrelevant" });

        var result = GridFilterPipeline.Process(null, customFilters, NoPage, schema, DefaultLimits);

        Assert.Null(result);
    }

    // ---- group / aggregation colIds (#1640) ------------------------------------------------------
    //
    // Before #1640 these were the one class of client-controlled colId that never reached this
    // pipeline at all: a host ran it over the filter and handed rowGroupCols/valueCols/groupKeys
    // straight to a compiler. Everything below is the closing of that gap.

    private static GridSchema MakeGroupableSchema(GridSchemaMode mode = GridSchemaMode.Strict) => new()
    {
        Mode = mode,
        Source = "sales",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.Int64 },
            ["region"] = new() { Type = FieldType.String, Groupable = true },
            ["segment"] = new() { Type = FieldType.String, Groupable = true },
            ["fiscalYear"] = new() { Type = FieldType.Int64, Groupable = true },
            ["notes"] = new() { Type = FieldType.String },
            ["amount"] = new() { Type = FieldType.Decimal, Aggregatable = [AggregationFunc.Sum] }
        }
    };

    private static GroupSpec Group(IReadOnlyList<string> cols, params string[] keys) => new()
    {
        GroupCols = cols,
        GroupKeys = [.. keys.Select(key => new StringValue { Value = key })]
    };

    [Fact]
    public void An_Unlisted_Group_Column_Is_Rejected_By_The_Allowlist()
    {
        var ex = Assert.Throws<UnknownFieldException>(() => GridFilterPipeline.Process(
            null, null, NoPage, MakeGroupableSchema(), DefaultLimits, Group(["regionHidden"])));

        Assert.Contains(ex.ColIds, colId => colId.StartsWith("regionHidden", StringComparison.Ordinal));
    }

    [Fact]
    public void An_Unlisted_Aggregation_Field_Is_Rejected_By_The_Allowlist()
    {
        var ex = Assert.Throws<UnknownFieldException>(() => GridFilterPipeline.Process(
            null, null, NoPage, MakeGroupableSchema(), DefaultLimits,
            Group(["region"]),
            [new Aggregation { Field = "secretSalary", Func = AggregationFunc.Sum }]));

        Assert.Contains(ex.ColIds, colId => colId.StartsWith("secretSalary", StringComparison.Ordinal));
    }

    /// <summary>
    /// SC-2's capability flags were enforced nowhere at all before #1640: a schema could be grouped
    /// on a field it declares <c>groupable: false</c>. <c>notes</c> is allowlisted — this is a
    /// capability denial, not an unknown colId.
    /// </summary>
    [Fact]
    public void A_Group_Column_Declared_Not_Groupable_Is_Rejected_Even_Though_It_Is_Allowlisted()
    {
        var schema = MakeGroupableSchema();
        Assert.True(schema.Fields.ContainsKey("notes"));

        var ex = Assert.Throws<UnknownFieldException>(() => GridFilterPipeline.Process(
            null, null, NoPage, schema, DefaultLimits, Group(["notes"])));

        Assert.Contains(ex.ColIds, colId => colId.Contains("groupable:false", StringComparison.Ordinal));
    }

    /// <summary>The same, per FUNCTION: <c>amount</c> declares only <c>sum</c>, so <c>avg</c> on it
    /// is a denial even though the field is aggregatable in general.</summary>
    [Fact]
    public void An_Aggregation_Function_Outside_The_Declared_Set_Is_Rejected()
    {
        var ex = Assert.Throws<UnknownFieldException>(() => GridFilterPipeline.Process(
            null, null, NoPage, MakeGroupableSchema(), DefaultLimits,
            Group(["region"]),
            [new Aggregation { Field = "amount", Func = AggregationFunc.Avg }]));

        Assert.Contains(ex.ColIds, colId => colId.Contains("aggregatable set", StringComparison.Ordinal));

        // ...and the declared one still passes, so the check is the set membership and not a blanket ban.
        GridFilterPipeline.Process(
            null, null, NoPage, MakeGroupableSchema(), DefaultLimits,
            Group(["region"]),
            [new Aggregation { Field = "amount", Func = AggregationFunc.Sum }]);
    }

    /// <summary>
    /// The deliberate asymmetry with the filter path, and the reason for it: Lenient may silently
    /// drop a <em>condition</em> (the result only widens), but dropping a group column changes which
    /// bucket the client asked about — it would answer a different question rather than a broader
    /// one. So Lenient throws here exactly as Strict does, while still stripping the unknown filter
    /// leaf in the very same call.
    /// </summary>
    [Fact]
    public void Lenient_Mode_Does_Not_Strip_An_Unknown_Group_Column_The_Way_It_Strips_A_Filter()
    {
        var schema = MakeGroupableSchema(GridSchemaMode.Lenient);

        // Baseline: the identical unknown colId in a FILTER is silently stripped in this mode.
        Assert.Null(GridFilterPipeline.Process(
            null, Eq("regionHidden", new StringValue { Value = "x" }), NoPage, schema, DefaultLimits));

        Assert.Throws<UnknownFieldException>(() => GridFilterPipeline.Process(
            null, null, NoPage, schema, DefaultLimits, Group(["regionHidden"])));
    }

    /// <summary>
    /// SEC-3's leaf budget now covers the drill-down: N client-supplied <c>groupKeys</c> become N leaf
    /// predicates — an amplification BM-2 introduced, since group state was simply ignored before it.
    /// Three keys against a two-leaf budget must fail on the group keys alone, with no filter present
    /// at all. (<c>ir.schema.json</c>'s <c>groupCols.maxItems</c> is the pipeline-independent floor
    /// under this, not a substitute for it — see <see cref="GroupSpecValidator.MaxGroupCols"/>: it is
    /// fixed, while this budget is host-configurable and can be far tighter.)
    /// </summary>
    [Fact]
    public void The_Drill_Down_Predicates_Are_Counted_Against_The_SEC3_Leaf_Budget()
    {
        var schema = new GridSchema
        {
            Source = "sales",
            StableKey = "id",
            Fields = new Dictionary<string, FieldConfig>
            {
                ["id"] = new() { Type = FieldType.Int64 },
                ["a"] = new() { Type = FieldType.String, Groupable = true },
                ["b"] = new() { Type = FieldType.String, Groupable = true },
                ["c"] = new() { Type = FieldType.String, Groupable = true },
                ["d"] = new() { Type = FieldType.String, Groupable = true }
            }
        };

        var limits = new ResourceLimits { MaxLeaves = 2 };
        var group = Group(["a", "b", "c", "d"], "1", "2", "3");

        var ex = Assert.Throws<ResourceLimitExceededException>(
            () => GridFilterPipeline.Process(null, null, NoPage, schema, limits, group));

        Assert.Contains(ex.Violations, violation => violation.LimitName == "max-leaves-exceeded");

        // Two keys is within budget, so the check is really counting them and not just rejecting
        // any grouped request.
        GridFilterPipeline.Process(null, null, NoPage, schema, limits, Group(["a", "b", "c", "d"], "1", "2"));
    }

    /// <summary>
    /// The budget is shared, not per-half: one drill-down key plus one user filter leaf is two
    /// leaves, which a max of 1 must reject — pinning that the limits check sees the MERGED tree
    /// rather than each side separately.
    /// </summary>
    [Fact]
    public void The_Leaf_Budget_Is_Shared_Between_The_Drill_Down_And_The_User_Filter()
    {
        var schema = MakeGroupableSchema();
        var limits = new ResourceLimits { MaxLeaves = 1 };

        // Either half alone fits.
        GridFilterPipeline.Process(null, null, NoPage, schema, limits, Group(["region", "segment"], "EMEA"));
        GridFilterPipeline.Process(Eq("notes", new StringValue { Value = "x" }), null, NoPage, schema, limits);

        Assert.Throws<ResourceLimitExceededException>(() => GridFilterPipeline.Process(
            Eq("notes", new StringValue { Value = "x" }),
            null,
            NoPage,
            schema,
            limits,
            Group(["region", "segment"], "EMEA")));
    }

    /// <summary>
    /// SEM-5 reaches the drill-down too. <c>groupKeys</c> arrive off the wire pre-typed and the AG
    /// Grid adapter passes them through verbatim (its one documented exception to "the adapter never
    /// coerces"), so before this a mistyped key sailed through parsing, allowlist and SEC-3 and then
    /// hit <c>SqlQueryBuilder</c>'s "run TypeCoercionValidator (SEM-5) before compiling" — a CALLER
    /// precondition failure raised on ordinary client input, where
    /// <c>spec/fixtures/adversarial/012</c>-<c>015</c> pin the identical defect class on the filter
    /// path as a staged client rejection.
    /// </summary>
    [Fact]
    public void A_Mistyped_Group_Key_Is_Rejected_At_The_Coercion_Stage_Not_By_The_Compiler()
    {
        var ex = Assert.Throws<TypeCoercionException>(() => GridFilterPipeline.Process(
            null, null, NoPage, MakeGroupableSchema(), DefaultLimits, Group(["fiscalYear"], "2024")));

        var violation = Assert.Single(ex.Violations);
        Assert.Equal("fiscalYear", violation.Field);
        Assert.Equal(FieldType.Int64, violation.DeclaredType);
        Assert.Equal("uncoercible-value", violation.Reason);
    }

    /// <summary>
    /// The same key sent as the <c>typedValue</c> variant <c>ir.schema.json</c>'s <c>groupSpec</c>
    /// asks for passes — so the check above is about the declared type, not about rejecting drill-down
    /// keys on numeric columns. It is strict rather than parsing on purpose: this method returns the
    /// user's filter alone, so a corrected key would be discarded and the compiler would re-synthesise
    /// the original — see <see cref="TypeCoercionValidator.ValidateDeclaredTypes"/>.
    /// </summary>
    [Fact]
    public void A_Correctly_Typed_Group_Key_Passes_The_Coercion_Stage()
    {
        var group = new GroupSpec
        {
            GroupCols = ["fiscalYear"],
            GroupKeys = [new Int64Value { Value = 2024 }]
        };

        Assert.Null(GridFilterPipeline.Process(null, null, NoPage, MakeGroupableSchema(), DefaultLimits, group));
    }

    /// <summary>
    /// What the pipeline RETURNS is unchanged: the user's filter alone, never the merged tree. The
    /// compiler synthesises the drill-down again itself, because the two halves resolve case
    /// sensitivity by different rules (GRP-2 versus SEM-1) and could not be told apart afterwards.
    /// </summary>
    [Fact]
    public void The_Returned_Tree_Still_Contains_Only_The_Users_Own_Filter()
    {
        var native = Eq("notes", new StringValue { Value = "x" });

        var result = GridFilterPipeline.Process(
            native, null, NoPage, MakeGroupableSchema(), DefaultLimits, Group(["region", "segment"], "EMEA"));

        Assert.Same(native, result);
    }
}
