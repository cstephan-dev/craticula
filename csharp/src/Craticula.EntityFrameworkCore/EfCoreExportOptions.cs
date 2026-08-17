namespace Craticula.EntityFrameworkCore;

/// <summary>Host configuration for <see cref="EfCoreExportStream"/>.</summary>
public sealed record EfCoreExportOptions
{
    /// <summary>
    /// EXP-3: how many rows one keyset chunk fetches. Default <b>5,000</b>.
    ///
    /// <para><b>Why the number is configurable and why 5,000 is the default.</b> The chunk size is the
    /// only lever between two costs that pull in opposite directions: a small chunk means more round
    /// trips and more query executions (each one re-evaluating the filter and re-seeking on the sort
    /// keys), while a large one means more rows held by the provider's own read buffer at once and a
    /// longer command holding a connection. 5,000 rows of a typical grid projection is a few hundred
    /// kilobytes — small enough that it is not the thing that decides an exporting host's memory
    /// profile, large enough that a million-row export is 200 statements rather than 20,000. It is
    /// exposed rather than fixed because the right value follows from the row WIDTH, which only the host
    /// knows.</para>
    ///
    /// <para>It is not a limit on the export: the walk continues until the data runs out. What bounds an
    /// export is EXP-7's row cap, which this adapter deliberately does not implement — see
    /// <see cref="EfCoreExportStream"/>.</para>
    /// </summary>
    public int ChunkSize { get; init; } = 5_000;

    internal void Validate()
    {
        if (ChunkSize <= 0)
        {
            throw new EfCoreCompilationException(
                $"ChunkSize must be positive, got {ChunkSize}. A zero or negative chunk fetches no rows, so the walk " +
                "would terminate on its first chunk and report an empty export as a complete one.");
        }
    }

    internal static readonly EfCoreExportOptions Default = new();
}
