using System.Runtime.CompilerServices;
using Craticula.Core.Export;
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.Mongo;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Craticula.MongoDB.Driver;

/// <summary>
/// The <b>export</b> entry point: EXP-1(b)'s columns for every matching document, streamed as
/// <see cref="ExportRow"/>s a writer consumes directly — <c>CsvWriter.WriteAsync(stream, columns,
/// options, destination, ct)</c>.
///
/// <para><b>No keyset chunking, and this is a scope decision worth recording explicitly rather than a
/// silent omission — the one place this adapter genuinely simplifies on its SQL-executing siblings.</b>
/// <c>Craticula.Dapper.DapperExportStream</c> and <c>Craticula.EntityFrameworkCore.EfCoreExportStream</c>
/// both walk a keyset boundary in <see cref="GridSchema.StableKey"/>-sized chunks because EXP-3 forbids
/// OFFSET-paging a SQL export and a STATELESS SQL-text compiler recompiled per chunk has no other way to
/// avoid re-scanning rows it already returned. Neither hazard exists on this execution model. Verified
/// directly in <c>Craticula.Mongo.MongoPipelineBuilder</c>'s own <c>$skip</c>/<c>$limit</c>-emission
/// method (<c>AppendPagingStages</c>): under <see cref="NonePage"/> — EXP-2's export scope — it emits
/// NEITHER stage at all, so the compiled export pipeline is unbounded by construction; there is no
/// offset to walk away from in the first place. And the driver's own
/// <see cref="IAsyncCursor{TDocument}"/> streams through as many <c>getMore</c> batches as the result
/// needs regardless of its size, with no "buffer everything" code path to fall into by accident, and no
/// equivalent of SQL's <c>OFFSET</c> cost model to guard against. Consuming the driver's cursor with no
/// pagination at all therefore satisfies EXP-3 completely for this adapter — <b>do not port
/// <c>Craticula.Dapper.DapperKeysetPredicate</c>'s machinery here; there is nothing on this execution
/// model for it to guard against.</b></para>
///
/// <para><b>Streaming is a contract here, matching the writer's own.</b> Each document is read off the
/// driver's own <see cref="IAsyncCursor{TDocument}"/> batch by batch rather than buffered into a list
/// first, so the memory held at any moment is one <c>getMore</c> batch's worth of documents plus one
/// row — a million-document export costs what a ten-document one costs, batch size aside.</para>
///
/// <para><b>Scope, per <c>spec/semantics.md</c>'s EXP-1 v1 scoping</b> ("Group-structured and pivot
/// exports are out of scope for v1"): this method refuses a request carrying a <see cref="GroupSpec"/> —
/// <c>Craticula.AgGrid.AgGridRequestParser</c> already discards group state on any export it resolves,
/// so a caller reaching this method with one has built the request by hand.</para>
///
/// <para><b>What this deliberately does NOT do: enforce EXP-7's row cap.</b> Same reasoning
/// <c>DapperExportStream</c>/<c>EfCoreExportStream</c> both give: nothing in the spec or the fixture
/// suite pins whether the cap truncates or fails, and picking one here would bake an unpinned policy
/// into the layer everything else depends on. <see cref="CancellationToken"/> is EXP-7's other half and
/// is implemented, observed once per document pulled off the cursor.</para>
/// </summary>
public static class MongoExportStream
{
    /// <summary>
    /// Streams every document matching <paramref name="request"/>, in the export's own column order.
    /// </summary>
    /// <param name="database">An already-open database handle. Never opened or closed here.</param>
    /// <param name="request">The validated IR. Its <see cref="MongoQueryRequest.Page"/> must be EXP-2's
    /// <see cref="NonePage"/>, and its <see cref="MongoQueryRequest.Group"/> must be
    /// <see langword="null"/>; see the exceptions below.</param>
    /// <param name="cancellationToken">EXP-7's cancellation hook. Observed between documents, so
    /// cancelling stops PULLING rather than only stopping whoever was writing.</param>
    /// <exception cref="MongoDriverCompilationException">The request carries a <see cref="RangePage"/> (use
    /// <see cref="MongoGridQuery.FetchPageAsync"/>), a <see cref="GroupSpec"/> (out of scope for v1), or
    /// an empty export projection.</exception>
    public static async IAsyncEnumerable<ExportRow> StreamAsync(
        IMongoDatabase database,
        MongoQueryRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(request);

        // EXP-1/EXP-2: `exportScope: "all"` is the scope this method serves. A RangePage means
        // `exportScope: "page"`, one bounded window — the identical aggregation a grid block runs — so
        // it is served by the grid entry point rather than run here unbounded. Refusing keeps this
        // file's "no row window, ever" property total instead of true-except-once, which is the only
        // form of it a structural test can hold (see this package's own test suite).
        if (request.Page is not NonePage)
        {
            throw new MongoDriverCompilationException(
                $"An export stream requires EXP-2's NonePage, but this request carries " +
                $"'{request.Page.GetType().Name}'. A page-scoped export (exportScope: 'page') is one bounded " +
                "window and is the same aggregation as a grid block: call MongoGridQuery.FetchPageAsync and " +
                "wrap its rows. This method may never apply a row window (EXP-3), so it has no arm for a row " +
                "range.");
        }

        // EXP-1 v1 scoping: "Group-structured and pivot exports are out of scope for v1." Refused rather
        // than silently flattened — AgGridRequestParser already discards group state on any export it
        // resolves, so a caller reaching this method with one has built the request by hand, and there is
        // no defined shape here for what a grouped export's document stream would even contain.
        if (request.Group is not null)
        {
            throw new MongoDriverCompilationException(
                "An export stream requires an ungrouped request (spec/semantics.md's EXP-1 v1 scoping: " +
                "group-structured and pivot exports are out of scope). AgGridRequestParser already discards " +
                "group state on any export it resolves — a caller reaching this method with a GroupSpec has " +
                "built the request by hand.");
        }

        var columns = request.Projection ?? request.Schema.FieldOrder;

        if (columns.Count == 0)
        {
            throw new MongoDriverCompilationException(
                "A request carries an empty EXP-1(b) export projection. Leave Projection null for the " +
                "schema-order default; AgGridRequestParser rejects an empty 'exportColumns' at the wire.");
        }

        var effective = request with { Projection = columns };
        var query = MongoPipelineBuilder.Build(effective);
        var types = ResolveTypes(columns, effective.Schema);

        var collection = database.GetCollection<BsonDocument>(query.Collection);
        var stages = MongoPipelineTranslator.Translate(query.Pipeline);
        var options = MongoPipelineTranslator.Options(query);

        using var cursor = await collection.AggregateAsync<BsonDocument>(stages, options, cancellationToken)
            .ConfigureAwait(false);

        while (await cursor.MoveNextAsync(cancellationToken).ConfigureAwait(false))
        {
            foreach (var document in cursor.Current)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var cells = new object?[columns.Count];
                for (var index = 0; index < columns.Count; index++)
                {
                    cells[index] = MongoValueReader.Read(document, columns[index], types[index]);
                }

                yield return new ExportRow(cells);
            }
        }
    }

    /// <summary>Which <see cref="FieldType"/> each of <paramref name="fields"/> declares — every entry
    /// here is a real schema field (an export column), unlike the grid path's column list, which can
    /// also carry an aggregate's output name.</summary>
    private static FieldType[] ResolveTypes(IReadOnlyList<string> fields, GridSchema schema)
    {
        var types = new FieldType[fields.Count];
        for (var index = 0; index < fields.Count; index++)
        {
            types[index] = schema.Fields.TryGetValue(fields[index], out var field)
                ? field.Type
                : throw new MongoDriverCompilationException($"Field '{fields[index]}' is not declared in the GridSchema.");
        }

        return types;
    }
}
