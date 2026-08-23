using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Craticula.OpenSearch.Client;

/// <summary>
/// PERF-2's deep-paging cursor cache — a PURE storage interface with no <c>IOpenSearchLowLevelClient</c>
/// dependency of its own, so it stays trivially testable without a live cluster (a fake/spy
/// implementation is exactly how <c>OpenSearchGridQueryTests</c> proves the adapter's own
/// close-on-evict hygiene, below).
///
/// <para><b>One cache, one key shape, two cursor payload shapes.</b> <see cref="SearchCursorEntry.Cursor"/>
/// is a <see cref="JsonNode"/> because it holds either of PERF-2's two structurally different paging
/// problems' own boundary: a <see cref="JsonArray"/> of the last row's <c>sort</c> values (OSE-10's
/// row-level deep paging) or a <see cref="JsonObject"/> <c>after_key</c> (OSE-11(d)'s grouped-level
/// paging). One abstraction serves both rather than two parallel mechanisms — see
/// <see cref="OpenSearchGridQuery"/>'s own remarks for how each one is produced and consumed.</para>
///
/// <para><b>Invalidation is implicit by construction, not a method on this interface.</b> A sort or
/// filter change compiles to a DIFFERENT <c>{query, sort}</c>/<c>{query, composite.sources}</c> shape,
/// which <see cref="OpenSearchGridQuery.ComputeCursorKey"/> hashes to a DIFFERENT cache key — so a
/// changed request is simply a cache miss, with no explicit "invalidate the old one" call needed for
/// CORRECTNESS. What still needs explicit handling is resource HYGIENE: an abandoned cursor's PIT costs
/// the cluster memory until its own <c>keep_alive</c> lapses, so the ADAPTER (never this cache) is what
/// best-effort closes the PIT of any entry it overwrites (a fresh walk replacing a request that moved
/// backward) or evicts (<see cref="RemoveAsync"/>) — which is exactly why <see cref="RemoveAsync"/>
/// hands the removed entry BACK, rather than returning nothing the way a plain
/// <c>ConcurrentDictionary.TryRemove</c>-shaped API might: the caller needs the old
/// <see cref="SearchCursorEntry.PitId"/> to close it.</para>
/// </summary>
public interface ISearchCursorCache
{
    /// <summary>
    /// The cached entry for <paramref name="cacheKey"/>, or <see langword="null"/> when there is none —
    /// including an entry that IS (or, for an implementation that self-cleans on read, WAS) still
    /// physically present but has passed its own <see cref="SearchCursorEntry.ExpiresAt"/>, which reads
    /// as absent for reuse purposes either way. A NOT-expired entry that is simply behind the request
    /// (<see cref="SearchCursorEntry.UpToRow"/> too small) is still returned as-is — <see cref="RemoveAsync"/>
    /// is what a caller falls through to for THAT case, to best-effort close its still-live
    /// <see cref="SearchCursorEntry.PitId"/> (see <see cref="InMemorySearchCursorCache"/>'s own remarks
    /// for why an EXPIRED entry's own PIT has, by construction, already lapsed server-side by the time
    /// this method would report it absent, which is what makes self-cleaning it here safe).
    /// </summary>
    Task<SearchCursorEntry?> TryGetAsync(string cacheKey, CancellationToken cancellationToken);

    /// <summary>Stores <paramref name="entry"/> under <paramref name="cacheKey"/>, replacing whatever
    /// was there. Does not itself close anything — see this interface's own remarks on why that is the
    /// ADAPTER's job.</summary>
    Task SetAsync(string cacheKey, SearchCursorEntry entry, CancellationToken cancellationToken);

    /// <summary>Removes and returns whatever entry is physically stored under <paramref name="cacheKey"/>
    /// — expired or not — or <see langword="null"/> when there is none, so the caller can best-effort
    /// close its <see cref="SearchCursorEntry.PitId"/> (a grouped-level entry's <see cref="SearchCursorEntry.PitId"/>
    /// is the empty string, since <c>composite.after</c> needs no PIT at all — see
    /// <see cref="OpenSearchGridQuery"/>'s own remarks; closing an empty id is a no-op).</summary>
    Task<SearchCursorEntry?> RemoveAsync(string cacheKey, CancellationToken cancellationToken);
}

/// <summary>
/// One cached deep-paging cursor. <paramref name="PitId"/> is the empty string for a grouped-level
/// entry (no PIT applies to <c>composite.after</c> paging — see <see cref="ISearchCursorCache"/>'s own
/// remarks).
/// </summary>
/// <param name="PitId">The open PIT this cursor was read through (row-level paging), or the empty
/// string (grouped-level paging).</param>
/// <param name="UpToRow">How many rows (or buckets) have been consumed from the start of this logical
/// walk — the position <paramref name="Cursor"/> is the boundary AFTER.</param>
/// <param name="Cursor">The last row's <c>sort</c> array, or the last-kept bucket's own composite
/// <c>key</c> object.</param>
/// <param name="ExpiresAt">When this entry (and the PIT/composite window it references) should be
/// treated as stale — refreshed on every use, tracking
/// <see cref="OpenSearchDeepPagingOptions.PitKeepAlive"/>.</param>
public sealed record SearchCursorEntry(string PitId, int UpToRow, JsonNode Cursor, DateTimeOffset ExpiresAt);

