namespace Craticula.EntityFrameworkCore;

/// <summary>
/// The engine facts this adapter cannot derive from the IR, the schema, or EF Core — five of them,
/// all statements <em>about a database</em> rather than spellings of anything.
///
/// <para><b>Why this exists at all, when EF Core is supposed to be provider-agnostic.</b> It is, for
/// everything EF has an API for. It has none for the two SEM-* rules whose realization is inherently
/// per-engine. SEM-1 says a <c>string</c> field compares case-insensitively by default and
/// case-sensitively when the host opts in (SC-3), and <em>both</em> directions may need an explicit
/// mechanism: <c>EF.Functions.Collate(column, name)</c> is provider-independent, but the collation
/// NAME is not (<c>craticula_ci</c> on PostgreSQL, <c>NOCASE</c> on SQLite,
/// <c>SQL_Latin1_General_CP1_CS_AS</c> on SQL Server), and on some engines the default collation
/// already folds case so the mechanism must be spelled for the SENSITIVE direction instead. SEM-7's
/// substring family has the same problem one level up: <c>EF.Functions.Like</c> compiles to the
/// engine's own <c>LIKE</c>, which folds ASCII case unconditionally on SQLite, follows the column's
/// collation on SQL Server and MySQL, and folds none at all on PostgreSQL. SEC-4 then adds a third: T-SQL's <c>LIKE</c> has a metacharacter the
/// other four engines do not.</para>
///
/// <para><b>This is NOT a second <c>ISqlDialect</c>, and the difference is structural rather than a
/// matter of degree.</b> Every member of that interface but one returns SQL <em>text</em> — the
/// compiler asks "how do you spell this?" and splices the answer in. Not one member here returns any
/// text: this record answers questions, the adapter composes an expression tree from the answers, and
/// <b>EF's provider</b> does all the spelling. The one member of <c>ISqlDialect</c> that is shaped
/// like these is <c>EmptyStringIsNull</c>, whose own remarks name the shape and say when to reach for
/// it — "when the composition must stay in one place and only a single engine fact varies inside it"
/// — which is every member below. It is also the same fact, under the same name, deciding the same
/// SEM-2 composition (SQL-7); that it recurs verbatim in a package that shares no code with the SQL
/// compiler is the clearest evidence available that it belongs to the engine and not to a
/// renderer.</para>
///
/// <para><b>Only <see cref="Sqlite"/> is supplied, and that is a deliberate refusal rather than an
/// omission.</b> #1645's execution-equality harness runs on SQLite, so those five values are
/// <em>measured</em>: every one of them is the reason some assertion in
/// <c>Craticula.EntityFrameworkCore.Tests</c> passes. A <c>Postgres</c> or <c>SqlServer</c> preset
/// would be prose promoted to code with no oracle behind it — this repo's standing rule is that a
/// path whose only witness is its own author's unit test is not verified — and getting one wrong is
/// invisible in exactly the direction SEM-1 exists to guard: a query that runs and quietly compares
/// case the other way. The remarks on each member state what a host must supply for the other four
/// engines and which <c>Craticula.Sql</c> dialect to read for the corresponding SQL-side rendering;
/// a host declares its own, once, next to its <c>DbContext</c>.</para>
///
/// <para><b>Two obligations that run without error and return wrong rows (Bug #2805).</b> The adapter documents them and does not
/// guard against them, because the thing that is wrong lives in the host's model or in the database, which the adapter neither sees
/// nor owns. Each is pinned by <c>EfCoreHostObligationTests</c>, wrong without it and right with it.
/// <list type="number">
/// <item><description><b>SQLite instants.</b> SEM-4's SQLite arm has a host store a <c>datetime</c> as the fixed-width UTC text
/// <c>yyyy-MM-ddTHH:mm:ss.fffZ</c>. EF's own SQLite mapping binds a <see cref="DateTime"/> parameter as <c>yyyy-MM-dd HH:mm:ss</c>
/// (a space, no <c>Z</c>), and SQLite compares TEXT character by character, where <c>'T'</c> sorts after <c>' '</c>. So a range
/// filter with a bound that is not at midnight counts every row of the bound's date as later (13,335 rows for 13,321 on the demo's
/// 20,000), and a keyset export sorted by a datetime repeats or drops rows at the chunk boundaries (20,060 and 19,947 rows for
/// 20,000), and it never ends when a calendar date holds a chunk's worth of rows; the export stops with an exception when its walk
/// meets the row the previous chunk ended on. The host maps every datetime to and from the stored text in its <c>DbContext</c>
/// (<c>ConfigureConventions</c>, <c>Properties&lt;DateTime&gt;().HaveConversion&lt;…&gt;()</c>). On a database whose values EF itself
/// wrote, the two forms agree and nothing is needed.</description></item>
/// <item><description><b><see cref="PatternMatchFoldsCase"/> says what the engine's <c>LIKE</c> does, and the host must not claim
/// more than is true.</b> <see langword="true"/> sends a bare <c>LIKE</c>. That is right where the <c>LIKE</c> folds case whatever the
/// column is (SQLite, ASCII only), and on SQL Server or MySQL only if <b>every</b> searchable column's collation folds case: on a
/// binary-collated column (the demo's SQL Server grouping columns are <c>Latin1_General_100_BIN2</c>; MySQL's <c>utf8mb4_bin</c>) a
/// <c>contains "germ"</c> finds no <c>Germany</c> (0 rows for 1,933). <see langword="false"/> lowers both sides and is right for every
/// <b>case-insensitive</b> field whatever the collation, at the cost of the column's index. It is not right for a field declared
/// <c>caseSensitivity: "sensitive"</c> on an engine whose <c>LIKE</c> folds (SQLite): there <see langword="true"/> is what makes the
/// adapter refuse the request (SC-3) instead of answering case-insensitively. So: SQLite <see langword="true"/>; SQL Server, MySQL and
/// PostgreSQL <see langword="false"/> unless the collations are checked.</description></item>
/// </list></para>
/// </summary>
public sealed record EfCoreProviderSemantics
{
    /// <summary>
    /// SEM-1: the collation that folds case on this engine, or <see langword="null"/> when the
    /// column's own default collation already does (so nothing must be spelled).
    ///
    /// <para>Applied to the COLUMN side — <c>"customerName" COLLATE NOCASE = @p</c> — for the
    /// exact-match/ordering/set family (<c>eq</c>/<c>neq</c>/<c>gt</c>/<c>gte</c>/<c>lt</c>/<c>lte</c>/
    /// <c>in</c>/<c>notIn</c>) and for every <c>ORDER BY</c> key over a case-insensitive field, which
    /// is SEM-1's "one mechanism per field in every context". Column-side rather than value-side even
    /// on SQLite, where <c>SqliteDialect</c> collates the value: SQLite resolves a binary comparison
    /// from the left operand's explicit collation and only then the right's, so the column side is
    /// correct there too, and it is the ONLY side that works for <c>x IN (…)</c> (SQLite reads x's
    /// collating sequence alone). Using one side for both constructs is what stops this adapter from
    /// needing SQLite's asymmetry as a sixth fact.</para>
    ///
    /// <para>What each engine needs: PostgreSQL the host-created <c>craticula_ci</c> the fixture suite
    /// declares; SQLite the built-in <c>NOCASE</c> (ASCII-only — a real semantic limit, not a
    /// spelling); SQL Server any <c>_CI_</c> collation; MySQL <see langword="null"/>, because case
    /// sensitivity there is a property of the column's declared collation and a query spells neither
    /// direction (see <c>MySqlDialect</c>); Oracle a linguistic <c>NLS_SORT</c> arrangement rather
    /// than a collation name, which is why no value is offered for it here.</para>
    /// </summary>
    public string? CaseInsensitiveCollation { get; init; }

