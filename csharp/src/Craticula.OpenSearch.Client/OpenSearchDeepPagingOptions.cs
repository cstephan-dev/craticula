namespace Craticula.OpenSearch.Client;

/// <summary>
/// Host configuration for <see cref="OpenSearchGridQuery.FetchPageAsync"/>'s deep-paging machinery
/// (PERF-2, OSE-10, OSE-11(d)).
/// </summary>
public sealed record OpenSearchDeepPagingOptions
{
    /// <summary>
    /// OSE-10: the offset beyond which plain <c>from</c>/<c>size</c> is refused by the engine — the
    /// point at which <see cref="OpenSearchGridQuery"/> switches to PIT + <c>search_after</c>. Default
    /// <b>10,000</b>, matching ES/OS's own <c>index.max_result_window</c> default: a request whose
    /// <c>from + size</c> exceeds this value against an index that never overrode the setting fails at
    /// the engine regardless of what this adapter does, so tracking the same default here means the
    /// switchover happens exactly where the plain path would otherwise start failing.
    /// </summary>
    public int MaxResultWindow { get; init; } = 10_000;

    /// <summary>
    /// How many rows (or, for a grouped level, buckets) one internal forward-walk step advances when
    /// catching a cached cursor up to a requested offset that is ahead of it — see
    /// <see cref="OpenSearchGridQuery"/>'s own remarks ("The hard part") for why this walk exists at
    /// all and why it is inherent to <c>search_after</c>/<c>composite.after</c> rather than a shortcut.
    /// A larger value means fewer round trips per catch-up walk at the cost of more rows discarded per
    /// step when the true target is only slightly ahead of the batch boundary; 1,000 keeps a worst-case
    /// catch-up (crossing <see cref="MaxResultWindow"/> for the first time) to single digits of round
    /// trips for AG Grid's own 100-row block size.
    /// </summary>
    public int WalkBatchSize { get; init; } = 1_000;

    /// <summary>
    /// How long an opened PIT — and the cursor-cache entry that references it — stays valid without
    /// being touched again. Refreshed (both server-side, by re-sending <c>keep_alive</c> on every
    /// search against the PIT, and cache-side, via <see cref="SearchCursorEntry.ExpiresAt"/>) on every
    /// use, so this is "how long a scrolling user may pause between blocks", not a hard session length.
    /// Default 5 minutes — long enough for an interactive grid session's own think time, short enough
    /// that an abandoned PIT's cluster-memory cost (see <see cref="ISearchCursorCache"/>'s own remarks)
    /// is bounded to a few minutes even when the adapter's own best-effort close never runs (a crashed
    /// host, a cache that silently forgot the entry).
    /// </summary>
    public TimeSpan PitKeepAlive { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// OSE-16: ask the engine for the <b>exact</b> row count (<c>track_total_hits: true</c>) when the caller asks for a count
    /// (<c>includeRowCount</c>). Default <see langword="false"/>: the count is capped at <see cref="MaxResultWindow"/> and reported as unknown
    /// (<see cref="OpenSearchGridPage.RowCount"/> <see langword="null"/>) once the true total exceeds it, which is cheap and lets
    /// the grid keep asking for blocks. <see langword="true"/> makes the count request count every matching document, which costs the
    /// cluster work proportional to the matches on every first-block request; a host that wants a total above 10,000 opts in, knowingly.
    /// It changes the count request only, never the page query or the compiled artifact.
    /// </summary>
    public bool ExactRowCount { get; init; }

    /// <summary>
    /// Which engine this client talks to (OSE-15): only the point-in-time create and close calls differ.
    /// Default <see cref="SearchEngineFlavour.OpenSearch"/>; against Elasticsearch 8 set
    /// <see cref="SearchEngineFlavour.Elasticsearch"/>.
    /// </summary>
    public SearchEngineFlavour Flavour { get; init; } = SearchEngineFlavour.OpenSearch;

    public static readonly OpenSearchDeepPagingOptions Default = new();

    internal void Validate()
    {
        if (!Enum.IsDefined(Flavour))
        {
            throw new OpenSearchClientCompilationException($"Flavour {(int)Flavour} is not a SearchEngineFlavour (OpenSearch or Elasticsearch).");
        }

        if (MaxResultWindow <= 0)
        {
            throw new OpenSearchClientCompilationException(
                $"MaxResultWindow must be positive, got {MaxResultWindow}. A zero or negative window would force " +
                "every row fetch, including row 0, through the PIT/search_after path.");
        }

        if (WalkBatchSize <= 0)
        {
            throw new OpenSearchClientCompilationException(
                $"WalkBatchSize must be positive, got {WalkBatchSize}. A zero or negative step would never advance " +
                "a catch-up walk, so it would never reach the requested offset.");
        }

        if (PitKeepAlive <= TimeSpan.Zero)
        {
            throw new OpenSearchClientCompilationException(
                $"PitKeepAlive must be positive, got {PitKeepAlive}. A non-positive keep-alive would have the " +
                "engine close the PIT before this adapter's own next request could use it.");
        }
    }
}
