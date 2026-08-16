namespace Craticula.Search;

/// <summary>
/// A request this compiler has no defined OpenSearch/Elasticsearch rendering for. Mirrors
/// <c>Craticula.Sql.SqlCompilationException</c> and <c>Craticula.Mongo.MongoCompilationException</c>:
/// most of what it reports is a <em>caller precondition</em> failure (a field the
/// <see cref="Craticula.Core.Schema.GridSchema"/> does not declare, a value whose variant disagrees
/// with that field's declared type) rather than a client error, because a compiler is only ever
/// reached after the request pipeline's allowlist, limit and coercion stages.
///
/// <para>Three of its uses are different, and deliberately so — they are the corners of the pinned
/// 7.10-era DSL subset that have no rendering at all, refused rather than approximated in SQL-7(b)'s
/// and MDB-9's register: a case-insensitive ordering comparison or sort key on a field that declares
/// no <c>search.normalizedSubField</c> (SEM-1's own MUST), and a grouped level asked for a row window
/// that a <c>composite</c> aggregation cannot express (OSE-11).</para>
/// </summary>
public sealed class SearchCompilationException : Exception
{
    public SearchCompilationException(string message) : base(message)
    {
    }

    public SearchCompilationException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
