using System.Text;

namespace Craticula.Core.Schema;

/// <summary>
/// SEC-4's <c>LIKE</c>/<c>ILIKE</c> pattern escaping, shared by every dialect — and, since #1645, by
/// the EF Core execution adapter, which is why it now lives here rather than in
/// <c>Craticula.Sql</c>.
///
/// <para><b>Why this is not a method on <c>Craticula.Sql.ISqlDialect</c> and not copied into each
/// implementation.</b> The <em>algorithm</em> — escape the escape character itself first, then
/// <c>%</c> and <c>_</c>, and nothing else — is identical in every dialect that has an
/// <c>ESCAPE</c> clause at all; only the escape <em>character</em> differs (Postgres names <c>\</c>, a
/// dialect whose <c>\</c> is already special names something else). Duplicating the loop per dialect
/// would mean four places to get "escape the escape first" wrong, and a dialect that dropped one of
/// the three characters would silently turn a user's literal <c>%</c> into a wildcard — a match-set
/// bug no artifact comparison in a dialect that happens to have no <c>%</c> in its fixtures would
/// catch.</para>
///
/// <para><b>Why it moved into <c>Craticula.Core</c> (#1645).</b> The reasoning above is about a
/// <em>second implementation of one rule</em>, and AR-4's whole purpose is that the IR-to-expression-tree
/// path and the IR-to-SQL-text path cannot answer a rule differently:
/// <c>Craticula.EntityFrameworkCore</c> builds the same pattern for
/// <c>EF.Functions.Like(column, pattern, "\\")</c> that <c>PostgresDialect</c> builds for
/// <c>ILIKE … ESCAPE '\'</c>, from the same client value, and a copy of this loop over there would be
/// the exact divergence the paragraph above refuses between two dialects — only now across two
/// packages, where no artifact comparison watches it at all. An execution adapter may not reference a
/// compiler (AR-3: dependencies point into the core), so the shared rule has exactly one legal home.
///
/// <para>The name keeps its <c>Sql</c> prefix deliberately, though it now sits beside the SC-*/SEC-*
/// validators. What it escapes IS a SQL <c>LIKE</c> pattern — EF's <c>EF.Functions.Like</c> emits one
/// too — and renaming a public type would have rewritten every <c>&lt;see cref&gt;</c> in the five
/// dialects that cite it, turning a mechanical move into a diff no reviewer could skim. Mongo's own
/// escaping stays separate and deliberately wider
/// (<c>Craticula.Mongo.MongoRegexEscaping</c>, SEM-7's "must NOT conflate the two").</para></para>
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

    /// <summary>
    /// <see cref="Escape"/> plus T-SQL's extra metacharacter: <c>[</c>, which opens a character class
    /// in SQL Server's <c>LIKE</c> and in no other dialect this project targets. Use this wherever the
    /// engine treats <c>[</c> as a metacharacter, and <see cref="Escape"/> everywhere else.
    ///
    /// <para><b>Why the two passes compose rather than being one loop</b> — the part that is easy to
    /// get wrong, and the reason this lives here instead of at each call site. The <c>[</c> pass must
    /// run <em>second</em>: the escape character that <see cref="Escape"/> introduces is an escape, not
    /// a literal, and must not be doubled again; and a literal escape character in the value was
    /// already doubled by that first pass and carries no <c>[</c> for this one to see. Running them the
    /// other way round, or fusing them, silently changes what the user searched for.</para>
    ///
    /// <para><b>Why it is shared.</b> SQL-5 is normative (see <c>dialect-parity</c>'s D5, pinned by
    /// fixture <c>022</c>): an unescaped <c>[</c> silently <em>widens</em> a match set, so the failure
    /// is wrong rows rather than an exception, and nothing downstream would notice. Two owners of that
    /// decision is exactly what #1645 moved this class into <c>Craticula.Core</c> to prevent, and the
    /// second owner appeared in the same commit — the SQL dialect and the EF Core adapter each held a
    /// copy. If SQL-5 ever widens to <c>]</c>, <c>^</c> or <c>-</c>, it widens here once.</para>
    /// </summary>
    public static string EscapeIncludingLeftBracket(string value, char escapeCharacter) =>
        Escape(value, escapeCharacter).Replace("[", $"{escapeCharacter}[", StringComparison.Ordinal);
}
