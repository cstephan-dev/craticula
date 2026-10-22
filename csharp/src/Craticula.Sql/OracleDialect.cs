using System.Globalization;
using System.Text;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql;

/// <summary>
/// The Oracle Database dialect (#1642), and the one dialect this project ships as
/// <b>community-tested</b>: it is graded by the T-1a artifact suite exactly like the other four —
/// every rendering here is pinned byte-for-byte by
/// <c>spec/fixtures/{dialect-parity,group-aggregation}/*/expected/oracle.artifact.json</c> — and, since #2880,
/// the 45 arms also run against a real Oracle Database Free 23ai in the execution-equality tier. That tier is
/// not part of the pull-request gate (the release workflow does run it, so a failing Oracle test stops a release), and Oracle 12.2 to 21c were not run. Nothing in
/// <c>spec/fixtures/README.md</c>'s support tiering should be read as promising more than that.
///
/// <para><b>This dialect assumes Oracle Database 12.2 or later</b> (with <c>COMPATIBLE &gt;= 12.2</c>
/// and <c>MAX_STRING_SIZE = EXTENDED</c>), the same kind of hard version floor
/// <see cref="SqliteDialect"/> takes on 3.30.0 for <c>NULLS FIRST</c>/<c>NULLS LAST</c>. 12.2 is the
/// release that introduced data-bound collation and the <c>COLLATE</c> operator, which is SEM-1's
/// mechanism here (see <see cref="CaseInsensitiveCollation"/>); it is also the release that raised
/// the identifier length limit from 30 bytes to 128, so this dialect assumes <b>128</b>. Nothing in
/// the fixture suite comes close to either limit — the longest identifier any fixture declares is
/// <c>employeeCode</c>, at 12 characters — so that is a statement of what the dialect assumes, not a
/// constraint the artifacts happen to satisfy. An older server rejects a compiled statement outright
/// (ORA-43929 for the collation), which is a loud failure like SQLite's, not a silent one like
/// MySQL's missing column collation.</para>
///
/// <para><b>Oracle treats the empty string as NULL, and that is why this story widened
/// <see cref="ISqlDialect"/>.</b> See <see cref="EmptyStringIsNull"/> and SQL-7: it is the only
/// member of this interface Oracle answers differently from every other dialect, and the only one
/// that is an engine fact rather than a spelling.</para>
///
/// <para><b>The same fact makes this the one dialect that REJECTS a value the IR allows.</b> An empty
/// <c>stringValue</c> is legal everywhere in <c>ir.schema.json</c>, and here it binds as NULL — so
/// every comparison against it is UNKNOWN and the compiled statement returns the wrong rows without
/// erroring. <see cref="RejectEmptyString"/> refuses it on all three paths a string value can reach
/// (scalar comparison, set membership, substring match) rather than emit that statement. No other
/// dialect changes behaviour, and the IR-level alternative was deliberately not taken — see that
/// method.</para>
///
/// <para><b>Four renderings here are Oracle facts rather than style choices.</b></para>
/// <list type="bullet">
/// <item><description><b>There is no <c>ILIKE</c>.</b> SEM-1's substring family reaches
/// case-insensitivity through the same <c>COLLATE</c> operator the exact-match family uses — one
/// mechanism per field, which is exactly what SEM-1 demands and what the rejected alternative could
/// not deliver (see <see cref="CaseInsensitiveCollation"/>).</description></item>
/// <item><description><b>Both case-sensitivity directions are spelled</b>, as on SQL Server and for
/// the same reason: Oracle's default column collation is <c>USING_NLS_COMP</c>, which resolves
/// through the <em>session</em>'s <c>NLS_COMP</c>/<c>NLS_SORT</c>, so "emit nothing" does not mean
/// "compare case-sensitively" — see <see cref="CaseSensitiveCollation"/>.</description></item>
/// <item><description><b>Set membership is <c>IN (...)</c> with N scalar parameters</b>, not
/// Postgres's single array parameter: Oracle's <c>= ANY</c> takes an expression list, and binding a
/// collection needs a schema-level declared type the compiler cannot assume
/// (SQL-4).</description></item>
/// <item><description><b>Paging binds the offset before the row count</b> (Oracle's 12.1 row-limiting
/// clause is spelled <c>OFFSET ... ROWS FETCH NEXT ... ROWS ONLY</c>, like T-SQL's), while the bare
/// row cap of GRP-6 is spelled <c>FETCH FIRST ... ROWS ONLY</c> with no <c>OFFSET</c> at all —
/// legal here and illegal on SQL Server, which is why <see cref="RenderRowLimit"/> exists as its own
/// member.</description></item>
/// </list>
///
/// <para><b>What is <em>not</em> different, despite folklore.</b> Identifier quoting is <c>"</c> with
/// <c>"</c> doubled, the same as Postgres and SQLite. Concatenation is <c>||</c>. <c>NULLS FIRST</c>
/// and <c>NULLS LAST</c> are native. A backslash in a string literal is an ordinary character, so
/// <c>ESCAPE '\'</c> is a one-character string here and MySQL's <c>'\\'</c> spelling would be wrong.
/// Oracle's <c>LIKE</c> has exactly two metacharacters, <c>%</c> and <c>_</c>, so
/// <see cref="SqlPatternEscaping"/>'s shared alphabet is the whole alphabet — T-SQL's <c>[</c> is
/// T-SQL's alone.</para>
///
/// <para>Stateless and therefore safe to share; <see cref="Instance"/> exists so callers need not
/// allocate one per request.</para>
/// </summary>
public sealed class OracleDialect : ISqlDialect
{
    public static readonly OracleDialect Instance = new();

