using System.Text;

namespace Craticula.Mongo;

/// <summary>
/// SEM-7's MongoDB escape set — a <b>strictly wider character class than SEC-4's</b>, and the
/// distinction is the whole reason this exists instead of reusing
/// <c>Craticula.Sql.SqlPatternEscaping</c>.
///
/// <para>SEC-4 covers SQL <c>LIKE</c> wildcards (<c>%</c>, <c>_</c>, <c>\</c>) and ES wildcard syntax
/// (<c>*</c>, <c>?</c>). It says nothing about <em>regex</em> metacharacters, and MongoDB has no
/// non-regex substring operator at all — so a <c>$regex</c> built from a SEC-4-escaped-only pattern
/// still leaves <c>.</c>, <c>+</c>, <c>*</c>, <c>(</c>, <c>)</c>, <c>[</c>, <c>]</c>, <c>{</c>,
/// <c>}</c>, <c>|</c>, <c>^</c>, <c>$</c> live. That is both a match-scope-widening bug (the pattern
/// matches more than the literal substring the user typed) and, combined with
/// attacker-influenced repetition/alternation, a ReDoS vector — the exact surface SEC-5 excludes
/// user-controlled regex operators to avoid, reopened through the back door. SEM-7's own "must NOT"
/// names this conflation explicitly.</para>
///
/// <para>The rule SEM-7 pins is therefore not a list of metacharacters but its complement: <b>a full
/// backslash-escape of every ASCII character outside <c>[A-Za-z0-9]</c></b> (MDB-11). Enumerating the
/// metacharacters instead would be a list to keep in sync with a regex engine's own grammar; the
/// allowlist cannot go stale. It escapes more than strictly necessary (a space becomes <c>\ </c>,
/// which is harmless in a regex), and <c>spec/fixtures/dialect-parity/020</c> pins exactly that:
/// <c>50% off (bonus)</c> compiles to <c>50\%\ off\ \(bonus\)</c>.</para>
///
/// <para><b>Why the rule stops at ASCII, and why that is a correctness fix rather than a
/// relaxation.</b> PCRE2 — the engine MongoDB's <c>$regex</c> uses — has no metacharacter outside
/// ASCII, so escaping a non-ASCII character can never change what the pattern matches; it can only
/// change how the pattern is *spelled*. Spelling it wrong, however, was possible: an earlier
/// "everything outside <c>[A-Za-z0-9]</c>" reading iterated UTF-16 <b>code units</b> and therefore
/// inserted a backslash <em>between the two halves of a surrogate pair</em>, turning
/// <c>contains "😀"</c> into a pattern containing two lone surrogates. <c>System.Text.Json</c>
/// replaces an unpaired surrogate with U+FFFD on write, so the emitted artifact silently matched
/// nothing. Restricting the escape to ASCII removes the question entirely: every character this
/// method inspects is one code unit, one code point and one rune at once, so no port has to be told
/// which of the three "character" means — the divergence surface a code-point-based port would
/// otherwise have had against a code-unit-based one.</para>
/// </summary>
public static class MongoRegexEscaping
{
    /// <summary>
    /// Backslash-escapes every ASCII character of <paramref name="value"/> that is not a letter or a
    /// digit, and passes every non-ASCII character through unchanged, so the result matches that
    /// value literally when embedded in a <c>$regex</c> pattern. The anchors a
    /// <c>startsWith</c>/<c>endsWith</c> adds (<c>^</c>, <c>$</c>) are concatenated by the caller
    /// AFTER this call — they are pattern syntax the compiler owns, not part of the value.
    /// </summary>
    public static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        var escaped = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            // Non-ASCII passes through: PCRE2 has no metacharacter above U+007F, so a backslash
            // there would be decoration — and, on a surrogate pair, a backslash BETWEEN the halves,
            // which is a broken pattern rather than a verbose one. Keeping the rule inside ASCII also
            // keeps it culture-independent: no port has to decide which Unicode categories count as
            // "a letter", nor whether it iterates code units or code points.
            if (character >= 0x80
                || character is (>= 'A' and <= 'Z') or (>= 'a' and <= 'z') or (>= '0' and <= '9'))
            {
                escaped.Append(character);
                continue;
            }

            escaped.Append('\\').Append(character);
        }

        return escaped.ToString();
    }
}
