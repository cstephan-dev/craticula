using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

public class FieldAllowlistValidatorTests
{
    private static ComparisonNode Eq(string field) => new()
    {
        Field = field,
        Op = ComparisonOperator.Eq,
        Value = new Int64Value { Value = 1 },
    };

    private static GridSchema MakeSchema(GridSchemaMode mode) => new()
    {
        Mode = mode,
        Source = "items",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.Int64 },
            ["name"] = new() { Type = FieldType.String }
        }
    };

    /// <summary>#1872: a schema whose <c>costBasis</c> field IS allowlisted but declared
    /// <c>filterable: false</c> — distinct from an unlisted colId, which <see cref="MakeSchema"/>'s
    /// two fields alone already cover. <c>costBasis</c> is also NOT <c>groupable</c> (the default),
    /// which #1907's "both violations" test below relies on.</summary>
    private static GridSchema MakeSchemaWithNonFilterableField(GridSchemaMode mode) => new()
    {
        Mode = mode,
        Source = "items",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.Int64 },
            ["name"] = new() { Type = FieldType.String },
            ["costBasis"] = new() { Type = FieldType.Decimal, Filterable = false }
        }
    };

    /// <summary>#1907: a schema whose <c>region</c> field is declared BOTH <c>groupable: true</c> AND
    /// <c>filterable: false</c> — the combination that lets a request group by it while still
    /// forbidding a client from drilling into one specific value of it.</summary>
    private static GridSchema MakeSchemaWithGroupableNonFilterableField(GridSchemaMode mode) => new()
    {
        Mode = mode,
        Source = "items",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.Int64 },
            ["name"] = new() { Type = FieldType.String },
            ["region"] = new() { Type = FieldType.String, Groupable = true, Filterable = false }
        }
    };

    [Fact]
    public void Strict_DoesNotThrow_WhenAllColIdsAreListed()
    {
        var schema = MakeSchema(GridSchemaMode.Strict);

        var result = FieldAllowlistValidator.Validate(schema, ["id", "name"]);

        Assert.Equal(["id", "name"], result.Allowed);
        Assert.Empty(result.Stripped);
    }

    [Fact]
    public void Strict_Throws_UnknownFieldException_ForAnUnlistedColId()
    {
        var schema = MakeSchema(GridSchemaMode.Strict);

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, ["id", "secretNotes"]));

        Assert.Equal(["secretNotes"], ex.ColIds);
    }

    [Fact]
    public void Strict_CollectsEveryUnlistedColId_NotJustTheFirst()
    {
        var schema = MakeSchema(GridSchemaMode.Strict);

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, ["secretNotes", "id", "internalRank"]));

        Assert.Equal(["secretNotes", "internalRank"], ex.ColIds);
    }

    // ---- #1872: SC-2's filterable flag, enforced on the same filter-tree path ------------------------

    /// <summary>A non-filterable colId is treated exactly like an unknown one for Strict/Lenient
    /// purposes (see <see cref="FieldAllowlistValidator.Validate"/>'s own doc for why) -- Strict
    /// rejects it, naming WHY it differs from a genuinely unknown colId.</summary>
    [Fact]
    public void Strict_Throws_UnknownFieldException_ForANonFilterableColId()
    {
        var schema = MakeSchemaWithNonFilterableField(GridSchemaMode.Strict);

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, ["id", "costBasis"]));

        var violation = Assert.Single(ex.Violations);
        Assert.Equal("costBasis", violation.ColId);
        Assert.Equal("field-not-filterable", violation.Reason);
    }

    /// <summary>The two rejection reasons stay distinguishable when BOTH occur in one request: a
    /// genuinely unknown colId keeps its bare, unchanged wording (matching
    /// <see cref="Strict_CollectsEveryUnlistedColId_NotJustTheFirst"/>'s own exact-string
    /// assertion), while the non-filterable one is suffixed with why.</summary>
    [Fact]
    public void Strict_CollectsBothUnknownAndNonFilterableColIds_WithDistinctReasons()
    {
        var schema = MakeSchemaWithNonFilterableField(GridSchemaMode.Strict);

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.Validate(schema, ["secretNotes", "id", "costBasis"]));

        // Distinguishable by DisplayText (pre-existing string-based contract, unchanged) AND by the
        // machine-readable Reason (#1872's own addition) — not just one or the other.
        Assert.Equal(2, ex.ColIds.Count);
        Assert.Contains("secretNotes", ex.ColIds);
        Assert.Contains("costBasis (declared filterable:false)", ex.ColIds);

        Assert.Equal(2, ex.Violations.Count);
        Assert.Equal("unknown-field", ex.Violations.Single(v => v.ColId == "secretNotes").Reason);
        Assert.Equal("field-not-filterable", ex.Violations.Single(v => v.ColId == "costBasis").Reason);
    }

    /// <summary>Lenient strips a non-filterable colId the SAME way it strips an unknown one --
    /// dropping a condition only widens the result, the trade Lenient mode already exists to let a
    /// host accept (contrast <see cref="FieldAllowlistValidator.ValidateStructuralColumns"/>, where
    /// the identical flag is NOT Lenient-strippable for <c>setValuesSpec.colId</c>).</summary>
    [Fact]
    public void Lenient_StripsNonFilterableColIds_AndKeepsFilterableOnes_WithoutThrowing()
    {
        var schema = MakeSchemaWithNonFilterableField(GridSchemaMode.Lenient);

        var result = FieldAllowlistValidator.Validate(schema, ["id", "costBasis", "name"]);

        Assert.Equal(["id", "name"], result.Allowed);
        // Bare colId, not decorated -- Strip() matches on this exact string.
        Assert.Equal(["costBasis"], result.Stripped);
    }

    [Fact]
    public void Lenient_StripsUnknownColIds_AndKeepsKnownOnes_WithoutThrowing()
    {
        var schema = MakeSchema(GridSchemaMode.Lenient);

        var result = FieldAllowlistValidator.Validate(schema, ["id", "secretNotes", "name"]);

        Assert.Equal(["id", "name"], result.Allowed);
        Assert.Equal(["secretNotes"], result.Stripped);
    }

    [Fact]
    public void Strip_ReturnsTheTreeUnchanged_WhenNothingIsStripped()
    {
        var tree = Eq("id");

        Assert.Same(tree, FieldAllowlistValidator.Strip(tree, []));
    }

    [Fact]
    public void Strip_RemovesTheLeafReferencingAStrippedField_FromAnAndsNodeList()
    {
        var kept = Eq("id");
        var tree = new AndNode { Nodes = [kept, Eq("secretNotes")] };

        var result = Assert.IsType<ComparisonNode>(FieldAllowlistValidator.Strip(tree, ["secretNotes"]));

        // Down to a single surviving child: unwrapped, not left as a spurious 1-child And.
        Assert.Same(kept, result);
    }

    [Fact]
    public void Strip_CollapsesAnAndNodeToNull_WhenEveryChildIsStripped()
    {
        var tree = new AndNode { Nodes = [Eq("secretNotes"), Eq("internalRank")] };

        Assert.Null(FieldAllowlistValidator.Strip(tree, ["secretNotes", "internalRank"]));
    }

    [Fact]
    public void Strip_DropsAWholeNotNode_WhenItsWrappedConditionIsFullyStripped()
    {
        // Not(nothing) has no coherent meaning under "silently ignore the unknown condition" — it
        // must disappear too, not flip into an always-false/always-true sentinel.
        var tree = new NotNode { Node = Eq("secretNotes") };

        Assert.Null(FieldAllowlistValidator.Strip(tree, ["secretNotes"]));
    }

    [Fact]
    public void Strip_LeavesAnUnrelatedBranchUntouched_ByReference()
    {
        var untouched = Eq("id");
        var tree = new AndNode { Nodes = [untouched, Eq("name"), Eq("secretNotes")] };

        var result = Assert.IsType<AndNode>(FieldAllowlistValidator.Strip(tree, ["secretNotes"]));

        Assert.Equal(2, result.Nodes.Count);
        Assert.Same(untouched, result.Nodes[0]);
    }

    // ---- #1872: ValidateStructuralColumns holds setValuesSpec.colId to the SAME Filterable flag,
    // ---- but WITHOUT a Lenient carve-out (mirrors GroupSpec.GroupCols/Aggregation.Field) ------------

    [Fact]
    public void ValidateStructuralColumns_Throws_ForANonFilterableSetValuesColId_InStrictMode()
    {
        var schema = MakeSchemaWithNonFilterableField(GridSchemaMode.Strict);
        var setValues = new SetValuesSpec { ColId = "costBasis" };

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.ValidateStructuralColumns(schema, group: null, aggregations: null, setValues));

        var violation = Assert.Single(ex.Violations);
        Assert.Equal("costBasis", violation.ColId);
        Assert.Equal("field-not-filterable", violation.Reason);
    }

    /// <summary>The decisive contrast with <see cref="Lenient_StripsNonFilterableColIds_AndKeepsFilterableOnes_WithoutThrowing"/>:
    /// the IDENTICAL flag on the IDENTICAL colId still throws in Lenient mode here, because a
    /// <c>setValuesSpec.colId</c> is the entire question rather than a condition narrowing it.</summary>
    [Fact]
    public void ValidateStructuralColumns_Throws_ForANonFilterableSetValuesColId_EvenInLenientMode()
    {
        var schema = MakeSchemaWithNonFilterableField(GridSchemaMode.Lenient);
        var setValues = new SetValuesSpec { ColId = "costBasis" };

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.ValidateStructuralColumns(schema, group: null, aggregations: null, setValues));

        var violation = Assert.Single(ex.Violations);
        Assert.Equal("costBasis", violation.ColId);
        Assert.Equal("field-not-filterable", violation.Reason);
    }

    [Fact]
    public void ValidateStructuralColumns_DoesNotThrow_ForAFilterableSetValuesColId()
    {
        var schema = MakeSchemaWithNonFilterableField(GridSchemaMode.Strict);
        var setValues = new SetValuesSpec { ColId = "name" };

        FieldAllowlistValidator.ValidateStructuralColumns(schema, group: null, aggregations: null, setValues);
    }

    // ---- #1907: a CONSUMED groupKeys entry is held to the SAME Filterable flag as an ordinary
    // ---- filter condition, because DrillDownFilter.Synthesize turns it into one -----------------------

    [Fact]
    public void ValidateStructuralColumns_Throws_ForANonFilterableConsumedDrillDownKey_InStrictMode()
    {
        var schema = MakeSchemaWithGroupableNonFilterableField(GridSchemaMode.Strict);
        var group = new GroupSpec { GroupCols = ["region"], GroupKeys = [new StringValue { Value = "West" }] };

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.ValidateStructuralColumns(schema, group, aggregations: null));

        var violation = Assert.Single(ex.Violations);
        Assert.Equal("region", violation.ColId);
        Assert.Equal("field-not-filterable", violation.Reason);
    }

    /// <summary>The decisive contrast, mirroring <see cref="ValidateStructuralColumns_Throws_ForANonFilterableSetValuesColId_EvenInLenientMode"/>:
    /// the IDENTICAL flag on the IDENTICAL colId still throws in Lenient mode, because dropping a
    /// consumed drill-down key would silently return a DIFFERENT bucket's rows mislabeled as the one
    /// the client asked for.</summary>
    [Fact]
    public void ValidateStructuralColumns_Throws_ForANonFilterableConsumedDrillDownKey_EvenInLenientMode()
    {
        var schema = MakeSchemaWithGroupableNonFilterableField(GridSchemaMode.Lenient);
        var group = new GroupSpec { GroupCols = ["region"], GroupKeys = [new StringValue { Value = "West" }] };

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.ValidateStructuralColumns(schema, group, aggregations: null));

        var violation = Assert.Single(ex.Violations);
        Assert.Equal("region", violation.ColId);
        Assert.Equal("field-not-filterable", violation.Reason);
    }

    /// <summary>The scoping proof: the SAME non-filterable-but-groupable column is untouched while it
    /// is only declared for a (not-yet-drilled-into) grouping level — plain bucketing across every
    /// value is exactly what <c>groupable: true</c> alone already permits. Only a CONSUMED key (see
    /// the two tests above) triggers the check.</summary>
    [Fact]
    public void ValidateStructuralColumns_DoesNotThrow_ForANonFilterableGroupCol_WhenNoKeyIsConsumedYet()
    {
        var schema = MakeSchemaWithGroupableNonFilterableField(GridSchemaMode.Strict);
        var group = new GroupSpec { GroupCols = ["region"], GroupKeys = [] };

        FieldAllowlistValidator.ValidateStructuralColumns(schema, group, aggregations: null);
    }

    /// <summary>A consumed key that is BOTH not groupable (the schema-wide default) AND not filterable
    /// reports two distinct violations, not just one — matching this file's "collect every violation"
    /// precedent already pinned by <see cref="Strict_CollectsBothUnknownAndNonFilterableColIds_WithDistinctReasons"/>.</summary>
    [Fact]
    public void ValidateStructuralColumns_ReportsBothGroupableAndFilterableViolations_ForTheSameConsumedColId()
    {
        var schema = MakeSchemaWithNonFilterableField(GridSchemaMode.Strict);
        var group = new GroupSpec { GroupCols = ["costBasis"], GroupKeys = [new StringValue { Value = "100" }] };

        var ex = Assert.Throws<UnknownFieldException>(
            () => FieldAllowlistValidator.ValidateStructuralColumns(schema, group, aggregations: null));

        Assert.Equal(2, ex.Violations.Count);
        Assert.Contains(ex.Violations, v => v.ColId == "costBasis" && v.Reason == "unknown-field" && v.DisplayText.Contains("groupable:false"));
        Assert.Contains(ex.Violations, v => v.ColId == "costBasis" && v.Reason == "field-not-filterable");
    }
}
