using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql;

/// <summary>
/// Everything <see cref="SqlQueryBuilder.BuildSetValues"/> needs to compile one BM-3 distinct-values
/// query — the counterpart of <see cref="SqlQueryRequest"/>, and deliberately a separate record
/// rather than a mode of it.
///
/// <para><b>Why not another flag on <see cref="SqlQueryRequest"/>.</b> That record's
/// <see cref="SqlQueryRequest.Page"/> is <c>required</c>, and its
/// <see cref="SqlQueryRequest.Sort"/>/<see cref="SqlQueryRequest.Group"/>/<see cref="SqlQueryRequest.Aggregations"/>
/// have no meaning at all here: a distinct-values query has no page (GRP-6's cap is a ceiling, not a
/// window), no user sort (it orders by the distinct column, always) and no aggregation. Folding the
/// two together would mean every set-values caller passing three ignored properties and one dummy
/// one, and every reader of the builder asking which combination is legal.</para>
/// </summary>
public sealed record SqlSetValuesRequest
{
    /// <summary>SC-1..SC-5: the field allowlist, physical mappings and
    /// <see cref="GridSchema.Source"/> (SQL-0's <c>FROM</c> target). <see cref="GridSchema.StableKey"/>
    /// is NOT read — SEM-6's tiebreaker has nothing to do here, since the distinct column is already
    /// unique across the result rows by construction.</summary>
    public required GridSchema Schema { get; init; }

    /// <summary>
    /// GRP-6: the column whose distinct values this query returns — the <c>SELECT DISTINCT</c>
    /// expression and the sole <c>ORDER BY</c> key.
    ///
    /// <para><b>Client-controlled, and it must reach here already allowlisted</b>, exactly like
    /// <see cref="Filter"/>: pass the whole <see cref="SetValuesSpec"/> to
    /// <see cref="GridFilterPipeline.ProcessSetValues"/>, which applies SC-1 in every schema mode
    /// (Lenient may not drop it — it is the question, not a condition on it).</para>
    /// </summary>
    public required string ColId { get; init; }

    /// <summary>
    /// The filter that survives GRP-6's own-field exclusion, with the search-prefix predicate already
    /// folded in — i.e. what <see cref="GridFilterPipeline.ProcessSetValues"/> returns.
    /// <see langword="null"/> compiles to no <c>WHERE</c> clause at all.
    ///
    /// <para>The exclusion is <b>not</b> re-derived by this builder, and that is the difference from
    /// GRP-5's drill-down: there is no provenance to preserve here. The prefix predicate is an
    /// ordinary <c>startsWith</c> governed by SEM-1/SEM-7 exactly as a user's own would be, so once
    /// the pipeline has merged it in, nothing downstream needs to tell the two apart.</para>
    /// </summary>
    public FilterNode? Filter { get; init; }

    /// <summary>
    /// GRP-6/SEC-3: how many distinct values the statement may return, <b>already resolved</b> by
    /// <see cref="ResourceLimitsValidator.ResolveSetValuesLimit"/> — the client's request clamped to
    /// the host's <see cref="ResourceLimits.MaxSetValuesResults"/>, or
    /// <see cref="ResourceLimits.DefaultSetValuesResults"/> when it asked for none. A distinct-values
    /// query is never unbounded, so this is not nullable: "no cap" is not one of the answers.
    /// </summary>
    public required int RowLimit { get; init; }
}
