namespace Craticula.Sql;

/// <summary>
/// A compiled SQL artifact: the statement text plus its ordered parameter values — exactly the
/// <c>{ dialect, sql, params }</c> shape every <c>spec/fixtures/*/expected/postgres.artifact.json</c>
/// pins, and the shape a Dapper-style execution adapter consumes (see
/// <c>docs/architecture/c4-components-sql-compiler.md</c>).
///
/// <para><b>Not</b> the shape the EF Core adapter consumes, though earlier prose here and in that
/// diagram said so. AR-4 exempts it from SQL text altogether: <c>Craticula.EntityFrameworkCore</c>
/// (#1645) compiles the same IR to <c>IQueryable&lt;T&gt;</c> expression trees and does not reference
/// this assembly at all. What the two paths share is the rules, in <c>Craticula.Core</c>, not this
/// type.</para>
///
/// <para><see cref="Parameters"/> carries the CLR values that serialize to the artifact's own
/// <c>params</c> array element-for-element: <see cref="string"/>, <see cref="long"/>,
/// <see cref="double"/>, <see cref="bool"/>, a nested <c>IReadOnlyList&lt;object?&gt;</c> for a set
/// membership's single array parameter, and — deliberately — a <see cref="string"/> for a
/// <c>decimal</c> value (AR-7's numeric string; binding it through a floating type would reintroduce
/// exactly the round-tripping loss AR-7 exists to avoid). <c>decimal</c> and <c>double</c> therefore
/// differ only by which CLR type appears here, which is why
/// <see cref="SqlQueryBuilder"/> types them from the schema's declared <c>FieldType</c> rather than
/// from the value's surface shape.</para>
/// </summary>
public sealed record SqlQuery
{
    /// <summary>The dialect that produced this artifact (<see cref="ISqlDialect.Name"/>) — the
    /// artifact's own <c>dialect</c> discriminator.</summary>
    public required string Dialect { get; init; }

    public required string Sql { get; init; }

    /// <summary>The bound values, in first-appearance order: the filter tree left-to-right, then
    /// whatever the paging clause binds. Always exactly as many entries as the statement has
    /// placeholders.</summary>
    public required IReadOnlyList<object?> Parameters { get; init; }
}
