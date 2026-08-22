using System.Data;
using System.Runtime.CompilerServices;
using Craticula.Core.Export;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Sql;
using Dapper;

namespace Craticula.Dapper;

/// <summary>
/// The <b>export</b> entry point: EXP-1(b)'s columns for every matching row, streamed as
/// <see cref="ExportRow"/>s a writer consumes directly — <c>CsvWriter.WriteAsync(stream, columns,
/// options, destination, ct)</c>.
///
/// <para><b>This type exists so that "an export never OFFSET-walks" is a property of the code rather
/// than a note in a comment</b> — <c>Craticula.EntityFrameworkCore.EfCoreExportStream</c>'s own
/// argument, restated for a SQL-text compiler: EXP-3 forbids offset paging for an export, and this
/// method compiles every chunk with <see cref="RangePage.StartRow"/> pinned to the LITERAL <c>0</c> —
/// only the <c>WHERE</c> clause's keyset boundary advances between chunks, never the paging clause's
/// skip count. A boolean "isExport" flag on a single entry point would put that rule somewhere a caller
/// can get wrong; two types make it structural, and a test reads this file's own source to prove
/// <see cref="RangePage.StartRow"/> is never assigned anything but <c>0</c>.</para>
///
/// <para><b>What replaces the offset: a keyset walk on the query's own sort tuple</b>
/// (<see cref="DapperKeysetPredicate"/>), <see cref="DapperExportOptions.ChunkSize"/> rows at a time.
/// Each chunk is compiled AS A NEW <see cref="SqlQueryRequest"/> — <c>Craticula.Sql.SqlQueryBuilder</c>
/// is a stateless IR→SQL compiler with no notion of "the next chunk", so this method owns the whole
/// loop: build the boundary predicate as ORDINARY <see cref="FilterNode"/> IR
/// (<see cref="DapperKeysetPredicate.Build"/>), AND it into the request's own filter via the EXISTING
/// <see cref="FilterTreeMerge.And"/>, and recompile via <see cref="SqlQueryBuilder.Build"/> with
/// <c>Page = RangePage { StartRow = 0, EndRow = chunkSize + 1 }</c>.</para>
///
/// <para><b>Streaming is a contract here, matching the writer's own.</b> Each chunk is read off a raw
/// <see cref="IDataReader"/> row by row rather than buffered into a list first, so the memory held at
/// any moment is one chunk's worth of provider buffer plus one row — a million-row export costs what a
/// ten-row one costs, chunk size aside.</para>
///
/// <para><b>Scope, per <c>spec/semantics.md</c>'s EXP-1 v1 scoping</b> ("Group-structured and pivot
/// exports are out of scope for v1"): this method refuses a request carrying a
/// <see cref="GroupSpec"/> — <c>AgGridRequestParser</c> already discards group state on any export it
/// resolves, so a caller reaching this method with one has built the request by hand.</para>
///
/// <para><b>What this deliberately does NOT do: enforce EXP-7's row cap.</b> The TRD sets a default
/// maximum of 1,000,000 exported rows; <c>CsvWriter</c> and
/// <c>Craticula.EntityFrameworkCore.EfCoreExportStream</c> both decline to enforce it, for the same
/// reason recorded there — nothing in the spec or the fixture suite pins whether the cap truncates or
/// fails, and picking one here would bake an unpinned policy into the layer everything else depends on.
/// <see cref="CancellationToken"/> is EXP-7's other half and is implemented.</para>
/// </summary>
public static class DapperExportStream
{
    /// <summary>
    /// Streams every row matching <paramref name="request"/>, in the export's own column order.
    /// </summary>
    /// <param name="connection">An already-open connection. Never opened or closed here.</param>
    /// <param name="request">The validated IR. Its <see cref="SqlQueryRequest.Page"/> must be EXP-2's
    /// <see cref="NonePage"/>, and its <see cref="SqlQueryRequest.Group"/> must be
    /// <see langword="null"/>; see the exceptions below.</param>
    /// <param name="dialect">The target engine's rendering rules.</param>
    /// <param name="options">Chunking configuration; <see langword="null"/> takes EXP-3's 5,000-row
    /// default.</param>
    /// <param name="cancellationToken">EXP-7's cancellation hook. Observed between rows, so cancelling
    /// stops PULLING rather than only stopping whoever was writing.</param>
    /// <exception cref="DapperCompilationException">The request carries a <see cref="RangePage"/> (use
    /// <see cref="DapperGridQuery.FetchPageAsync"/>), a <see cref="GroupSpec"/> (out of scope for v1), an
    /// empty export projection, or a non-positive <see cref="DapperExportOptions.ChunkSize"/>; or the
    /// schema's <see cref="GridSchema.StableKey"/> is not actually unique in the data.</exception>
    public static async IAsyncEnumerable<ExportRow> StreamAsync(
        IDbConnection connection,
        SqlQueryRequest request,
        ISqlDialect dialect,
        DapperExportOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(dialect);

        var chunking = options ?? DapperExportOptions.Default;
        chunking.Validate();

        // EXP-1/EXP-2: `exportScope: "all"` is the scope this method serves, and the adapter that parsed
        // the request has already resolved it to NonePage. A RangePage means `exportScope: "page"`, one
        // bounded window — the identical query a grid block runs — so it is served by the grid entry
        // point rather than given a chunked walk here. Refusing keeps this file's no-OFFSET guarantee
        // total instead of true-except-once, which is the only form of it a test can hold.
        if (request.Page is not NonePage)
        {
            throw new DapperCompilationException(
                $"An export stream requires EXP-2's NonePage, but this request carries " +
                $"'{request.Page.GetType().Name}'. A page-scoped export (exportScope: 'page') is one bounded " +
                "window and is the same query as a grid block: call DapperGridQuery.FetchPageAsync and wrap its " +
                "rows. This method may never apply a row offset (EXP-3), so it has no arm for a row range.");
        }

        // EXP-1 v1 scoping: "Group-structured and pivot exports are out of scope for v1." Refused rather
        // than silently flattened — AgGridRequestParser already discards group state on any export it
        // resolves, so a caller reaching this method with one has built the request by hand, and there is
        // no defined shape here for what a grouped export's row stream would even contain.
        if (request.Group is not null)
        {
            throw new DapperCompilationException(
                "An export stream requires an ungrouped request (spec/semantics.md's EXP-1 v1 scoping: " +
                "group-structured and pivot exports are out of scope). AgGridRequestParser already discards " +
                "group state on any export it resolves — a caller reaching this method with a GroupSpec has " +
                "built the request by hand.");
        }

        var keys = SemanticsResolver.AppendStableKeyTiebreaker(request.Sort, request.Schema.StableKey);

        // SEM-6/SEM-1 collation hazard, UNCONDITIONAL on the stableKey's own declared type -- an earlier
        // version of this guard fired only for a MINTED tiebreaker, on the reasoning that a sort already
        // ending in the stableKey keeps "the user's own key", which both resolvers were assumed to treat
        // identically. Measured wrong on two counts, both closing to the same conclusion:
        //
        // (a) "Both sides agree" is not sufficient even when they DO agree. A case-INSENSITIVE
        //     collation is not a TOTAL order -- 'aB3' and 'Ab3' compare EQUAL under it -- and SEM-6's
        //     whole walk depends on the stableKey producing one. Two rows tying under a folded
        //     collation are adjacent in the sorted result but distinct rows; the boundary's strict
        //     "after" comparison treats the second as NOT after the first (since they compare equal)
        //     and silently drops it -- reproduced on this file's own SQLite harness with ids "aB3"/"Ab3"
        //     and no exception anywhere, because the duplicate-detector compares CLR strings by
        //     Equals, which correctly says they're DIFFERENT (masking that the SQL comparison said
        //     they're the SAME). This applies whether or not a tiebreaker was minted.
        //
        // (b) The "declare it caseSensitivity: sensitive" escape hatch this guard used to offer does
        //     not reliably close the gap either. ResolveCaseSensitivity's Sensitive answer is
        //     (Sensitive, None) -- no explicit collation mechanism, relying on the column's own default
        //     comparison -- and on Oracle/SQL Server specifically, SC-3's Sensitive spelling for an
        //     ORDINARY comparison DOES emit an explicit COLLATE clause (their typical default column
        //     collation is itself case-insensitive), while a MINTED tiebreaker's ORDER BY stays
        //     (NotApplicable, None) regardless -- i.e. still no explicit COLLATE. Declaring the field
        //     sensitive can therefore swap WHICH side disagrees rather than remove the disagreement.
        //
        // No per-dialect, per-declaration carve-out is safe today, so the refusal is unconditional:
        // ANY string stableKey is out of scope for keyset-chunked export until a dedicated design (an
        // exact, binary comparison this predicate can ask for independent of SEM-1's per-field
        // classification) closes both (a) and (b) together.
        if (request.Schema.Fields[request.Schema.StableKey].Type == FieldType.String)
        {
            throw new DapperCompilationException(
                $"The schema's stableKey '{request.Schema.StableKey}' is a text column. EXP-3's keyset " +
                "chunking depends on the stableKey producing a TOTAL order (SEM-6), which SEM-1's " +
                "per-field case-sensitivity classification cannot guarantee agrees between the compiled " +
                "ORDER BY and this predicate's boundary comparison across every dialect and declaration -- " +
                "a case-insensitive collation is not total (two differently-cased values can compare " +
                "equal), which silently drops one of a tied pair rather than throwing. A text stableKey " +
                "is not supported for keyset-chunked export today; use an int64/decimal/date/datetime " +
                "stableKey, or fetch this export a page at a time via DapperGridQuery.FetchPageAsync " +
                "instead.");
        }

        // EXP-3/SEM-2 hazard, checked once before the walk starts rather than left latent: a sort key
        // declared FieldType.String is walked via DapperKeysetPredicate's Blank/NotBlank comparisons,
        // which SqlQueryBuilder.RenderBlank renders as `(col IS NULL OR col = '')` on every dialect
        // except Oracle (ISqlDialect.EmptyStringIsNull). That is SEM-2's own, deliberate business
        // semantics for an ordinary "is this blank" filter condition -- but the compiled ORDER BY treats
        // NULL and '' as distinct sort positions, so a boundary built this way disagrees with the
        // ordering whenever the column actually holds an empty string: a NULLS LAST boundary's "OR
        // Blank" disjunct re-matches the '' row itself (duplicate output, or a NULLS LAST walk that
        // never terminates), and a NULLS FIRST boundary's NotBlank silently drops it. The IR has no
        // exact IS NULL/IS NOT NULL primitive independent of blank/notBlank's text-aware semantics, so
        // this shape cannot be walked correctly today -- refused with a named cause rather than left to
        // fail as a silent short export or a hang. Scoped to the request's own filter, not the whole
        // table: a filter that already excludes every '' row makes the hazard moot for this export.
        if (!dialect.EmptyStringIsNull)
        {
            foreach (var key in keys)
            {
                if (request.Schema.Fields[key.Field].Type != FieldType.String)
                {
                    continue;
                }

                if (await ContainsEmptyString(connection, request, dialect, key.Field, cancellationToken).ConfigureAwait(false))
                {
                    throw new DapperCompilationException(
                        $"Sort key '{key.Field}' is a text column and at least one row (within this export's " +
                        "own filter scope) holds an empty string (''). EXP-3's keyset boundary tests null-ness " +
                        "via SEM-2's blank/notBlank operators, which intentionally treat '' the same as NULL " +
                        $"for an ordinary filter condition on the '{dialect.Name}' dialect -- but the compiled " +
                        "ORDER BY treats them as distinct sort positions, so the boundary and the ordering " +
                        "disagree for this column: rows can be dropped, duplicated, or (a NULLS LAST sort) the " +
                        "walk can fail to terminate. This shape cannot be exported via keyset chunking until " +
                        "the IR gains an exact IS NULL/IS NOT NULL primitive independent of blank/notBlank.");
                }
            }
        }

        var columns = request.Projection ?? request.Schema.FieldOrder;

        if (columns.Count == 0)
        {
            throw new DapperCompilationException(
                "A request carries an empty EXP-1(b) export projection. Leave Projection null for the " +
                "schema-order default; AgGridRequestParser rejects an empty 'exportColumns' at the wire.");
        }

        // The projection: the export's own columns, then the keyset walk's sort keys, in one list — ONE
        // execution serves both, and a key that is also an export column is simply read twice, cheaper
        // than a second query. Mirrors EfCoreExportStream's identical reasoning.
        var projected = new List<string>(columns.Count + keys.Count);
        projected.AddRange(columns);
        foreach (var key in keys)
        {
            projected.Add(key.Field);
        }

        var columnTypes = ResolveTypes(columns, request.Schema);
        var keyTypes = ResolveTypes([.. keys.Select(key => key.Field)], request.Schema);

        var boundary = new object?[keys.Count];
        var walked = false;

        // SEM-6 guarantees the LAST key is the stable key — AppendStableKeyTiebreaker either appended it
        // or found the request's own sort already ending in it — which is the fact the whole walk rests
        // on.
        var stableKeyIndex = keys.Count - 1;

        while (true)
        {
            FilterNode? filter = request.Filter;

            if (walked)
            {
                if (DapperKeysetPredicate.Build(keys, boundary) is not { } after)
                {
                    // No row can sort after the last one emitted. The export is complete; issuing a
                    // constant-false query to learn that would be a round trip for nothing.
                    yield break;
                }

                filter = FilterTreeMerge.And(filter, after);
            }

            // ChunkSize + 1, and the extra row is a PROBE that is never emitted — see the loop body for
            // why it is what lets a duplicate stable key be detected at all.
            // StartRow is pinned to the literal 0 on EVERY chunk: only `filter` grows between
            // iterations, never the paging clause's skip count (EXP-3's own guarantee).
            var chunkRequest = request with
            {
                Filter = filter,
                Page = new RangePage { StartRow = 0, EndRow = chunking.ChunkSize + 1 },
                Projection = projected
            };

            var query = SqlQueryBuilder.Build(chunkRequest, dialect);
            var (sql, parameters) = DapperParameterBinder.Prepare(query);
            var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);

            var rowsInChunk = 0;
            var probed = false;

            using (var reader = await connection.ExecuteReaderAsync(command).ConfigureAwait(false))
            {
                while (await DapperGridQuery.ReadRowAsync(reader, cancellationToken).ConfigureAwait(false))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    // SEM-6's uniqueness, checked on the data rather than trusted from the declaration —
                    // the consequence of it being false is silent. The boundary predicate is "strictly
                    // after the last row emitted", so the walk can never repeat a row or fail to
                    // terminate; a non-unique stableKey instead SKIPS the second of two tied rows at
                    // whichever chunk edge it lands on. Adjacent rows are the right place to look: the
                    // result is sorted, so duplicates of the trailing key are neighbours.
                    var stableKey = DapperValueReader.Read(reader, columns.Count + stableKeyIndex, keyTypes[stableKeyIndex]);

                    if (rowsInChunk == chunking.ChunkSize)
                    {
                        // The probe row. Never emitted and never becomes the boundary — its only job is
                        // to be compared, because it is the row the NEXT chunk's predicate would drop if
                        // it duplicates the stable key of the last row emitted here.
                        probed = true;

                        if (Equals(stableKey, boundary[stableKeyIndex]))
                        {
                            throw DuplicateStableKey(request.Schema.StableKey, stableKey);
                        }

                        break;
                    }

                    // The same defect inside one chunk, where the rows ARE adjacent in the result.
                    if (rowsInChunk > 0 && Equals(stableKey, boundary[stableKeyIndex]))
                    {
                        throw DuplicateStableKey(request.Schema.StableKey, stableKey);
                    }

                    var cells = new object?[columns.Count];
                    for (var index = 0; index < columns.Count; index++)
                    {
                        cells[index] = DapperValueReader.Read(reader, index, columnTypes[index]);
                    }

                    for (var index = 0; index < keys.Count; index++)
                    {
                        boundary[index] = DapperValueReader.Read(reader, columns.Count + index, keyTypes[index]);
                    }

                    rowsInChunk++;
                    yield return new ExportRow(cells);
                }
            }

