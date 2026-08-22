namespace Craticula.MongoDB.Driver;

/// <summary>
/// One page of grid rows, as <see cref="MongoGridQuery.FetchPageAsync"/> returns it.
///
/// <para><b>This is deliberately NOT OQ-8's shared response envelope, and #1647 deliberately does not
/// design one.</b> #1622's definition of done wants an AG Grid <c>LoadSuccessParams</c>-shaped result
/// backed by one neutral core type, shared by all four execution adapters. No such type exists, no
/// story has built it, and inventing it inside this adapter is how a contract gets set in the wrong
/// package and then has to be undone — the same reasoning <c>Craticula.EntityFrameworkCore.EfCoreGridPage</c>
/// and <c>Craticula.Dapper.DapperGridPage</c> both carry verbatim, for the identical reason. So this
/// type is the narrowest thing this package's own two real consumers (a grid page, <c>CsvWriter</c> via
/// <see cref="MongoExportStream"/>) need, it lives in this package rather than in <c>Craticula.Core</c>,
/// and the shape it turns out to need is reported to #1622/#1649 rather than promoted.</para>
///
/// <para><b>Why <see cref="Columns"/> can name a grouped level's bucket differently from every SQL
/// artifact.</b> <c>Craticula.Mongo.MongoPipelineBuilder</c>'s own remarks explain why: a <c>$group</c>
/// stage's bucket key is ALWAYS <c>_id</c> — MongoDB's own output contract for the stage — never
/// renamed to the group column's logical colId the way GRP-3 aliases it in SQL
/// (<c>Craticula.Core.Ir.GroupingPlan</c>'s own doc calls this "keeping the group key and the
/// aggregates apart only INTERNALLY, until the adapter assembling the outward row object puts them
/// back together"). <b>This package does exactly that</b>: <see cref="MongoGridQuery"/> resolves the
/// SAME <see cref="Craticula.Core.Ir.GroupingPlan"/> the compiler itself resolved to build the
/// pipeline, and renames the grouped level's <c>_id</c> key to the group column's own colId before it
/// ever reaches <see cref="Columns"/>. That is reading back what the pipeline decided, not re-deciding
/// anything — no grouping-DISPATCH logic (which pipeline shape to build) is ported into this package,
/// only this one label lookup. Everywhere else (an ordinary row, a drill-down leaf, an export) the
/// pipeline's own <c>$project</c> stage already re-keys every field under its logical colId, so no
/// renaming is needed there — <see cref="Columns"/> is read straight off the request's own resolved
/// projection.</para>
///
/// <para><b>Why the rows are positional and come with their own column list</b> rather than being a
/// list of <c>Dictionary&lt;string, object?&gt;</c> — <see cref="Craticula.Core.Export.ExportRow"/>'s
/// own argument, restated: the projection is an ORDERED list (EXP-1(b)), this adapter already has the
/// values in that order (having resolved which document key each column reads from), and a per-row
/// dictionary would build one lookup table per row for a mapping that cannot differ between rows.</para>
/// </summary>
public sealed record MongoGridPage
{
    /// <summary>
    /// The logical colIds each row's cells are positionally aligned with, in the order the compiled
    /// pipeline actually produces its documents' fields.
    ///
    /// <para>Unlike <c>Craticula.Dapper.DapperGridPage.Columns</c> (read directly off the executed
    /// reader's own schema), this list is resolved from the request's own already-settled shape rather
    /// than read off the first returned document — a page with ZERO matching documents carries no BSON
    /// schema at all to inspect (unlike an ADO.NET reader, which always knows its column list even over
    /// zero rows), so a document-derived column list would be undefined on an empty page. See
    /// <see cref="MongoGridQuery"/>'s own remarks on how the column list is derived instead.</para>
    /// </summary>
    public required IReadOnlyList<string> Columns { get; init; }

    /// <summary>The page's rows, each aligned with <see cref="Columns"/>, cells as native CLR values
    /// (never a raw <c>BsonValue</c> — see <see cref="MongoValueReader"/>).</summary>
    public required IReadOnlyList<IReadOnlyList<object?>> Rows { get; init; }

    /// <summary>
    /// The number of documents matching the filter with the row window removed — AG Grid SSRM's
    /// <c>rowCount</c>, which lets the grid size its scrollbar and stop requesting blocks. For a
    /// grouped level this is instead the number of BUCKETS at that level — the same <c>$count</c>
    /// aggregation (<see cref="MongoGridQuery.CountAsync"/>) answers both automatically, since a
    /// grouped level's own compiled documents ARE its buckets, with no special-casing required.
    /// <see langword="null"/> when the caller did not ask for it, which is distinct from <c>0</c>: the
    /// grid's own contract treats an unknown total as "keep asking", and a wrong zero would empty it.
    ///
    /// <para>Opt-in rather than always computed, because it is a second round trip on every block a
    /// scrolling user pulls. A grid needs it once per filter change, not once per page.</para>
    /// </summary>
    public long? RowCount { get; init; }
}