    /// <summary>
    /// SEM-1's case-insensitive mechanism here: Oracle's built-in <c>BINARY_CI</c> named collation,
    /// applied through the <c>COLLATE</c> operator introduced in 12.2. Pinned across the suite the
    /// way <see cref="PostgresDialect.CaseInsensitiveCollation"/> and
    /// <see cref="MssqlDialect.CaseInsensitiveCollation"/> are — one name, chosen here rather than
    /// declared per <c>GridSchema</c>. Built in rather than host-created (unlike Postgres's
    /// <c>craticula_ci</c>), so the host owes no <c>CREATE COLLATION</c>.
    ///
    /// <para><b>Chosen over <c>NLSSORT(col, 'NLS_SORT=BINARY_CI')</c>, deliberately and at the cost
    /// of a version floor.</b> NLSSORT works on every Oracle back to 9i and would have avoided
    /// pinning 12.2, but it cannot express a case-insensitive <b>substring</b> match at all: a
    /// collation key of a substring is not a substring of that string's collation key, so a
    /// <c>LIKE</c> over NLSSORT output matches nothing meaningful. The substring family would have
    /// had to fall back to <c>UPPER(col) LIKE UPPER(:v)</c> or to the session's
    /// <c>NLS_COMP = LINGUISTIC</c> — leaving one field folding case by two different mechanisms
    /// depending on the operator, which is precisely what SEM-1's "the same mechanism in every
    /// context on a field" forbids. NLSSORT also wraps the bound <em>value</em> in a function call,
    /// so the SQL text would carry engine-specific scaffolding around every placeholder. The session
    /// route was rejected outright on the same ground as SQLite's
    /// <c>PRAGMA case_sensitive_like</c>: connection-level state a compiler must not depend
    /// on.</para>
    /// </summary>
    public const string CaseInsensitiveCollation = "BINARY_CI";

    /// <summary>
    /// SEM-1's case-SENSITIVE collation here — Oracle's <c>BINARY</c>, the byte-ordering counterpart
    /// of <see cref="CaseInsensitiveCollation"/>.
    ///
    /// <para>Spelled rather than left bare because on this engine, as on SQL Server (SQL-6),
    /// "emit nothing" is not the same as "compare case-sensitively". A column declares no collation
    /// of its own unless the DDL gave it one, in which case it derives <c>USING_NLS_COMP</c> — which
    /// resolves through the <em>session</em> parameters <c>NLS_COMP</c> and <c>NLS_SORT</c>. A
    /// session running <c>NLS_COMP = LINGUISTIC</c> with <c>NLS_SORT = BINARY_CI</c>, or a column
    /// declared <c>COLLATE BINARY_CI</c> in DDL, folds case; a bare comparison there would silently
    /// return the rows a <c>caseSensitivity: "sensitive"</c> declaration asked to exclude, and the
    /// SQL would read as though it were correct.</para>
    ///
    /// <para>Unlike MySQL — which faces the same problem and still emits nothing — Oracle has one
    /// portable spelling for this direction, so it is a token rather than a host DDL obligation.</para>
    /// </summary>
    public const string CaseSensitiveCollation = "BINARY";

