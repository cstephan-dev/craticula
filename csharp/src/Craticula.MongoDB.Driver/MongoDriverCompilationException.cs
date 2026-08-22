namespace Craticula.MongoDB.Driver;

/// <summary>
/// A structurally valid request this adapter has no defined execution for, or a value this package
/// cannot materialize/encode — the Craticula.MongoDB.Driver counterpart of
/// <c>Craticula.Dapper.DapperCompilationException</c>/<c>Craticula.EntityFrameworkCore.EfCoreCompilationException</c>,
/// used for the equivalent populations of failure on THIS package's own execution surface.
///
/// <para><b>Named <c>MongoDriverCompilationException</c>, not <c>MongoCompilationException</c> —
/// review found the obvious name collides with <see cref="Craticula.Mongo.MongoCompilationException"/>,
/// the COMPILER's own exception type, and that collision is a real hazard rather than a cosmetic one.</b>
/// This package's own source never needs to name the compiler's type, since every exception
/// <c>Craticula.Mongo.MongoPipelineBuilder.Build</c> throws propagates unchanged and uncaught — but a
/// HOST calling <see cref="MongoGridQuery.FetchPageAsync"/>/<see cref="MongoExportStream.StreamAsync"/>
/// can receive EITHER type from one call (the compiler's on a schema/variant precondition failure, this
/// one on a column/materialization failure it cannot attribute), so a same-simple-name pair would force
/// every host to fully qualify both in every <c>catch</c> clause forever, and a host importing only one
/// namespace would silently catch only half with no diagnostic. Every sibling adapter
/// (<c>Dapper</c>/<c>EntityFrameworkCore</c>) was spared this because neither one's own compiler
/// (<c>Craticula.Sql</c>/none) happens to share its exception's simple name — Mongo is the first
/// adapter to face it, since its compiler and its adapter are both named after "Mongo".</para>
///
/// <list type="number">
/// <item><description><b>A shape this package refuses on purpose</b> — a
/// <see cref="Craticula.Core.Ir.RangePage"/> reaching <see cref="MongoExportStream.StreamAsync"/>
/// (EXP-2/EXP-3 — call <see cref="MongoGridQuery.FetchPageAsync"/> for a page-scoped export instead),
/// row-group state reaching the export path (EXP-1 v1's scoping — group exports are out of scope), or
/// an empty EXP-1(b) export projection.</description></item>
/// <item><description><b>A native-value materialization this package cannot perform</b> — a driver
/// <c>BsonValue</c> <see cref="MongoValueReader"/> has no defined conversion from into the schema's
/// declared <see cref="Craticula.Core.Schema.FieldType"/>.</description></item>
/// <item><description><b>A compiled pipeline shape that disagrees with the request that produced it</b>
/// — a document field this package cannot attribute to either a declared schema field or a resolvable
/// aggregation output, which would indicate <c>Craticula.Mongo.MongoPipelineBuilder</c> and this
/// package's own column-naming logic (<see cref="Craticula.Core.Ir.GroupingPlan"/>) have drifted apart.</description></item>
/// <item><description><b>A caller precondition failure</b> passed through unchanged from
/// <see cref="Craticula.Mongo.MongoPipelineBuilder.Build"/> itself (a field the schema does not
/// declare, a value whose variant disagrees with that field's declared type) — this package adds no
/// defense-in-depth re-validation of its own on top of that call, since it never composes the filter
/// tree or the pipeline, only executes whatever <c>Craticula.Mongo</c> already produced.</description></item>
/// </list>
///
/// <para>Deliberately not an <see cref="InvalidOperationException"/> or an
/// <see cref="ArgumentException"/>, for the reason <c>DapperCompilationException</c> gives: a host
/// distinguishing "this request cannot be executed" from "the driver itself rejected the operation"
/// needs the two to be different types, and <c>MongoDB.Driver</c>'s own <c>MongoException</c>
/// hierarchy is what throws the latter.</para>
/// </summary>
public sealed class MongoDriverCompilationException : Exception
{
    public MongoDriverCompilationException(string message) : base(message)
    {
    }

    public MongoDriverCompilationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
