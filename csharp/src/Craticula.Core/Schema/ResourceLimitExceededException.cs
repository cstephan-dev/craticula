namespace Craticula.Core.Schema;

/// <summary>
/// SEC-3: one exceeded limit — which one, the actual value reached, and the configured max.
/// <see cref="Actual"/> is <see cref="long"/> (not <see cref="int"/>) so a page-size violation can
/// report its true magnitude even when <c>endRow - startRow</c> would overflow a 32-bit int.
/// </summary>
public sealed record ResourceLimitViolation(string LimitName, long Actual, int Max);

/// <summary>
/// SEC-3: thrown by <see cref="ResourceLimitsValidator.Validate"/> when one or more configured
/// resource limits are exceeded. Carries every violated limit (not just the first) — mirroring
/// <see cref="UnknownFieldException"/>'s style of naming everything wrong in one clean rejection.
/// </summary>
public sealed class ResourceLimitExceededException : Exception
{
    public IReadOnlyList<ResourceLimitViolation> Violations { get; }

    public ResourceLimitExceededException(IReadOnlyList<ResourceLimitViolation> violations)
        : base(BuildMessage(violations))
    {
        Violations = violations;
    }

    private static string BuildMessage(IReadOnlyList<ResourceLimitViolation> violations) =>
        "SEC-3 resource limit(s) exceeded: " +
        string.Join("; ", violations.Select(v => $"{v.LimitName} (actual {v.Actual}, max {v.Max})"));
}
