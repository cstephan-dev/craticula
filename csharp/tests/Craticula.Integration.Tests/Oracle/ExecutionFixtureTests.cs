using Craticula.Integration.Tests.Oracle;

namespace Craticula.Integration.Tests.Oracle;

/// <summary>
/// The loader reads every fixture that carries the pair, and the expectations it hands the harnesses are the
/// ones XEQ-1/XEQ-4 name. Offline: no engine runs here.
/// </summary>
public class ExecutionFixtureTests
{
    public static IEnumerable<object[]> Fixtures() => ExecutionFixture.All().Select(name => new object[] { name });

    [Fact]
    public void The_Tier_Has_The_Fixtures_Authored_In_2204()
    {
        // A floor: #2204 authored 171 pairs; a smaller count means the loader or the fixture root moved.
        Assert.True(ExecutionFixture.All().Count >= 171, $"only {ExecutionFixture.All().Count} fixtures carry the pair");
    }

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void A_Fixture_Loads_With_Typed_Columns_And_At_Least_One_Backend(string name)
    {
        var fixture = ExecutionFixture.Load(name);

        Assert.NotEmpty(fixture.Seed);
        Assert.NotEmpty(fixture.Backends);
        Assert.All(fixture.Seed, row => Assert.Equal(fixture.Schema.Fields.Count, row.Cells.Count));

        var types = ColumnTypes.Resolve(fixture.Schema, fixture.Request, fixture.Expected.Columns);
        Assert.Equal(fixture.Expected.Columns.Count, types.Count);

        foreach (var divergence in fixture.Divergences.Where(d => d.Expectation is ExpectRows))
        {
            var rows = ((ExpectRows)divergence.Expectation).Result;
            Assert.Equal(fixture.Expected.Columns.Count, rows.Columns.Count);
        }
    }

    [Fact]
    public void A_Backend_Without_An_Entry_Is_Held_To_The_Main_Result_And_One_With_An_Entry_To_Its_Own()
    {
        var fixture = ExecutionFixture.Load("group-aggregation/017-set-filter-values-excludes-own-field-filter");

        var postgres = Assert.IsType<ExpectRows>(fixture.ExpectationFor("postgres"));
        Assert.Same(fixture.Expected, postgres.Result);

        var mongo = Assert.IsType<ExpectRows>(fixture.ExpectationFor("mongo"));
        Assert.NotSame(fixture.Expected, mongo.Result);
        Assert.Equal(2, mongo.Result.Rows.Count);
        Assert.Equal(3, fixture.Expected.Rows.Count);
    }

    [Fact]
    public void A_Set_Values_Request_Types_Its_Column_By_The_Field_Named_By_ColId()
    {
        var fixture = ExecutionFixture.Load("group-aggregation/016-set-filter-values-no-active-filters");

        Assert.Equal(["region"], fixture.Expected.Columns);
        Assert.Equal([Craticula.Core.Schema.FieldType.String], ColumnTypes.Resolve(fixture.Schema, fixture.Request, fixture.Expected.Columns));
    }

    [Fact]
    public void An_Aggregate_Column_Is_Typed_By_Its_Function_Not_By_The_Field()
    {
        var fixture = ExecutionFixture.Load("ag-grid-majors/004-v34-grouped-request-with-date-conditions");

        Assert.Equal(
            [Craticula.Core.Schema.FieldType.String, Craticula.Core.Schema.FieldType.Decimal],
            ColumnTypes.Resolve(fixture.Schema, fixture.Request, fixture.Expected.Columns));
    }

    [Fact]
    public void An_Export_Discards_Group_State_So_Its_Value_Columns_Are_Plain_Fields()
    {
        var fixture = ExecutionFixture.Load("page-export/011-export-all-ignores-multi-level-group-and-multiple-aggs");

        var index = fixture.Expected.Columns.ToList().IndexOf("totalAmount");
        Assert.True(index >= 0);
        Assert.Equal(
            Craticula.Core.Schema.FieldType.Decimal,
            ColumnTypes.Resolve(fixture.Schema, fixture.Request, fixture.Expected.Columns)[index]);
        Assert.DoesNotContain("totalAmount", ColumnTypes.AverageColumns(fixture.Request, fixture.Expected.Columns));
    }

    [Fact]
    public void An_Export_Stays_Non_Grouped_Whatever_The_Order_Of_Its_Value_Columns()
    {
        var schema = ExecutionFixture.Load("page-export/011-export-all-ignores-multi-level-group-and-multiple-aggs").Schema;
        using var request = System.Text.Json.JsonDocument.Parse("""
            {
              "rowGroupCols": [ { "id": "region" } ],
              "groupKeys": [],
              "valueCols": [
                { "id": "totalAmount", "field": "totalAmount", "aggFunc": "avg" },
                { "id": "totalAmount2", "field": "totalAmount", "aggFunc": "sum" }
              ],
              "exportFormat": "csv"
            }
            """);

        Assert.True(ColumnTypes.IsExport(request.RootElement));
        Assert.False(ColumnTypes.IsGrouped(request.RootElement));
        Assert.Empty(ColumnTypes.AverageColumns(request.RootElement, ["totalAmount", "totalAmount2"]));
        Assert.Equal(
            Craticula.Core.Schema.FieldType.Decimal,
            ColumnTypes.Resolve(schema, request.RootElement, ["totalAmount"])[0]);
    }
}
