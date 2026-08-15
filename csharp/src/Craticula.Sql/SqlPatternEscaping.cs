using System.Text;

namespace Craticula.Sql;

/// <summary>
/// SEC-4's <c>LIKE</c>/<c>ILIKE</c> pattern escaping, shared by every dialect.
///
/// <para><b>Why this is not a method on <see cref="ISqlDialect"/> and not copied into each one.</b>
/// The <em>algorithm</em> — escape the escape character itself first, then <c>%</c> and <c>_</c>,
/// and nothing else — is identical in every dialect that has an <c>ESCAPE</c> clause at all; only the escape <em>character</em>
/// differs (Postgres names <c>\</c>, a dialect whose <c>\</c> is already special names something
/// else). Duplicating the loop per dialect would mean four places to get "escape the escape first"
/// wrong, and a dialect that dropped one of the three characters would silently turn a user's
/// literal <c>%</c> into a wildcard — a match-set bug no artifact comparison in a dialect that
/// happens to have no <c>%</c> in its fixtures would catch.</para>
/// </summary>
public static class SqlPatternEscaping
{
    /// <summary>
    /// Prefixes every <paramref name="escapeCharacter"/>, <c>%</c> and <c>_</c> in
    /// <paramref name="value"/> with <paramref name="escapeCharacter"/>, so the value matches
    /// literally under a pattern operator carrying <c>ESCAPE '&lt;escapeCharacter&gt;'</c>.
    ///
    /// <para>Nothing else is touched: the value is a bound parameter, not interpolated text, so
    /// ordinary SQL-injection characters are already harmless here (SEC-2) and escaping them would
    /// change what the user searched for. Deliberately narrower than the Mongo <c>$regex</c> escape
    /// set — SEM-7's own "must NOT" about conflating the two.</para>
    /// </summary>
    public static string Escape(string value, char escapeCharacter)
    {
        ArgumentNullException.ThrowIfNull(value);

        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            // The escape character goes first in the test as well as in the output: it must be
            // doubled BEFORE the prefixes added below, or escaping would escape its own escapes.
            if (character == escapeCharacter || character is '%' or '_')
            {
                escaped.Append(escapeCharacter);
            }

            escaped.Append(character);
        }

        return escaped.ToString();
    }
}