    /// <summary>
    /// SEM-1/SC-3: the collation that distinguishes case on this engine, or <see langword="null"/>
    /// when the column's own default collation already does — which is the common case.
    ///
    /// <para><b>Why a case-SENSITIVE field can need a mechanism too.</b>
    /// <c>SemanticsResolver.CaseSensitivityMode</c> is three-valued precisely because
    /// <c>Sensitive</c> is a decision a backend must realize, not the absence of one: SQL Server and
    /// Azure SQL ship <c>SQL_Latin1_General_CP1_CI_AS</c>, so a field the host declared
    /// <c>caseSensitivity: "sensitive"</c> silently matches case-insensitively unless a CS collation
    /// is spelled. <see langword="null"/> on PostgreSQL and SQLite (both default to a
    /// case-distinguishing collation) and on MySQL (the column decides).</para>
    /// </summary>
    public string? CaseSensitiveCollation { get; init; }

    /// <summary>
    /// SEM-7: does this engine's <c>LIKE</c> ignore case? <see langword="true"/> on SQLite (whose
    /// <c>LIKE</c> is ASCII-case-insensitive unconditionally and ignores collating sequences
    /// entirely), <see langword="false"/> on PostgreSQL (whose <c>LIKE</c> never folds — <c>ILIKE</c>
    /// is the case-insensitive operator, and it is reachable only through the Npgsql provider's own
    /// extension method, which this package must not reference).
    ///
    /// <para>It decides how each direction of the substring family is built, and it is independent of
    /// <see cref="CaseInsensitiveCollation"/> rather than derivable from it — SQLite proves the
    /// independence in one engine: its default collation is <c>BINARY</c> (case-distinguishing) while
    /// its <c>LIKE</c> folds, so the two facts point in opposite directions there.</para>
    ///
    /// <list type="bullet">
    /// <item><description><b>Insensitive.</b> <see langword="true"/> emits
    /// <c>EF.Functions.Like(column, pattern)</c> and nothing more. <see langword="false"/> emits
    /// <c>Like(column.ToLower(), &lt;pattern already lowered here&gt;)</c> — the column side becomes
    /// <c>lower(…)</c>, which every provider translates. That is a documented divergence from
    /// <c>PostgresDialect</c>'s <c>ILIKE</c>: the two agree on ASCII and on ordinary Unicode case
    /// pairs and can differ under an exotic collation. Collating the operand instead
    /// (<c>column COLLATE craticula_ci LIKE @p</c>) is legal T-SQL but PostgreSQL rejects
    /// <c>LIKE</c> against a nondeterministic collation outright, and <c>craticula_ci</c> is declared
    /// <c>deterministic = false</c> — so the portable mechanism is <c>lower()</c>.</description></item>
    /// <item><description><b>Sensitive.</b> <see langword="false"/> emits plain
    /// <c>Like(column, pattern)</c>, optionally collated with <see cref="CaseSensitiveCollation"/>
    /// where the engine's default folds. <see langword="true"/> is <b>refused</b>: on such an engine
    /// <c>LIKE</c> cannot express a case-sensitive match at all, and neither can EF's
    /// <c>string.StartsWith</c>/<c>EndsWith</c>, which compile to <c>LIKE</c> as well.
    /// <c>SqliteDialect</c> reaches for <c>GLOB</c> there, and <c>EF.Functions.Glob</c> exists — in
    /// <c>Microsoft.EntityFrameworkCore.Sqlite</c>, a provider package. See
    /// <see cref="EfCoreQueryBuilder"/> for why refusing beats the two silent
    /// alternatives.</description></item>
    /// </list>
    ///
    /// <para><b>Host obligation (#2805).</b> <see langword="true"/> is a bare <c>LIKE</c>: claim it only where the <c>LIKE</c> folds
    /// case itself (SQLite) or every searchable column's collation does (SQL Server, MySQL); on a binary-collated column the match is
    /// exact-case and returns wrong rows without an error. See the type's remarks.</para>
    /// </summary>
    public bool PatternMatchFoldsCase { get; init; }