    /// <summary>The <c>ESCAPE</c> character named in every emitted <c>LIKE</c>. Oracle has no default
    /// escape character at all, so SEM-7's "never a pattern without a matching <c>ESCAPE</c>" is not
    /// a formality here — and unlike MySQL, a backslash inside an Oracle string literal is an
    /// ordinary character, so the clause is spelled <c>ESCAPE '\'</c> and not <c>ESCAPE '\\'</c>.</summary>
    private const char PatternEscape = '\\';

    public string Name => "oracle";

    /// <summary>
    /// SQL-7's one <c>true</c> in the whole project. Oracle documents that a character value of
    /// length zero is NULL, which makes SEM-2's <c>= ''</c> disjunct UNKNOWN for every row rather
    /// than merely never true — and therefore makes <c>notBlank</c>, which negates the whole
    /// composition, match nothing at all. See <see cref="ISqlDialect.EmptyStringIsNull"/> for the
    /// three-valued-logic walk-through.
    /// </summary>
    public bool EmptyStringIsNull => true;

    /// <summary>
    /// SQL-7's other half, and the only place any dialect in this project <em>rejects</em> a value the
    /// IR permits. <c>ir.schema.json</c>'s <c>stringValue.value</c> has a <c>maxLength</c> and no
    /// <c>minLength</c>, so <c>""</c> is a legal filter value everywhere — and on this engine alone it
    /// is NULL, which makes every comparison against it UNKNOWN rather than false.
    ///
    /// <para><b>Why rejecting beats compiling.</b> The wrong answers are not symmetrical and the worse
    /// one is silent: <c>eq ""</c> returns an empty grid (defensible-looking), while <c>neq ""</c>
    /// returns <em>only the NULL rows</em> where every other dialect returns everything-but-empty —
    /// wrong rows rather than no rows, on syntactically valid SQL, with no error. <c>contains ""</c>
    /// is worse still: the anchored pattern <c>'%' || NULL || '%'</c> is itself NULL, so a predicate
    /// that means "match every non-null row" matches none. A compiler that cannot express the request
    /// correctly must say so.</para>
    ///
    /// <para>Scoped to this dialect on purpose. The alternative — a <c>minLength: 1</c> on
    /// <c>stringValue</c> — would be a <c>/v1/</c> contract change forbidding an input the other four
    /// engines handle correctly, so it was considered and deferred to a story of its own rather than
    /// smuggled in behind a dialect. Callers wanting "is this value empty or absent" already have the
    /// operator for it: SEM-2's <c>blank</c>/<c>notBlank</c>, which SQL-7 compiles correctly here.</para>
    /// </summary>
    private static void RejectEmptyString(object? value, string construct)
    {
        if (value is string { Length: 0 })
        {
            throw new SqlCompilationException(
                $"Oracle stores a zero-length string as NULL, so binding '' in {construct} compiles to a " +
                "comparison against NULL — UNKNOWN for every row, whichever way the operator points. The " +
                "statement would be valid SQL returning the wrong rows silently (an empty result for a " +
                "positive operator, only the NULL-valued rows for a negated one), so this dialect refuses " +
                "it instead. Use the 'blank'/'notBlank' operators to ask whether a value is empty or " +
                "absent (SEM-2/SQL-7); the other four dialects accept an empty value and are unaffected.");
        }
    }

