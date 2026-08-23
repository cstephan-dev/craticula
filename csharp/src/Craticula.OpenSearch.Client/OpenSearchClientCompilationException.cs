namespace Craticula.OpenSearch.Client;

/// <summary>
/// A structurally valid request this adapter has no defined execution for, a value this package cannot
/// materialize/encode, or an OpenSearch/Elasticsearch call that itself failed — the
/// <c>Craticula.OpenSearch.Client</c> counterpart of
/// <c>Craticula.Dapper.DapperCompilationException</c>/<c>Craticula.MongoDB.Driver.MongoDriverCompilationException</c>,
/// used for the equivalent populations of failure on THIS package's own execution surface.
///
/// <para><b>Named <c>OpenSearchClientCompilationException</c>, not <c>OpenSearchCompilationException</c> —
/// #1647's own forward note, applied here on purpose.</b> The obvious name collides with
/// <see cref="Craticula.Search.SearchCompilationException"/>... except it does not share a simple name
/// with the COMPILER's own type at all (that one is <c>SearchCompilationException</c>, named after the
/// compiler PACKAGE — <c>Craticula.Search</c> — rather than after either backend it targets). The
/// collision #1647 actually warns against is the OTHER one: an adapter named after its client library
/// (<c>Craticula.OpenSearch.Client</c>) picking the SAME simple name as a sibling adapter's own
/// exception would be a silent, needless landmine the moment a host imports both namespaces in one
/// <c>catch</c> — so this type is named after ITS OWN package, exactly as
/// <see cref="Craticula.MongoDB.Driver.MongoDriverCompilationException"/> is named after
/// <c>Craticula.MongoDB.Driver</c> rather than reusing <c>Craticula.Mongo</c>'s own
/// <c>MongoCompilationException</c>.</para>
///
/// <list type="number">
/// <item><description><b>A shape this package refuses on purpose</b> — a <see cref="Craticula.Core.Ir.NonePage"/>
/// reaching <see cref="OpenSearchGridQuery.FetchPageAsync"/> (use <see cref="OpenSearchExportStream.StreamAsync"/>
/// for an unpaginated export instead), a <see cref="Craticula.Core.Ir.RangePage"/> or a
/// <see cref="Craticula.Core.Ir.GroupSpec"/> reaching <see cref="OpenSearchExportStream.StreamAsync"/>
/// (EXP-2/EXP-3 and EXP-1 v1's scoping, mirroring every sibling adapter verbatim).</description></item>
/// <item><description><b>A native-value materialization this package cannot perform</b> — a
/// <see cref="System.Text.Json.JsonElement"/> <see cref="OpenSearchValueReader"/> has no defined
/// conversion from into the schema's declared <see cref="Craticula.Core.Schema.FieldType"/>.</description></item>
/// <item><description><b>A compiled search body shape that disagrees with the request that produced
/// it</b> — an aggregation output this package cannot attribute to a resolvable
/// <see cref="Craticula.Core.Ir.GroupingPlan"/> entry, which would indicate
/// <c>Craticula.Search.SearchQueryBuilder</c> and this package's own column-naming logic have drifted
/// apart.</description></item>
/// <item><description><b>An OpenSearch/Elasticsearch call itself failed</b> — a non-success HTTP
/// response, or the low-level client's own connection/serialization failure. Unlike
/// <c>MongoDB.Driver</c>, <c>OpenSearch.Net</c>'s low-level client does not throw on a failed call by
/// default — a failed request comes back as an ordinary response object with
/// <c>ApiCallDetails.Success == false</c> — so this package has no separate driver-exception hierarchy
/// to let propagate unchanged the way <c>Craticula.MongoDB.Driver</c> does; this type is what a host
/// catches for "the request could not be executed" either way.</description></item>
/// <item><description><b>A caller precondition failure</b> passed through unchanged from
/// <see cref="Craticula.Search.SearchQueryBuilder.Build"/> itself (a field the schema does not declare,
/// a value whose variant disagrees with that field's declared type) — this package adds no
/// defense-in-depth re-validation of its own on top of that call, since it never composes the query
/// tree or the aggregation shape, only executes whatever <c>Craticula.Search</c> already
/// produced.</description></item>
/// </list>
///
/// <para>Deliberately not an <see cref="InvalidOperationException"/> or an
/// <see cref="ArgumentException"/>, for the reason <c>DapperCompilationException</c> gives: a host
/// distinguishing "this request cannot be executed" from any other failure category needs a dedicated
/// type to catch.</para>
/// </summary>
public sealed class OpenSearchClientCompilationException : Exception
{
    /// <summary>
    /// The raw HTTP status of the OpenSearch/Elasticsearch response that caused this exception, when the
    /// failure originated from a non-success response (<c>RequireSuccess</c> in
    /// <see cref="OpenSearchGridQuery"/>/<see cref="OpenSearchExportStream"/>) — <see langword="null"/>
    /// for every other population of failure this type also carries (a shape refused up front, a
    /// materialization failure, a caller precondition failure).
    ///
    /// <para><b>Why this exists</b>: a 4xx-range status means the engine explicitly REJECTED this
    /// specific request (a stale/invalid <c>search_after</c> payload, a closed PIT) — the cursor itself
    /// is poisoned and a cache entry keyed on it should be evicted. A 5xx/transport failure says nothing
    /// about the cursor's own validity — evicting on that would discard a perfectly healthy, already-
    /// cached cursor for no reason. <see cref="OpenSearchGridQuery"/>'s own cursor-cache eviction logic
    /// is exactly this distinction, made possible by exposing the status here rather than working around
    /// its absence.</para>
    /// </summary>
    public int? HttpStatusCode { get; }

    public OpenSearchClientCompilationException(string message) : base(message)
    {
    }

    public OpenSearchClientCompilationException(string message, Exception innerException, int? httpStatusCode = null)
        : base(message, innerException)
    {
        HttpStatusCode = httpStatusCode;
    }
}