/// <summary>
/// The default <see cref="ISearchCursorCache"/>: an in-process <see cref="ConcurrentDictionary{TKey,TValue}"/>,
/// expire-on-read against <see cref="SearchCursorEntry.ExpiresAt"/>, self-cleaning on read rather than a
/// background sweep timer.
///
/// <para><b>Why no proactive BACKGROUND sweep timer, but why <see cref="TryGetAsync"/> DOES clean up on
/// read.</b> A timer here would not shorten a PIT's own SERVER-SIDE lifetime — that lapses on its own
/// <c>keep_alive</c> regardless of what this process does (this cache has no client to call
/// <c>DeletePit</c> with in the first place; see <see cref="ISearchCursorCache"/>'s own remarks on why
/// closing is the ADAPTER's job) — so a timer would only ever save THIS process's own dictionary memory.
/// That cost is real, though, and review correctly flagged this doc's own earlier claim ("a few hundred
/// bytes") as wrong: <see cref="SearchCursorEntry.PitId"/> is commonly KILOBYTES (a PIT id encodes
/// per-shard base64 state, growing with shard count), plus the <see cref="SearchCursorEntry.Cursor"/>
/// node — genuinely unbounded growth for a long-lived host serving many short-lived grid sessions that
/// never come back to clean up after themselves. Rather than a background sweep timer (unnecessary
/// complexity for a first cut, exactly as <c>docs/feat/1648/context.md</c> calls out),
/// <see cref="TryGetAsync"/> itself now removes an EXPIRED entry the moment it observes one — bounding
/// growth to "entries touched since they last expired" at zero extra cost over the read that was
/// happening anyway.</para>
///
/// <para><b>Why this does NOT reopen the "TryGetAsync must not remove a stale row" concern below.</b>
/// The adapter's own cold-start branch relies on <see cref="RemoveAsync"/> — never
/// <see cref="TryGetAsync"/> — to hand back a still-closeable PIT for an entry that is present but
/// BEHIND the request (not yet expired, <see cref="SearchCursorEntry.UpToRow"/> too small): that case is
/// untouched here, since <see cref="TryGetAsync"/> only ever removes when
/// <see cref="SearchCursorEntry.ExpiresAt"/> has ALREADY passed. And for a genuinely EXPIRED entry, the
/// PIT it names has, by construction, ALSO already lapsed server-side by the time local expiry is
/// observed (<see cref="SearchCursorEntry.ExpiresAt"/> is set from the exact same
/// <see cref="OpenSearchDeepPagingOptions.PitKeepAlive"/> duration the PIT's own server-side
/// <c>keep_alive</c> uses) — so the adapter's own best-effort close on an expired entry was already a
/// near-certain no-op before this change; self-cleaning on read does not give up anything the adapter
/// could otherwise still have collected.</para>
///
/// <para><b>Why <see cref="TryGetAsync"/> and <see cref="RemoveAsync"/> still answer "is this expired?"
/// differently for a NON-expired entry.</b> <see cref="TryGetAsync"/> reports an entry as absent when
/// EITHER it is missing OR it has expired — right for its only caller, which asks "can I reuse this
/// cursor?" — while a present-but-BEHIND entry (not expired, just <see cref="SearchCursorEntry.UpToRow"/>
/// too small for the request) is still returned, exactly as before: the adapter's own logic is what
/// decides to fall through to <see cref="RemoveAsync"/> for that case (see this type's own remarks
/// above), and this self-cleaning change never touches that path.</para>
/// </summary>
public sealed class InMemorySearchCursorCache : ISearchCursorCache
{
    private readonly ConcurrentDictionary<string, SearchCursorEntry> entries = new();
    private readonly TimeProvider clock;

    public InMemorySearchCursorCache(TimeProvider? clock = null) => this.clock = clock ?? TimeProvider.System;

    public Task<SearchCursorEntry?> TryGetAsync(string cacheKey, CancellationToken cancellationToken)
    {
        if (!entries.TryGetValue(cacheKey, out var entry))
        {
            return Task.FromResult<SearchCursorEntry?>(null);
        }

        if (entry.ExpiresAt > clock.GetUtcNow())
        {
            return Task.FromResult<SearchCursorEntry?>(entry);
        }

        // Self-cleaning on read (review finding: unbounded growth otherwise) — a compare-and-remove
        // (not a bare key removal) so a concurrent SetAsync racing this read is never clobbered: this
        // only removes the SAME entry instance just observed as expired, never whatever a racing writer
        // may have replaced it with in between.
        ((ICollection<KeyValuePair<string, SearchCursorEntry>>)entries)
            .Remove(new KeyValuePair<string, SearchCursorEntry>(cacheKey, entry));

        return Task.FromResult<SearchCursorEntry?>(null);
    }

    public Task SetAsync(string cacheKey, SearchCursorEntry entry, CancellationToken cancellationToken)
    {
        entries[cacheKey] = entry;
        return Task.CompletedTask;
    }

    public Task<SearchCursorEntry?> RemoveAsync(string cacheKey, CancellationToken cancellationToken) =>
        Task.FromResult(entries.TryRemove(cacheKey, out var removed) ? removed : null);
}
