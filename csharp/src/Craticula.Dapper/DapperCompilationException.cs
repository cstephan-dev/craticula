namespace Craticula.Dapper;

/// <summary>
/// A structurally valid request this adapter has no defined execution for, or a value this package
/// cannot materialize/encode — the Dapper counterpart of <c>Craticula.Sql.SqlCompilationException</c>
/// and <c>Craticula.EntityFrameworkCore.EfCoreCompilationException</c>, used for the equivalent
/// populations of failure.
///
/// <list type="number">
/// <item><description><b>A shape this package refuses on purpose</b> — a
/// <see cref="Craticula.Core.Ir.RangePage"/> reaching <see cref="DapperExportStream.StreamAsync"/>
/// (EXP-3 forbids offset-walking an export; call <see cref="DapperGridQuery.FetchPageAsync"/> for a
/// page-scoped export instead), row-group state reaching the export path (EXP-1 v1's scoping — group
/// exports are out of scope), or a non-positive <see cref="DapperExportOptions.ChunkSize"/>.</description></item>
/// <item><description><b>A non-unique <see cref="Craticula.Core.Schema.GridSchema.StableKey"/></b>,
/// detected during the keyset walk exactly as
/// <c>Craticula.EntityFrameworkCore.EfCoreExportStream</c> detects it for its own adapter — see that
/// type's remarks for why the failure is a silently SHORT export rather than an infinite loop.</description></item>
/// <item><description><b>A native-value materialization this package cannot perform</b> — a driver
/// value <see cref="DapperValueReader"/> has no defined conversion from into the schema's declared
/// <see cref="Craticula.Core.Schema.FieldType"/>, or a keyset boundary value
/// <see cref="DapperKeysetPredicate"/> cannot encode back into AR-7's typed IR for the next chunk.
/// Neither <c>Craticula.Sql</c> nor <c>Craticula.EntityFrameworkCore</c> has an equivalent population,
/// because neither reads a value back off an executed statement the way this package's export chunking
/// does.</description></item>
/// <item><description><b>A caller precondition failure</b> passed through unchanged from
/// <see cref="Craticula.Sql.SqlQueryBuilder.Build"/> itself (a field the schema does not declare, a
/// value whose variant disagrees with the declared type) — this package adds no defense-in-depth
/// re-validation of its own on top of that call, since it never composes the filter tree, only
/// recompiles whatever the caller/keyset walk hands it.</description></item>
/// </list>
///
/// <para>Deliberately not an <see cref="InvalidOperationException"/> or an
/// <see cref="ArgumentException"/>: a host distinguishing "this request cannot be executed" from "the
/// driver itself rejected the statement" needs the two to be different types, and Dapper/ADO.NET throws
/// the latter itself.</para>
/// </summary>
public sealed class DapperCompilationException : Exception
{
    public DapperCompilationException(string message) : base(message)
    {
    }

    public DapperCompilationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
