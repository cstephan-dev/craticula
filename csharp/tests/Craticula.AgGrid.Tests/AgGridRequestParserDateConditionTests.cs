using Craticula.Core.Ir;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Hand-built boundary tests for <see cref="AgGridRequestParser.Parse"/>'s <c>date</c> filterType
/// dispatch — the same operator vocabulary as <c>number</c>, value taken from <c>dateFrom</c>
/// (<c>dateTo</c> unused for these non-range operators).
///
/// <para>AG-2 date presets share this same <c>type</c> slot but are resolved rather than rejected
/// since #1636 — they need a clock/zone/schema and have their own suites
/// (<see cref="AgGridRequestParserDatePresetTests"/>, <see cref="AgGridRequestParserDatePresetsFixtureTests"/>,
/// <see cref="DatePresetResolverTests"/>). What is pinned here is the interaction: resolving presets
/// must not have widened this file's own closed operator vocabulary.</para>
/// </summary>
public class AgGridRequestParserDateConditionTests
{
    private const string Envelope = """
        {{
          "startRow": 0, "endRow": 100,
          "rowGroupCols": [], "valueCols": [], "pivotCols": [], "pivotMode": false, "groupKeys": [],
          "filterModel": {{ "birthDate": {0} }},
          "sortModel": []
        }}
        """;

    private static ComparisonNode ParseSingleComparison(string entryJson)
    {
        var request = string.Format(Envelope, entryJson);
        var parsed = AgGridRequestParser.Parse(request);
        return Assert.IsType<ComparisonNode>(parsed.NativeFilter);
    }

    [Theory]
    [InlineData("equals", ComparisonOperator.Eq)]
    [InlineData("notEqual", ComparisonOperator.Neq)]
    [InlineData("lessThan", ComparisonOperator.Lt)]
    [InlineData("lessThanOrEqual", ComparisonOperator.Lte)]
    [InlineData("greaterThan", ComparisonOperator.Gt)]
    [InlineData("greaterThanOrEqual", ComparisonOperator.Gte)]
    public void Binary_Operator_Uses_DateFrom_As_StringValue(string wireType, ComparisonOperator expectedOp)
    {
        var comparison = ParseSingleComparison(
            $$"""{ "filterType": "date", "type": "{{wireType}}", "dateFrom": "1990-05-12", "dateTo": null }""");

        Assert.Equal("birthDate", comparison.Field);
        Assert.Equal(expectedOp, comparison.Op);
        var value = Assert.IsType<StringValue>(comparison.Value);
        Assert.Equal("1990-05-12", value.Value);
    }

    [Theory]
    [InlineData("blank", ComparisonOperator.Blank)]
    [InlineData("notBlank", ComparisonOperator.NotBlank)]
    public void Unary_Operator_Ignores_Null_DateFrom_And_DateTo(string wireType, ComparisonOperator expectedOp)
    {
        var comparison = ParseSingleComparison(
            $$"""{ "filterType": "date", "type": "{{wireType}}", "dateFrom": null, "dateTo": null }""");

        Assert.Equal(expectedOp, comparison.Op);
        Assert.Null(comparison.Value);
    }

    [Fact]
    public void Native_InRange_Throws_UnknownOperator()
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => ParseSingleComparison(
            """{ "filterType": "date", "type": "inRange", "dateFrom": "2026-01-01", "dateTo": "2026-12-31" }"""));

        Assert.Equal("unknown-operator", ex.Reason);
    }

    [Theory]
    [InlineData("today")]
    [InlineData("last7Days")]
    [InlineData("thisMonth")]
    public void Date_Preset_Type_Parsed_Without_Options_Is_A_Clean_MalformedRequest_Not_A_Guess(string presetType)
    {
        // #1636 resolves presets, but only when the caller supplied the AgGridParseOptions.Schema the
        // resolution needs (SEM-4: the field's declared type selects the bounds' typedValue variant).
        // This file's Parse(string) overload supplies none, so a preset is a clean rejection here —
        // deliberately NOT silently resolved against a guessed field type. The resolved-successfully
        // path lives in AgGridRequestParserDatePresetTests.
        var ex = Assert.Throws<AgGridRequestParseException>(() => ParseSingleComparison(
            $$"""{ "filterType": "date", "type": "{{presetType}}" }"""));

        Assert.Equal("malformed-request", ex.Reason);
        Assert.Contains("GridSchema", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Today")]
    [InlineData("last8Days")]
    [InlineData("notAnOperator")]
    public void A_Date_Type_Outside_Both_The_Operator_Set_And_The_Preset_Vocabulary_Is_Still_UnknownOperator(string type)
    {
        var ex = Assert.Throws<AgGridRequestParseException>(() => ParseSingleComparison(
            $$"""{ "filterType": "date", "type": "{{type}}" }"""));

        Assert.Equal("unknown-operator", ex.Reason);
    }
}