    public string QuoteIdentifier(string identifier) =>
        // The standard SQL form, identical to Postgres's and SQLite's, with an embedded '"' doubled.
        //
        // Quoting is not optional cosmetics on this engine even more than elsewhere: Oracle folds an
        // UNquoted identifier to UPPERCASE, so `FROM employees` reaches the server as `EMPLOYEES`.
        // SQL-1's "always quote" neutralises that in the statement — but it moves the obligation to
        // the host's DDL, because Oracle also stores an unquoted-CREATEd name folded. Every `source`
        // and physical column the fixture suite declares is lowercase or camelCase (`employees`,
        // `sales_fact`, `lastName`, `fiscal_year`), so those tables must be created with quoted
        // names or `FROM "employees"` raises ORA-00942. Recorded in dialect-parity/README.md's host
        // obligations table.
        '"' + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + '"';

    // Named binds with a colon prefix — Oracle's own placeholder syntax. Named rather than positional
    // (`:1`, `:2` are also legal) so the ordinal is visible in the text, which is what lets
    // DialectConformanceFixtureTests assert ascending first-appearance order here as it does for
    // SQL Server's `@pN` and cannot for MySQL/SQLite's bare `?`.
    public string ParameterPlaceholder(int ordinal) =>
        ":p" + (ordinal + 1).ToString(CultureInfo.InvariantCulture);

    public string RenderScalarComparison(
        string quotedColumn,
        SqlScalarComparison comparison,
        object? value,
        FieldType fieldType,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        // SQL-7: '' is NULL here, so `c = ''` is `c = NULL`. Checked before anything is bound so the
        // parameter sink is never left holding a partial statement's values.
        RejectEmptyString(value, "a scalar comparison");

        // SEM-1: on the COLUMN side, like Postgres and SQL Server (SQLite is the outlier that
        // collates the value). Oracle's COLLATE binds tighter than any comparison operator, so
        // `c COLLATE BINARY_CI = :p1` parses as `(c COLLATE BINARY_CI) = :p1` with no parentheses.
        var left = quotedColumn + Collation(caseSensitivity);
        return $"{left} {SqlComparisonSymbols.For(comparison)} {parameters.Bind(value)}";
    }

    /// <summary>
    /// The <c>COLLATE</c> suffix (including its leading space) for <paramref name="caseSensitivity"/>,
    /// or the empty string when no collation may be spelled at all. One place, because SEM-1 requires
    /// the same mechanism in every context on a field: the scalar, substring, set-membership and
    /// sort-key paths all attach exactly this.
    /// </summary>
    private static string Collation(SemanticsResolver.CaseSensitivityMode caseSensitivity) => caseSensitivity switch
    {
        SemanticsResolver.CaseSensitivityMode.Insensitive => $" COLLATE {CaseInsensitiveCollation}",
        SemanticsResolver.CaseSensitivityMode.Sensitive => $" COLLATE {CaseSensitiveCollation}",

        // NotApplicable: not a text comparison. Oracle's COLLATE operator accepts only a character
        // operand and rejects a NUMBER/DATE one outright, so this arm is a hard requirement rather
        // than a tidiness preference — the second engine to make the tri-state seam load-bearing.
        SemanticsResolver.CaseSensitivityMode.NotApplicable => string.Empty,

        _ => throw new SqlCompilationException($"Unknown case-sensitivity mode '{caseSensitivity}'.")
    };

    public string RenderSubstringMatch(
        string quotedColumn,
        SqlSubstringAnchor anchor,
        bool negated,
        string value,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        // SQL-7: an empty pattern binds as NULL, and `'%' || NULL || '%'` is NULL — so a `contains ""`
        // that means "every non-null row" would match nothing. Same engine fact, third path.
        RejectEmptyString(value, "a substring match");

        // SEM-1: Oracle has no ILIKE, so the substring family reaches case-insensitivity through the
        // same COLLATE operator the exact-match family uses and the operator itself never changes.
        // 12.2's data-bound collation covers LIKE explicitly, which is what makes one mechanism per
        // field achievable here at all (see CaseInsensitiveCollation on why NLSSORT could not).
        var collation = Collation(caseSensitivity);
        var op = negated ? "NOT LIKE" : "LIKE";

        var placeholder = parameters.Bind(SqlPatternEscaping.Escape(value, PatternEscape));

        // '||' is Oracle's concatenation operator, as on Postgres and SQLite; T-SQL's '+' and the
        // CONCAT() call form are both wrong here.
        var pattern = anchor switch
        {
            SqlSubstringAnchor.Anywhere => $"'%' || {placeholder} || '%'",
            SqlSubstringAnchor.Start => $"{placeholder} || '%'",
            SqlSubstringAnchor.End => $"'%' || {placeholder}",
            _ => throw new SqlCompilationException($"Unknown substring anchor '{anchor}'.")
        };

        return $"{quotedColumn}{collation} {op} {pattern} ESCAPE '{PatternEscape}'";
    }

