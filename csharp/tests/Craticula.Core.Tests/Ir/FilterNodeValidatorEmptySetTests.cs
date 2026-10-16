using Craticula.Core.Ir;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// SEM-8: an empty <c>in</c>/<c>notIn</c> list is valid (it is a constant, not a malformed node); a missing
/// array, a null element and the other operators' structural rules are unchanged.
/// </summary>
public class FilterNodeValidatorEmptySetTests
{
    [Theory]
    [InlineData(ComparisonOperator.In)]
    [InlineData(ComparisonOperator.NotIn)]
    public void An_Empty_Values_Array_Is_Valid(ComparisonOperator op) =>
        FilterNodeValidator.Validate(new ComparisonNode { Field = "status", Op = op, Values = [] });

    [Theory]
    [InlineData(ComparisonOperator.In)]
    [InlineData(ComparisonOperator.NotIn)]
    public void A_Missing_Values_Array_Is_Still_Rejected(ComparisonOperator op) =>
        Assert.Throws<FilterNodeValidationException>(() =>
            FilterNodeValidator.Validate(new ComparisonNode { Field = "status", Op = op, Values = null }));

    [Fact]
    public void A_Null_Element_Is_Still_Rejected() =>
        Assert.Throws<FilterNodeValidationException>(() =>
            FilterNodeValidator.Validate(new ComparisonNode
            {
                Field = "status",
                Op = ComparisonOperator.In,
                Values = [new StringValue { Value = "a" }, null!]
            }));

    [Fact]
    public void An_Empty_Set_Is_Valid_Inside_And_Or_Not() =>
        FilterNodeValidator.Validate(new NotNode
        {
            Node = new OrNode
            {
                Nodes =
                [
                    new ComparisonNode { Field = "status", Op = ComparisonOperator.In, Values = [] },
                    new AndNode { Nodes = [new ComparisonNode { Field = "status", Op = ComparisonOperator.NotIn, Values = [] }] }
                ]
            }
        });
}
