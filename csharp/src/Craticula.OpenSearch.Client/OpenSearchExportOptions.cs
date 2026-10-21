namespace Craticula.OpenSearch.Client;

/// <summary>Host configuration for <see cref="OpenSearchExportStream"/>.</summary>
public sealed record OpenSearchExportOptions
{
    /// <summary>
    /// EXP-3's pinned default: how many hits one <c>search_after</c> batch fetches. Default
    /// <b>5,000</b>, mirroring every sibling adapter's own chunk/batch size
    /// (<c>Craticula.Dapper.DapperExportOptions.ChunkSize</c>,
    /// <c>Craticula.EntityFrameworkCore.EfCoreExportOptions.ChunkSize</c>) for the identical
    /// round-trips-versus-buffer-size trade-off — see either's own remarks.
    /// </summary>
    public int ChunkSize { get; init; } = 5_000;

    /// <summary>
    /// How long the export's ONE PIT stays valid without being touched again — refreshed on every
    /// batch. Unlike <see cref="OpenSearchDeepPagingOptions.PitKeepAlive"/>, this has no cursor-cache
    /// counterpart to also bound: an export owns no cross-request state at all
    /// (<see cref="OpenSearchExportStream"/>'s own remarks), so this only needs to outlast the time
    /// between two CONSECUTIVE batches of the SAME export call, never an idle user's think time.
    /// </summary>
    public TimeSpan PitKeepAlive { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Which engine this client talks to (OSE-15): only the point-in-time create and close calls differ.
    /// Default <see cref="SearchEngineFlavour.OpenSearch"/>; against Elasticsearch 8 set
    /// <see cref="SearchEngineFlavour.Elasticsearch"/>.
    /// </summary>
    public SearchEngineFlavour Flavour { get; init; } = SearchEngineFlavour.OpenSearch;

    internal void Validate()
    {
        if (ChunkSize <= 0)
        {
            throw new OpenSearchClientCompilationException(
                $"ChunkSize must be positive, got {ChunkSize}. A zero or negative chunk fetches no hits, so the " +
                "walk would terminate on its first batch and report an empty export as a complete one.");
        }

        if (PitKeepAlive <= TimeSpan.Zero)
        {
            throw new OpenSearchClientCompilationException(
                $"PitKeepAlive must be positive, got {PitKeepAlive}. A non-positive keep-alive would have the " +
                "engine close the PIT before this export's own next batch could use it.");
        }
    }

    internal static readonly OpenSearchExportOptions Default = new();
}
