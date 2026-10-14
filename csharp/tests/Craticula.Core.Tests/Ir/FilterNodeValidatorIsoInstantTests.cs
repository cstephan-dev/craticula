using Craticula.Core.Ir;

namespace Craticula.Core.Tests.Ir;

/// <summary>
/// Bug #2319: <see cref="FilterNodeValidator.IsIsoInstant"/> is the one rule for "is this wire string already an
/// ISO-8601 instant" that both Core's coercion (SEM-5) and the AG Grid adapter (a set filter's <c>values</c>,
/// SEM-4) apply, so the two cannot drift.
/// </summary>
public class FilterNodeValidatorIsoInstantTests
{
    [Theory]
    [InlineData("2026-01-01T00:00:00Z")]
    [InlineData("2026-01-01T00:00:00.001Z")]
    [InlineData("2026-01-01T09:30:00-05:00")]
    [InlineData("2026-06-15T12:30:00+14:00")]
    public void An_Iso_Instant_Is_Accepted(string raw) => Assert.True(FilterNodeValidator.IsIsoInstant(raw));

    [Theory]
    [InlineData("2026-01-01 00:00:00")]
    [InlineData("2026-01-01")]
    [InlineData("2026-01-01T00:00:00")]
    [InlineData("2026-13-01T00:00:00Z")]
    [InlineData("2026-01-01T24:00:00Z")]
    [InlineData("2026-01-01T00:00:60Z")]
    [InlineData("2026-01-01T00:00:00+15:00")]
    [InlineData("not a date")]
    public void Anything_Else_Is_Refused(string raw) => Assert.False(FilterNodeValidator.IsIsoInstant(raw));
}
