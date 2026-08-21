namespace Craticula.EntityFrameworkCore;

/// <summary>
/// A structurally valid IR shape this adapter has no defined <see cref="System.Linq.IQueryable"/>
/// translation for — the exact counterpart of <c>Craticula.Sql.SqlCompilationException</c>, and used
/// for the same three populations of failure.
///
/// <list type="number">
/// <item><description><b>A caller precondition failure</b> — a field the
/// <see cref="Craticula.Core.Schema.GridSchema"/> does not declare, a value whose variant disagrees
/// with the declared type, a <c>values</c> array that is missing. Reached only by a request that
/// skipped <c>GridFilterPipeline</c>.</description></item>
/// <item><description><b>A shape this adapter refuses on purpose</b> — row-group state reaching the
/// per-clause entry points directly rather than through <see cref="EfCoreGridQuery.FetchPageAsync{TRow}"/>
/// (each applies one clause and has no defined meaning for group state on its own), aggregations with
/// no group column at all (#1622: pivot mode has no translation anywhere in this adapter), a
/// <c>physical.sql</c> mapping form with no oracle, a case-sensitive substring match on a provider
/// whose pattern operator folds case. Each of these is a place where the alternative is a query that
/// runs and returns the wrong rows, which is the failure mode this project consistently prefers an
/// exception to.</description></item>
/// <item><description><b>A divergence from the SQL compiler that would otherwise be silent</b> — the
/// one case being a <c>nullValue</c> in a scalar comparison; see
/// <see cref="EfCoreQueryBuilder"/>.</description></item>
/// </list>
///
/// <para>Deliberately not an <see cref="InvalidOperationException"/> or an
/// <see cref="ArgumentException"/>: a host distinguishing "this request cannot be compiled" from
/// "EF could not translate the tree I built" needs the two to be different types, and EF throws the
/// latter itself.</para>
/// </summary>
public sealed class EfCoreCompilationException : Exception
{
    public EfCoreCompilationException(string message) : base(message)
    {
    }

    public EfCoreCompilationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