    /// <summary>
    /// SEC-4: does this engine's <c>LIKE</c> treat <c>[</c> as a metacharacter? <see langword="true"/>
    /// on SQL Server alone, where it opens a character class, so an unescaped user value
    /// <c>A[BC]</c> matches <c>AB</c> or <c>AC</c> rather than itself and <c>[a-z]</c>/<c>[^x]</c>
    /// widen the match set further. <see langword="false"/> on the other four.
    ///
    /// <para>The escaping alphabet is otherwise shared and lives in one place
    /// (<c>SqlPatternEscaping.Escape</c>, which covers <c>\</c>, <c>%</c> and <c>_</c>); this fact
    /// adds T-SQL's fourth character on top of that call, exactly as
    /// <c>MssqlDialect.EscapePattern</c> does and in the same order — the shared pass first, so the
    /// <c>\</c> this one introduces is an escape character and is not itself doubled. Read that
    /// method's remarks for why <c>]</c>, <c>^</c> and <c>-</c> need no escaping.</para>
    ///
    /// <para>It is a fact and not a spelling because the consequence of getting it wrong is a
    /// <em>match set</em>, not a syntax error: SEM-7's own Must-Not is that a user-supplied literal
    /// must never behave as a wildcard.</para>
    /// </summary>
    public bool PatternMatchTreatsLeftBracketAsMetacharacter { get; init; }

    /// <summary>
    /// SQL-7: does this engine treat the zero-length string as NULL? <see langword="true"/> on Oracle
    /// alone.
    ///
    /// <para>The single input SEM-2's <c>blank</c> composition needs, and the same member
    /// <c>ISqlDialect</c> carries under the same name for the same reason. On an engine where
    /// <c>''</c> IS NULL, the text arm's <c>= ''</c> disjunct is not redundant but UNKNOWN for every
    /// row, which leaves <c>notBlank</c> — defined as the Boolean negation of <c>blank</c> — matching
    /// no row at all. So the arm collapses onto the non-text one there. The composition itself stays
    /// in <see cref="EfCoreQueryBuilder"/>, so <c>blank</c> and <c>notBlank</c> cannot disagree on any
    /// provider.</para>
    /// </summary>
    public bool EmptyStringIsNull { get; init; }

    /// <summary>
    /// SQLite, as measured by #1645's execution-equality harness — the one provider whose values are
    /// witnessed by a test that executes both paths and compares rows, rather than asserted from
    /// documentation.
    ///
    /// <para><c>NOCASE</c> is SQLite's built-in ASCII-only case-insensitive collating sequence; its
    /// default (<c>BINARY</c>) already distinguishes case, so no case-sensitive collation is needed;
    /// its <c>LIKE</c> folds ASCII case unconditionally; <c>[</c> is an ordinary character to it; and
    /// it distinguishes <c>''</c> from NULL. Every one of the five is also asserted on the SQL side by
    /// <c>SqliteDialect</c>, from which they can be read back independently.</para>
    /// </summary>
    public static EfCoreProviderSemantics Sqlite { get; } = new()
    {
        CaseInsensitiveCollation = "NOCASE",
        CaseSensitiveCollation = null,
        PatternMatchFoldsCase = true,
        PatternMatchTreatsLeftBracketAsMetacharacter = false,
        EmptyStringIsNull = false
    };
}
