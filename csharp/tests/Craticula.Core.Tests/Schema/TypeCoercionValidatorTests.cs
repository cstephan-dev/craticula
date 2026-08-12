using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Hand-built boundary tests for <see cref="TypeCoercionValidator"/> (SEM-5): pass-through when a
/// <see cref="TypedValue"/> already matches its field's declared <see cref="FieldType"/>, successful
/// string-to-typed coercion per declared type, <see cref="NullValue"/> always passing, every other
/// mismatch rejected, tree-wide violation collection, and the immutable-rebuild-only-what-changed
/// behavior. Real-fixture-driven coverage of the rejection paths lives in
/// <see cref="TypeCoercionAdversarialFixtureTests"/>.
/// </summary>
public class TypeCoercionValidatorTests
{
    private static GridSchema Schema { get; } = new()
    {
        Source = "items",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = new() { Type = FieldType.Int64 },
            ["name"] = new() { Type = FieldType.String },
            ["amount"] = new() { Type = FieldType.Decimal },
            ["price"] = new() { Type = FieldType.Double },
            ["active"] = new() { Type = FieldType.Boolean },
            ["createdDate"] = new() { Type = FieldType.Date },
            ["createdAt"] = new() { Type = FieldType.Datetime },
        }
    };

    private static ComparisonNode Eq(string field, TypedValue value) => new()
    {
        Field = field,
        Op = ComparisonOperator.Eq,
        Value = value,
    };

    [Fact]
    public void Null_Filter_Passes_Through_As_Null() =>
        Assert.Null(TypeCoercionValidator.Coerce(null, Schema));

    [Fact]
    public void Already_Matching_Variant_Passes_Through_Unchanged_By_Reference()
    {
        var node = Eq("id", new Int64Value { Value = 42 });

        var coerced = TypeCoercionValidator.Coerce(node, Schema);

        Assert.Same(node, coerced);
    }

    [Fact]
    public void NullValue_Always_Passes_Regardless_Of_Declared_Type()
    {
        var node = Eq("id", new NullValue());

        var coerced = TypeCoercionValidator.Coerce(node, Schema);

        Assert.Same(node, coerced);
    }

    [Theory]
    [InlineData("id", "30", typeof(Int64Value))]
    [InlineData("amount", "10.50", typeof(DecimalValue))]
    [InlineData("price", "3.14", typeof(DoubleValue))]
    [InlineData("active", "true", typeof(BooleanValue))]
    [InlineData("createdDate", "2026-08-11", typeof(DateValue))]
    public void StringValue_Coerces_Into_The_Fields_Declared_Type(string field, string raw, Type expectedType)
    {
        var node = Eq(field, new StringValue { Value = raw });

        var coerced = Assert.IsType<ComparisonNode>(TypeCoercionValidator.Coerce(node, Schema));

        Assert.IsType(expectedType, coerced.Value);
    }

    [Fact]
    public void StringValue_Coerces_Into_Int64Value_With_The_Parsed_Number()
    {
        var node = Eq("id", new StringValue { Value = "30" });

        var coerced = Assert.IsType<ComparisonNode>(TypeCoercionValidator.Coerce(node, Schema));

        Assert.Equal(30L, Assert.IsType<Int64Value>(coerced.Value).Value);
    }

    [Fact]
    public void StringValue_Coerces_Into_DatetimeValue_Defaulting_Zone_To_Utc()
    {
        var node = Eq("createdAt", new StringValue { Value = "2026-08-11T10:00:00Z" });

        var coerced = Assert.IsType<ComparisonNode>(TypeCoercionValidator.Coerce(node, Schema));

        var datetime = Assert.IsType<DatetimeValue>(coerced.Value);
        Assert.Equal("2026-08-11T10:00:00Z", datetime.Value);
        Assert.Equal("UTC", datetime.Zone);
    }

    [Fact]
    public void Unparseable_StringValue_Throws_TypeCoercionException()
    {
        var node = Eq("id", new StringValue { Value = "not-a-number" });

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(node, Schema));

        var violation = Assert.Single(ex.Violations);
        Assert.Equal("id", violation.Field);
        Assert.Equal(FieldType.Int64, violation.DeclaredType);
    }

    [Fact]
    public void NonString_Mismatch_Throws_TypeCoercionException_Without_Attempting_To_Parse()
    {
        // A BooleanValue against a String field: SEM-5's other-mismatch case, no reverse coercion.
        var node = Eq("name", new BooleanValue { Value = true });

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(node, Schema));

        var violation = Assert.Single(ex.Violations);
        Assert.Equal("name", violation.Field);
        Assert.Equal(FieldType.String, violation.DeclaredType);
    }

    [Fact]
    public void Collects_Every_Violation_TreeWide_Not_Just_The_First()
    {
        var tree = new AndNode
        {
            Nodes =
            [
                Eq("id", new StringValue { Value = "not-a-number" }),
                Eq("amount", new StringValue { Value = "12.34.56" }),
            ]
        };

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(tree, Schema));

        Assert.Equal(2, ex.Violations.Count);
        Assert.Contains(ex.Violations, v => v.Field == "id");
        Assert.Contains(ex.Violations, v => v.Field == "amount");
    }

    [Fact]
    public void Every_Value_Element_In_A_Set_Comparison_Is_Coerced_Independently()
    {
        var node = new ComparisonNode
        {
            Field = "id",
            Op = ComparisonOperator.In,
            Values = [new StringValue { Value = "1" }, new Int64Value { Value = 2 }],
        };

        var coerced = Assert.IsType<ComparisonNode>(TypeCoercionValidator.Coerce(node, Schema));

        Assert.Equal(1L, Assert.IsType<Int64Value>(coerced.Values![0]).Value);
        Assert.Same(node.Values[1], coerced.Values[1]);
    }

    [Fact]
    public void Both_Bad_Values_In_A_Set_Comparison_Are_Collected_As_Separate_Violations()
    {
        var node = new ComparisonNode
        {
            Field = "id",
            Op = ComparisonOperator.In,
            Values = [new StringValue { Value = "true" }, new StringValue { Value = "false" }],
        };

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(node, Schema));

        Assert.Equal(2, ex.Violations.Count);
    }

    [Fact]
    public void From_And_To_On_A_Range_Comparison_Are_Each_Coerced()
    {
        var node = new ComparisonNode
        {
            Field = "amount",
            Op = ComparisonOperator.InRange,
            From = new StringValue { Value = "10.00" },
            To = new StringValue { Value = "20.00" },
            FromInclusive = true,
            ToInclusive = false,
        };

        var coerced = Assert.IsType<ComparisonNode>(TypeCoercionValidator.Coerce(node, Schema));

        Assert.Equal("10.00", Assert.IsType<DecimalValue>(coerced.From).Value);
        Assert.Equal("20.00", Assert.IsType<DecimalValue>(coerced.To).Value);
    }

    [Fact]
    public void Rebuilds_The_Tree_From_Only_The_Changed_Branch()
    {
        var unchangedLeaf = Eq("id", new Int64Value { Value = 1 });
        var changedLeaf = Eq("amount", new StringValue { Value = "10.00" });
        var tree = new AndNode { Nodes = [unchangedLeaf, changedLeaf] };

        var coerced = Assert.IsType<AndNode>(TypeCoercionValidator.Coerce(tree, Schema));

        Assert.NotSame(tree, coerced); // root changed: one child's comparison was rebuilt
        Assert.Same(unchangedLeaf, coerced.Nodes[0]); // untouched branch is reused, not reallocated
        Assert.NotSame(changedLeaf, coerced.Nodes[1]);
    }

    [Fact]
    public void Returns_The_Same_Tree_Reference_When_Nothing_Needed_Coercion()
    {
        var tree = new AndNode
        {
            Nodes = [Eq("id", new Int64Value { Value = 1 }), Eq("name", new StringValue { Value = "west" })]
        };

        var coerced = TypeCoercionValidator.Coerce(tree, Schema);

        Assert.Same(tree, coerced);
    }

    [Fact]
    public void Field_Absent_From_Schema_Throws_KeyNotFoundException_As_A_Precondition_Violation()
    {
        var node = Eq("unknownField", new Int64Value { Value = 1 });

        Assert.Throws<KeyNotFoundException>(() => TypeCoercionValidator.Coerce(node, Schema));
    }

    [Fact]
    public void Malformed_Tree_Throws_FilterNodeValidationException_Before_Coercion_Runs()
    {
        var malformed = new ComparisonNode { Field = "id", Op = ComparisonOperator.Eq }; // 'eq' requires 'value'

        Assert.Throws<FilterNodeValidationException>(() => TypeCoercionValidator.Coerce(malformed, Schema));
    }

    [Fact]
    public void Null_Element_In_A_Composite_Throws_FilterNodeValidationException_Not_NullReferenceException()
    {
        var tree = new AndNode { Nodes = [Eq("id", new Int64Value { Value = 1 }), null!] };

        Assert.Throws<FilterNodeValidationException>(() => TypeCoercionValidator.Coerce(tree, Schema));
    }

    [Fact]
    public void Overlong_Decimal_String_Is_Rejected_Even_Though_It_Matches_The_Numeric_Pattern()
    {
        // ir.schema.json's decimalValue.maxLength is 128 — a syntactically-valid but 500-digit
        // string must not be smuggled through coercion just because it fits stringValue's 8192 cap.
        var overlong = new string('1', 500);
        var node = Eq("amount", new StringValue { Value = overlong });

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(node, Schema));

        var violation = Assert.Single(ex.Violations);
        Assert.Equal("amount", violation.Field);
    }

    [Theory]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    [InlineData("1e400")]
    public void NonFinite_Double_Strings_Are_Rejected(string raw)
    {
        var node = Eq("price", new StringValue { Value = raw });

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(node, Schema));

        var violation = Assert.Single(ex.Violations);
        Assert.Equal("price", violation.Field);
    }

    [Fact]
    public void Violation_Carries_The_Stable_UncoercibleValue_Reason_Code()
    {
        var node = Eq("id", new StringValue { Value = "not-a-number" });

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(node, Schema));

        Assert.Equal("uncoercible-value", Assert.Single(ex.Violations).Reason);
    }
}