            // No probe row means the engine had nothing beyond this chunk: the ordering is total, so
            // there is no arrangement in which it returns fewer rows than asked for and more remain.
            if (!probed)
            {
                yield break;
            }

            walked = true;
        }
    }

    /// <summary>
    /// Whether any row within <paramref name="request"/>'s own filter scope has an empty string in
    /// <paramref name="field"/> — a single bounded existence probe (<c>LIMIT 1</c>), run once before the
    /// walk starts, never per chunk. ANDs the check into the request's OWN filter rather than scanning
    /// the whole table: a filter that already excludes every <c>''</c> row makes the hazard moot.
    /// </summary>
    private static async Task<bool> ContainsEmptyString(
        IDbConnection connection, SqlQueryRequest request, ISqlDialect dialect, string field, CancellationToken cancellationToken)
    {
        var probeRequest = request with
        {
            Filter = FilterTreeMerge.And(
                request.Filter, new ComparisonNode { Field = field, Op = ComparisonOperator.Eq, Value = new StringValue { Value = "" } }),
            // null, not []: ir.schema.json's sort.minItems:1 rejects an empty-but-present array —
            // omitting it entirely is how "no sort" is expressed, and this existence probe has none.
            Sort = null,
            Page = new RangePage { StartRow = 0, EndRow = 1 },
            Projection = [field]
        };

        var query = SqlQueryBuilder.Build(probeRequest, dialect);
        var (sql, parameters) = DapperParameterBinder.Prepare(query);
        var command = new CommandDefinition(sql, parameters, cancellationToken: cancellationToken);

        using var reader = await connection.ExecuteReaderAsync(command).ConfigureAwait(false);
        return await DapperGridQuery.ReadRowAsync(reader, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Which <see cref="FieldType"/> each of <paramref name="fields"/> declares — every entry
    /// here is a real schema field (an export column or a sort key), unlike the grid path's column list,
    /// which can also carry an aggregate's output name.</summary>
    private static FieldType[] ResolveTypes(IReadOnlyList<string> fields, GridSchema schema)
    {
        var types = new FieldType[fields.Count];
        for (var index = 0; index < fields.Count; index++)
        {
            types[index] = schema.Fields.TryGetValue(fields[index], out var field)
                ? field.Type
                : throw new DapperCompilationException($"Field '{fields[index]}' is not declared in the GridSchema.");
        }

        return types;
    }

    /// <summary>
    /// SEM-6's uniqueness, reported rather than trusted from the declaration — because the consequence
    /// of it being false is silent. The boundary predicate is "strictly after the last row emitted", so
    /// the walk can never repeat a row or fail to terminate; what it does instead, when two rows share a
    /// stable key, is <b>skip</b> the second one, producing a short export that is otherwise perfectly
    /// well formed.
    /// </summary>
    private static DapperCompilationException DuplicateStableKey(string stableKeyField, object? value) =>
        new($"Two rows share the stable key '{stableKeyField}' (value '{(value is null ? "null" : DapperValueReader.Truncate(value))}'). " +
            "SC-5 declares that key " +
            "unique and EXP-3's keyset chunking relies on it: it is the last key of every ordering this " +
            "adapter emits (SEM-6), and it is what makes the sort a TOTAL order that a boundary predicate " +
            "can split. Without uniqueness the walk does not loop or repeat — it silently omits the " +
            "duplicate, producing a short export that looks complete. Fix the schema's stableKey; a " +
            "different chunk size only moves which row is lost.");
}
