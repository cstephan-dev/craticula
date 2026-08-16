using System.Text;

namespace Craticula.Search;

/// <summary>
/// SEC-4/SEM-7: neutralises the <c>wildcard</c> query's pattern language inside a value the user
/// typed, so <c>contains "A*B"</c> matches the literal three characters instead of everything from
/// <c>A</c> to <c>B</c>.
///
/// <para><b>The alphabet is three characters and no more.</b> A <c>wildcard</c> query's syntax has
/// exactly two metacharacters — <c>*</c> (any sequence) and <c>?</c> (any single character) — plus
/// <c>\</c>, which escapes them and therefore has to escape itself. That is SEC-4's own
/// "ES wildcard syntax (<c>*</c>, <c>?</c>)" plus the mechanical consequence of having an escape
/// character at all, and it is <b>narrower than every SQL dialect's and far narrower than
/// MongoDB's</b>: <c>%</c>, <c>_</c> and <c>[</c> are ordinary characters here (SQL-5's per-dialect
/// alphabets), and so is every regex metacharacter MDB-11 escapes, because a <c>wildcard</c> query is
/// not a regex. <c>spec/fixtures/dialect-parity/020</c> and <c>/022</c> pin exactly that:
/// <c>50% off (bonus)</c> and <c>A[BC]</c> travel through unchanged while <c>/021</c>'s <c>AB*10</c>
/// becomes <c>AB\*10</c>. Widening the set "to be safe" is not safe — a <c>\%</c> in a wildcard
/// pattern is a literal backslash followed by a percent sign, which is not what the user searched
/// for, the same trap SQL-5's own Must-NOT names.</para>
///
/// <para><b>Only ASCII is considered, and that is MDB-11's rule reached for MDB-11's reason.</b> The
/// three characters are ASCII, so a scan over UTF-16 code units that only ever inserts a backslash
/// <em>before an ASCII match</em> can never land between the two halves of a surrogate pair — the
/// corruption MDB-11 records, where a JSON writer then replaces each lone half with U+FFFD and the
/// pattern silently matches nothing. Stated rather than left implicit because the Mongo escape looked
/// equally safe until it was tested on an emoji.</para>
/// </summary>
internal static class SearchWildcardEscaping
{
    /// <summary>Escapes <paramref name="value"/> for use inside a <c>wildcard</c> query's
    /// <c>value</c>. The compiler's own <c>*</c> anchors are composed around the RESULT, so an
    /// anchor can never be confused with an escaped literal.</summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var escaped = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (character is '\\' or '*' or '?')
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
