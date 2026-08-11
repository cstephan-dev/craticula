using Craticula.Core.Ir;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// Hand-built boundary tests for <see cref="SortItemValidator"/>: a <see langword="null"/> sort array
/// is valid (no sort at all), an empty non-null array is rejected (<c>ir.schema.json</c>'s
/// <c>sort.minItems:1</c>), field-length/blank checks per item, and the collect-all-then-throw
/// pattern mirroring <see cref="FilterNodeValidator"/>'s own tests.
/// </summary>
public class SortItemValidatorTests
{
    private static SortItem Item(string field, SortDirection direction = SortDirection.Asc, NullsPosition nullsPosition = NullsPosition.Last) =>
        new() { Field = field, Direction = direction, NullsPosition = nullsPosition };

    [Fact]
    public void Null_Sort_Is_Valid() => SortItemValidator.Validate(null);

    [Fact]
    public void Empty_Sort_Array_Throws_SortItemValidationException()
    {
        var ex = Assert.Throws<SortItemValidationException>(() => SortItemValidator.Validate([]));

        Assert.Single(ex.Errors);
    }

    [Fact]
    public void Single_Valid_Item_Does_Not_Throw() => SortItemValidator.Validate([Item("lastName")]);

    [Fact]
    public void Multiple_Valid_Items_Do_Not_Throw() =>
        SortItemValidator.Validate([Item("department"), Item("salary", SortDirection.Desc, NullsPosition.First)]);

    [Theory]
    [InlineData("")]
    public void Blank_Field_Throws_SortItemValidationException(string field)
    {
        var ex = Assert.Throws<SortItemValidationException>(() => SortItemValidator.Validate([Item(field)]));

        Assert.Single(ex.Errors);
        Assert.Contains("field must not be blank", ex.Errors[0]);
    }

    [Fact]
    public void Overlong_Field_Throws_SortItemValidationException()
    {
        var overlong = new string('a', 257);

        var ex = Assert.Throws<SortItemValidationException>(() => SortItemValidator.Validate([Item(overlong)]));

        Assert.Single(ex.Errors);
        Assert.Contains("256-character limit", ex.Errors[0]);
    }

    [Fact]
    public void Field_At_Exactly_256_Characters_Does_Not_Throw()
    {
        var boundary = new string('a', 256);

        SortItemValidator.Validate([Item(boundary)]);
    }

    [Fact]
    public void Collects_Every_Violation_Not_Just_The_First()
    {
        var overlong = new string('a', 300);

        var ex = Assert.Throws<SortItemValidationException>(
            () => SortItemValidator.Validate([Item(""), Item(overlong)]));

        Assert.Equal(2, ex.Errors.Count);
    }

    [Fact]
    public void Null_Element_Throws_SortItemValidationException_Not_NullReferenceException()
    {
        var ex = Assert.Throws<SortItemValidationException>(() => SortItemValidator.Validate([Item("id"), null!]));

        Assert.Single(ex.Errors);
        Assert.Contains("must not be null", ex.Errors[0]);
    }
}
