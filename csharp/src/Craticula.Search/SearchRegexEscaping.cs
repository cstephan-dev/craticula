using System.Text;

namespace Craticula.Search;

/// <summary>
/// SEM-7/SEC-5, for the one place this compiler emits a regex: the <c>terms</c> aggregation's
/// <c>include</c> parameter, which GRP-6(c)'s search prefix compiles to (OSE-12). That parameter is a
/// <b>Lucene</b> regular expression, a different language again from the <c>wildcard</c> query's
/// pattern syntax (<see cref="SearchWildcardEscaping"/>) and from PCRE2, which is what MongoDB's
/// <c>$regex</c> uses.
///
/// <para><b>Escape every ASCII character outside <c>[A-Za-z0-9]</c>, exactly as MDB-11 does.</b>
/// Lucene's <c>RegExp</c> reads <c>\</c> as "the next character, literally" for <em>any</em> next
/// character, so over-escaping an ordinary ASCII character costs nothing and removes the need for
/// this class to carry a metacharacter table that would have to be kept in step with Lucene's
/// (<c>. ? + * | { } [ ] ( ) " \ # @ &amp; &lt; &gt; ~</c> today, with optional-syntax flags able to
/// change it). Under-escaping is the failure that matters: an unescaped <c>.</c> in a prefix widens
/// the bucket filter, and unescaped repetition/alternation is the ReDoS surface SEC-5 excludes
/// user-controlled regex to avoid — reopened here unless the value is escaped before insertion.</para>
///
/// <para><b>ASCII-scoped for MDB-11's reason, which is not a style choice.</b> Iterating UTF-16 code
/// units and escaping "everything outside <c>[A-Za-z0-9]</c>" puts a backslash BETWEEN the two halves
/// of a surrogate pair; the JSON writer then emits each lone half as U+FFFD and the compiled pattern
/// matches nothing, silently. No Lucene metacharacter lives above U+007F, so restricting the escape
/// to ASCII answers the question by removing it.</para>
///
/// <para>The compiler's own <c>.*</c> suffix is appended to the RESULT (OSE-12), so a literal
/// <c>.</c> or <c>*</c> the user typed cannot become part of it.</para>
/// </summary>
internal static class SearchRegexEscaping
{
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var escaped = new StringBuilder(value.Length);

        foreach (var character in value)
        {
            if (character <= 0x7F && !char.IsAsciiLetterOrDigit(character))
            {
                escaped.Append('\\');
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
