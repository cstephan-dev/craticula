using System.Runtime.CompilerServices;
using Craticula.Core.Export;
using Craticula.Core.Ir;
using Microsoft.EntityFrameworkCore;

namespace Craticula.EntityFrameworkCore;

/// <summary>
/// The <b>export</b> entry point: EXP-1(b)'s columns for every matching row, streamed as
/// <see cref="ExportRow"/>s a writer consumes directly —
/// <c>CsvWriter.WriteAsync(stream, columns, options, destination, ct)</c>.
///
/// <para><b>This type exists so that "an export never OFFSET-walks" is a property of the code rather
/// than a note in a comment.</b> EXP-3 forbids offset paging for an export, and EF compiles
/// <see cref="Queryable.Skip{TSource}"/> to <c>OFFSET</c> — measured, not assumed. The token
/// <c>Skip</c> does not appear in this file, there is no offset in scope for it to pass, and
/// <see cref="EfCoreQueryBuilder.ApplyRowWindow"/> (the one method that would emit one) is never called
/// from here. Two tests hold that: one reads this file's own source for the forbidden token, the other
/// asserts that a chunk's generated SQL contains no <c>OFFSET</c>. A boolean "isExport" flag on a
/// single entry point would have put the same rule somewhere a caller can get wrong, and the symptom is
/// not an error — it is a download that returns exactly the right bytes, quadratically slower, on a
/// dataset large enough that nobody is watching.</para>
///
/// <para><b>What replaces the offset: a keyset walk on the query's own sort tuple</b>
/// (<see cref="EfCoreKeysetPredicate"/>), <see cref="EfCoreExportOptions.ChunkSize"/> rows at a time,
/// each chunk a <c>Take</c> with no <c>Skip</c>. Every chunk after the first adds one predicate — "sorts
/// strictly after the last row I emitted" — which an index on the sort keys can seek to directly instead
/// of counting past. EXP-3 names server-side cursors first and keyset chunking as the fallback for
/// drivers without them; chunking is what this adapter implements, because it is the one that holds for
/// every EF provider and does not depend on a connection staying open across a million-row read.</para>
///
/// <para><b>Streaming is a contract here, matching the writer's own.</b> Rows are yielded as they are
/// read: the memory held at any moment is one chunk's worth of provider buffer plus one row, so a
/// million-row export costs what a ten-row one costs. Because the consumer's <c>await</c> suspends this
/// iterator, a slow destination stops the reader rather than filling memory ahead of it — the same
/// backpressure <c>CsvWriter</c> documents from the other side.</para>
///
/// <para><b>What this deliberately does NOT do: enforce EXP-7's row cap.</b> The TRD sets a default
/// maximum of 1,000,000 exported rows, and <c>CsvWriter</c> correctly declines to enforce it ("it would
/// have to guess whether a truncated file or an error is what the host wants") and points at the
/// adapter. #1645 declines too, and for a reason rather than by omission: nothing in the spec or the
/// fixture suite pins <em>which</em> of those two behaviours the cap has —
/// <c>spec/fixtures/page-export/021</c> documents the number and explicitly enforces nothing — and the
/// two are not interchangeable to a user (a silently truncated CSV is a wrong answer that looks
/// complete). Picking one here would put an unpinned policy in the layer everything else depends on. A
/// host caps it today by counting the rows it forwards, which it is already doing to write them;
/// <see cref="CancellationToken"/> is EXP-7's other half and is implemented.</para>
/// </summary>
public static class EfCoreExportStream
{
    /// <summary>
    /// Streams every row matching <paramref name="request"/>, in the export's own column order.
    ///
    /// <para>The row's cells are an <see cref="ArraySegment{T}"/> over a wider array the projection
    /// filled: the sort keys the keyset walk needs are appended after the export's own columns, so ONE
    /// projection serves both and no row is read twice. The segment costs no copy —
    /// <see cref="ArraySegment{T}"/> is an <see cref="IReadOnlyList{T}"/>, which is what
    /// <see cref="ExportRow"/> wraps — and the consumer sees exactly
    /// <c>EfCoreQueryBuilder.ResolveProjection</c>'s columns, never the bookkeeping ones.</para>
    /// </summary>
    /// <param name="source">The <c>FROM</c> target — a <c>DbSet</c> with its global query filters, or
    /// <see cref="GridSchemaPropertyBag.Query"/>.</param>
    /// <param name="request">The validated IR. Its <see cref="EfCoreQueryRequest.Page"/> must be
    /// EXP-2's <see cref="NonePage"/>; see the exception below.</param>
    /// <param name="semantics">The five engine facts SEM-1/SEM-7/SEC-4/SQL-7 need.</param>
    /// <param name="options">Chunking configuration; <see langword="null"/> takes EXP-3's 5,000-row
    /// default.</param>
    /// <param name="cancellationToken">EXP-7's cancellation hook. Observed between chunks and between
    /// rows, so cancelling stops PULLING rather than only stopping whoever was writing.</param>
    /// <exception cref="EfCoreCompilationException">The request carries a
    /// <see cref="RangePage"/> (use <see cref="EfCoreGridQuery.FetchPageAsync{TRow}"/>), row-group state
    /// (#1622), or any of the shapes <see cref="EfCoreQueryBuilder"/> refuses.</exception>
    public static async IAsyncEnumerable<ExportRow> StreamAsync<TRow>(
        IQueryable<TRow> source,
        EfCoreQueryRequest request,
        EfCoreProviderSemantics semantics,
        EfCoreExportOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(semantics);

        var chunking = options ?? EfCoreExportOptions.Default;
        chunking.Validate();

        // EXP-1/EXP-2: `exportScope: "all"` is the scope this method serves, and the adapter that parsed
        // the request has already resolved it to NonePage. A RangePage means `exportScope: "page"`
        // (spec/fixtures/page-export/007), which is ONE bounded window — the identical query a grid
        // block runs, down to the projection — so it is served by the grid entry point rather than given
        // a Skip here. Refusing keeps this file's no-OFFSET guarantee total instead of true-except-once,
        // which is the only form of it a test can hold.
        if (request.Page is not NonePage)
        {
            throw new EfCoreCompilationException(
                $"An export stream requires EXP-2's NonePage, but this request carries " +
                $"'{request.Page.GetType().Name}'. A page-scoped export (exportScope: 'page') is one bounded window " +
                "and is the same query as a grid block: call EfCoreGridQuery.FetchPageAsync and wrap its rows. This " +
                "method may never apply a row offset (EXP-3), so it has no arm for a row range.");
        }

        var context = EfCoreQueryBuilder.CreateValidatedContext(request, semantics, typeof(TRow));
        var keys = EfCoreQueryBuilder.ResolveSortKeys(request);
        var columns = EfCoreQueryBuilder.ResolveProjection(request);

        // The projection: the export's columns, then the sort keys, in one list. A key that is also an
        // export column is simply read twice — cheaper than a second query, and the alternative (looking
        // up which projected index holds each key) would make the row layout depend on a coincidence
        // between two independent lists.
        var projected = new List<string>(columns.Count + keys.Count);
        projected.AddRange(columns);
        foreach (var key in keys)
        {
            projected.Add(key.Field);
        }

        var boundary = new object?[keys.Count];
        var walked = false;

        // SEM-6 guarantees the LAST key is the stable key — AppendStableKeyTiebreaker either appended it
        // or found the request's own sort already ending in it — which is the fact the whole walk rests
        // on. Named here because the uniqueness check below reads it.
        var stableKeyIndex = keys.Count - 1;

        while (true)
        {
            var filtered = EfCoreQueryBuilder.ApplyFilter(source, request, semantics);

            if (walked)
            {
                if (EfCoreKeysetPredicate.Build<TRow>(keys, boundary, context) is not { } after)
                {
                    // No row can sort after the last one emitted (every key null, every key's nulls
                    // last). The export is complete; issuing a constant-false query to learn that would
                    // be a round trip for nothing.
                    yield break;
                }

                filtered = filtered.Where(after);
            }

            // ChunkSize + 1, and the extra row is a PROBE that is never emitted. Without it the
            // uniqueness check below is blind in the one place a row is actually lost: when two rows
            // share the stable key ACROSS a chunk edge, the boundary predicate ("strictly after the
            // last row emitted") excludes the second one, so it is never fetched and there is nothing
            // left to compare it against. The adjacent-row check alone therefore only sees duplicates
            // that happen to land inside one chunk — which is a function of the chunk size, not of the
            // data, and at the 5,000 default it misses roughly one duplicate pair in 5,000. What it
            // misses is a silently SHORT export that is otherwise perfectly well formed.
            //
            // One extra row per chunk, discarded, is the whole cost.
            var chunk = EfCoreQueryBuilder.ApplyProjection(
                EfCoreQueryBuilder.ApplyOrdering(filtered, keys, context).Take(chunking.ChunkSize + 1),
                request.Schema,
                projected);

            var rowsInChunk = 0;
            var probed = false;

            // The key the previous chunk ended on. A correct walk never meets it again: the boundary predicate is "strictly
            // after" it. Meeting it means the predicate compared the boundary against the stored value in a different form
            // (Bug #2805: SQLite text a host stored as 'T…Z' against EF's own 'yyyy-MM-dd HH:mm:ss' binding), and the walk
            // would restart at the same row for ever once a calendar date holds a chunk's worth of rows.
            var previousChunkEnd = walked ? boundary[stableKeyIndex] : null;

            await foreach (var cells in chunk.AsAsyncEnumerable()
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();

                // SEM-6's uniqueness, checked on the data rather than trusted from the declaration —
                // because the consequence of it being false is a silent one. The boundary predicate is
                // "strictly after the last row emitted", so the walk can never repeat a row or fail to
                // terminate; what it does instead, when two rows share a stable key, is SKIP the second
                // one at whichever chunk edge it lands on. A short export that is otherwise perfectly
                // well formed is exactly the kind of wrong answer this project prefers to fail on.
                //
                // Adjacent rows are the right place to look: the result is sorted, so duplicates of the
                // trailing key are neighbours. It costs one comparison per row, and it cannot produce a
                // false positive — SC-5 declares the key unique, so two equal values are a schema defect
                // whether or not this particular export would have dropped anything.
                var stableKey = cells[columns.Count + stableKeyIndex];

                if (walked && Equals(stableKey, previousChunkEnd))
                {
                    throw RepeatedRow(request.Schema.StableKey, stableKey);
                }

                if (rowsInChunk == chunking.ChunkSize)
                {
                    // The probe row. It is never emitted and never becomes the boundary — its only job
                    // is to be compared, because it is the row the NEXT chunk's predicate would drop if
                    // it duplicates the stable key of the last row emitted here.
                    probed = true;

                    if (Equals(stableKey, boundary[stableKeyIndex]))
                    {
                        throw DuplicateStableKey(request.Schema.StableKey, stableKey);
                    }

                    break;
                }

                // The same defect inside one chunk, where the rows ARE adjacent in the result. Both
                // checks are needed: this one cannot see across a chunk edge, and the probe above only
                // looks at the edge.
                if (rowsInChunk > 0 && Equals(stableKey, boundary[stableKeyIndex]))
                {
                    throw DuplicateStableKey(request.Schema.StableKey, stableKey);
                }

                for (var index = 0; index < keys.Count; index++)
                {
                    boundary[index] = cells[columns.Count + index];
                }

                rowsInChunk++;
                EfCoreValueNormalizer.NormalizeInPlace(cells, columns.Count);
                yield return new ExportRow(new ArraySegment<object?>(cells, 0, columns.Count));
            }

            // No probe row means the engine had nothing beyond this chunk: the ordering is total, so
            // there is no arrangement in which it returns fewer rows than asked for and more remain.
            // Testing the probe rather than `rowsInChunk < ChunkSize` also covers the exactly-full last
            // chunk, which would otherwise cost one more empty round trip.
            if (!probed)
            {
                yield break;
            }

            walked = true;
        }
    }

