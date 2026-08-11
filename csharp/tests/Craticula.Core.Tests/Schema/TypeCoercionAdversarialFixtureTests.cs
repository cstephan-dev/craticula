using System.Text.Json;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Core.Tests.Schema;

/// <summary>
/// Exercises <see cref="TypeCoercionValidator"/> against the real conformance fixtures authored in
/// #1630 (<c>spec/fixtures/adversarial/012-015</c>), cross-checked against each fixture's own
/// <c>expected/rejection.json</c> — mirroring <see cref="AdversarialFixtureAllowlistTests"/>'s and
/// <see cref="ResourceLimitsAdversarialFixtureTests"/>'s pattern of reading real fixture files via
/// <see cref="RepoPaths"/> rather than hardcoding values.
///
/// Every one of these fixtures sends its adversarial value through AG Grid's own native
/// <c>filterModel</c>, not <c>customFilters</c> — this repo has no wire adapter yet (see
/// <see cref="FilterTreeMergeFixtureTests"/>'s class doc comment for the same limitation), so each
/// test reads the field name and offending wire value straight out of the fixture's own
/// <c>agGridRequest.json</c> (never hardcoded) and hand-builds the equivalent <see cref="StringValue"/>
/// comparison <see cref="TypeCoercionValidator"/> would receive once that adapter exists — the
/// coercion failure itself is exercised for real; only the wire-to-IR translation step is stood in
/// for.
/// </summary>
public class TypeCoercionAdversarialFixtureTests
{
    private static GridSchema LoadSchema(string fixtureDir)
    {
        var path = RepoPaths.Fixture("adversarial", fixtureDir, "schema.json");
        Assert.True(File.Exists(path), $"Fixture schema not found at '{path}'.");
        return GridSchema.Parse(File.ReadAllText(path));
    }

    private static JsonDocument LoadRequest(string fixtureDir)
    {
        var path = RepoPaths.Fixture("adversarial", fixtureDir, "agGridRequest.json");
        Assert.True(File.Exists(path), $"Fixture request not found at '{path}'.");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    /// <summary>
    /// Binds the fixture's own <c>expected/rejection.json</c> oracle to what
    /// <paramref name="violations"/> actually carries — not fixture-to-fixture comparison. The
    /// oracle's <c>reason</c> is asserted against every violation's real
    /// <see cref="TypeCoercionViolation.Reason"/>, so this would fail if the coercion reason code
    /// ever drifted, closing the gap a prior review found in an earlier version of this method (which
    /// only asserted the fixture file against itself).
    /// </summary>
    private static void AssertRejectedAsUncoercibleValue(string fixtureDir, IReadOnlyList<TypeCoercionViolation> violations)
    {
        var path = RepoPaths.Fixture("adversarial", fixtureDir, "expected", "rejection.json");
        Assert.True(File.Exists(path), $"Fixture rejection oracle not found at '{path}'.");
        using var rejection = JsonDocument.Parse(File.ReadAllText(path));

        Assert.Equal("typeCoercion", rejection.RootElement.GetProperty("rejectedAt").GetString());
        var expectedReason = rejection.RootElement.GetProperty("reason").GetString();
        Assert.NotEmpty(violations);
        Assert.All(violations, v => Assert.Equal(expectedReason, v.Reason));
    }

    [Fact]
    public void Fixture012_NonNumericString_Against_Int64Field_Throws_TypeCoercionException()
    {
        const string fixtureDir = "012-non-numeric-string-int64-field";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var filterModel = request.RootElement.GetProperty("filterModel").EnumerateObject().Single();
        var field = filterModel.Name;
        var raw = filterModel.Value.GetProperty("filter").GetString()!;

        var node = new ComparisonNode { Field = field, Op = ComparisonOperator.Eq, Value = new StringValue { Value = raw } };

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(node, schema));

        Assert.Contains(ex.Violations, v => v.Field == field);
        AssertRejectedAsUncoercibleValue(fixtureDir, ex.Violations);
    }

    [Fact]
    public void Fixture013_MalformedCalendarDate_Against_DateField_Throws_TypeCoercionException()
    {
        const string fixtureDir = "013-malformed-date-string-date-field";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var filterModel = request.RootElement.GetProperty("filterModel").EnumerateObject().Single();
        var field = filterModel.Name;
        var raw = filterModel.Value.GetProperty("dateFrom").GetString()!;

        var node = new ComparisonNode { Field = field, Op = ComparisonOperator.Eq, Value = new StringValue { Value = raw } };

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(node, schema));

        Assert.Contains(ex.Violations, v => v.Field == field);
        AssertRejectedAsUncoercibleValue(fixtureDir, ex.Violations);
    }

    [Fact]
    public void Fixture014_BooleanLikeStrings_Against_Int64Field_Throws_TypeCoercionException()
    {
        const string fixtureDir = "014-boolean-like-value-int64-field";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var filterModel = request.RootElement.GetProperty("filterModel").EnumerateObject().Single();
        var field = filterModel.Name;
        var rawValues = filterModel.Value.GetProperty("values").EnumerateArray()
            .Select(v => v.GetString()!)
            .ToList();

        var node = new ComparisonNode
        {
            Field = field,
            Op = ComparisonOperator.In,
            Values = rawValues.Select(v => (TypedValue)new StringValue { Value = v }).ToList(),
        };

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(node, schema));

        Assert.Equal(rawValues.Count, ex.Violations.Count);
        Assert.All(ex.Violations, v => Assert.Equal(field, v.Field));
        AssertRejectedAsUncoercibleValue(fixtureDir, ex.Violations);
    }

    [Fact]
    public void Fixture015_MalformedDecimalLiteral_Against_DecimalField_Throws_TypeCoercionException()
    {
        const string fixtureDir = "015-malformed-decimal-string-decimal-field";
        var schema = LoadSchema(fixtureDir);
        using var request = LoadRequest(fixtureDir);

        var filterModel = request.RootElement.GetProperty("filterModel").EnumerateObject().Single();
        var field = filterModel.Name;
        var raw = filterModel.Value.GetProperty("filter").GetString()!;

        var node = new ComparisonNode { Field = field, Op = ComparisonOperator.Eq, Value = new StringValue { Value = raw } };

        var ex = Assert.Throws<TypeCoercionException>(() => TypeCoercionValidator.Coerce(node, schema));

        Assert.Contains(ex.Violations, v => v.Field == field);
        AssertRejectedAsUncoercibleValue(fixtureDir, ex.Violations);
    }
}
