namespace Craticula.Core.Schema;

/// <summary>
/// SEC-3: host-configurable resource ceilings enforced by <see cref="ResourceLimitsValidator"/>
/// over an IR filter tree and page spec. All five are plain, non-nullable <see cref="int"/>
/// properties with SEC-3's own defaults — by design there is no way to express "no limit" here
/// (SEC-3: "none unlimited"). Each <c>init</c> accessor additionally rejects non-positive values:
/// a limit of 0 or less is not "no limit", it is a silent denial-of-service (every request,
/// however small, would exceed it) — a plausible outcome of a config binder mapping an absent or
/// blank configuration key to the type's default. There is no upper-bound check: SEC-3 forbids the
/// nullable-"unlimited" sentinel, not a host's own choice to set a very large positive number.
/// </summary>
public sealed record ResourceLimits
{
    private readonly int _maxDepth = 8;
    private readonly int _maxLeaves = 64;
    private readonly int _maxInValues = 1000;
    private readonly int _maxPageSize = 1000;
    private readonly int _maxSetValuesResults = 10000;

    /// <summary>Max filter tree depth, root node = depth 1 (SEC-3 default: 8).</summary>
    public int MaxDepth
    {
        get => _maxDepth;
        init => _maxDepth = RequirePositive(value, nameof(MaxDepth));
    }

    /// <summary>Max tree-wide leaf comparison count, summed across every branch (SEC-3 default: 64).</summary>
    public int MaxLeaves
    {
        get => _maxLeaves;
        init => _maxLeaves = RequirePositive(value, nameof(MaxLeaves));
    }

    /// <summary>Max values in a single <c>in</c>/<c>notIn</c> set comparison (SEC-3 default: 1000).</summary>
    public int MaxInValues
    {
        get => _maxInValues;
        init => _maxInValues = RequirePositive(value, nameof(MaxInValues));
    }

    /// <summary>Max requested page size, i.e. (endRow - startRow) on a <c>RangePage</c> (SEC-3 default: 1000).</summary>
    public int MaxPageSize
    {
        get => _maxPageSize;
        init => _maxPageSize = RequirePositive(value, nameof(MaxPageSize));
    }

    /// <summary>
    /// Caps BM-3's distinct-values query LIMIT — a compiled query's own result size, not an input
    /// shape to reject, so unlike the other four limits <see cref="ResourceLimitsValidator"/> does
    /// not check this one; it is recorded here for the future BM-3 compiler work to consume
    /// (SEC-3 default: 10000).
    /// </summary>
    public int MaxSetValuesResults
    {
        get => _maxSetValuesResults;
        init => _maxSetValuesResults = RequirePositive(value, nameof(MaxSetValuesResults));
    }

    private static int RequirePositive(int value, string paramName) => value > 0
        ? value
        : throw new ArgumentOutOfRangeException(paramName, value, $"{paramName} must be a positive integer — SEC-3 requires every limit configurable but none unlimited, and a value <= 0 would reject every request regardless of size.");
}
