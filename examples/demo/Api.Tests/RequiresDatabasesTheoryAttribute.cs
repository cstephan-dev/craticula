namespace Demo.Api.Tests;

/// <summary>A [Theory] with the same skip rule as <see cref="RequiresDatabasesFactAttribute"/>.</summary>
public sealed class RequiresDatabasesTheoryAttribute : TheoryAttribute
{
    public RequiresDatabasesTheoryAttribute() => Skip = new RequiresDatabasesFactAttribute().Skip;
}