    public string RenderSetMembership(
        string quotedColumn,
        bool negated,
        IReadOnlyList<object?> values,
        FieldType fieldType,
        SemanticsResolver.CaseSensitivityMode caseSensitivity,
        ISqlParameterSink parameters)
    {
        // SQL-7, third path: one empty member is enough to poison the whole list. `IN (:p1, :p2)` with
        // a NULL member still matches the non-null ones, so the positive direction merely loses a
        // value — but `NOT IN` with any NULL member is UNKNOWN for EVERY row and returns nothing at
        // all, which is the same silent-empty-grid failure notBlank had. Checked over every member
        // before any is bound.
        foreach (var member in values)
        {
            RejectEmptyString(member, "a set membership list");
        }

        var left = quotedColumn + Collation(caseSensitivity);

        // SQL-4: N scalar parameters. Oracle's `= ANY` takes an expression list rather than an array
        // value, and binding a collection needs a schema-level declared type (a nested table or
        // VARRAY) that no host has been asked to create — so the portable form is the same IN-list
        // the other three non-Postgres dialects use, and params.Length again depends on the value
        // count, which is why this method takes the whole list.
        var placeholders = values.Select(parameters.Bind);
        return $"{left} {(negated ? "NOT IN" : "IN")} ({string.Join(", ", placeholders)})";
    }

    public string RenderSortKey(
        string quotedColumn,
        SortDirection direction,
        NullsPosition nullsPosition,
        SemanticsResolver.CaseSensitivityMode caseSensitivity)
    {
        var key = new StringBuilder(quotedColumn);

        // SEM-1: the same collation mechanism the exact-match family uses, in BOTH directions — an
        // ORDER BY here folds case under a linguistic NLS_SORT exactly as `=` does, so a field must
        // not be able to filter under one mechanism and sort under another.
        key.Append(Collation(caseSensitivity));

        key.Append(direction == SortDirection.Asc ? " ASC" : " DESC");

        // SEM-3: native, like Postgres and SQLite — no ISNULL/CASE bucket emulation, so one IR key
        // stays one ORDER BY key. Always explicit, never the engine's default, even though Oracle's
        // own default (nulls last on ASC, first on DESC) happens to coincide with the adapter's.
        key.Append(nullsPosition == NullsPosition.First ? " NULLS FIRST" : " NULLS LAST");

        return key.ToString();
    }

    // Oracle's row-limiting clause (12.1+) spells the offset BEFORE the row count, so its two paging
    // values bind in the opposite order to Postgres's LIMIT/OFFSET and the same order as T-SQL's —
    // bound as they are spelled, so params[] stays first-appearance ordered end to end. EXP-2's
    // unpaginated case never reaches here: the builder resolves it to "no clause" before asking.
    public string RenderPaging(int offset, int limit, ISqlParameterSink parameters) =>
        $"OFFSET {parameters.Bind(offset)} ROWS FETCH NEXT {parameters.Bind(limit)} ROWS ONLY";

    // GRP-6's row cap. Oracle's FETCH is legal with no OFFSET before it, unlike T-SQL's — so this is
    // the plain `FETCH FIRST n ROWS ONLY` form and needs neither MssqlDialect's literal zero nor a
    // second statement shape. #1641 predicted this spelling would fit RenderRowLimit unchanged when
    // it added the member; it does.
    public string RenderRowLimit(int limit, ISqlParameterSink parameters) =>
        $"FETCH FIRST {parameters.Bind(limit)} ROWS ONLY";
}