    /// <summary>
    /// SEM-6's uniqueness, reported rather than trusted from the declaration — because the consequence
    /// of it being false is silent. The boundary predicate is "strictly after the last row emitted", so
    /// the walk can never repeat a row or fail to terminate; what it does instead, when two rows share a
    /// stable key, is <b>skip</b> the second one, producing a short export that is otherwise perfectly
    /// well formed.
    ///
    /// <para>It cannot produce a false positive: SC-5 declares the key unique, so two equal values are a
    /// schema defect whether or not this particular export would have dropped anything.</para>
    /// </summary>
    private static EfCoreCompilationException RepeatedRow(string stableKeyField, object? value) =>
        new($"The export met the row with stable key '{stableKeyField}' = '{value}' again after emitting it: the keyset predicate " +
            "did not exclude the row the previous chunk ended on, so the walk would repeat for ever. The usual cause is a sort key " +
            "whose bound parameter is written differently from the stored value (SQLite datetimes: the host must map every " +
            "datetime to and from the stored 'yyyy-MM-ddTHH:mm:ss.fffZ' text, see EfCoreProviderSemantics). Stopping instead of " +
            "returning a long, wrong export.");

    private static EfCoreCompilationException DuplicateStableKey(string stableKeyField, object? value) =>
        new($"Two rows share the stable key '{stableKeyField}' (value '{value}'). SC-5 declares that key " +
            "unique and EXP-3's keyset chunking relies on it: it is the last key of every ordering this " +
            "adapter emits (SEM-6), and it is what makes the sort a TOTAL order that a boundary predicate " +
            "can split. Without uniqueness the walk does not loop or repeat — it silently omits the " +
            "duplicate, producing a short export that looks complete. Fix the schema's stableKey; a " +
            "different chunk size only moves which row is lost.");
}
