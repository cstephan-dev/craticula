using System.Text.Json;
using Craticula.AgGrid;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql.Tests;

/// <summary>
/// The <c>ORDER BY</c> and paging halves of <see cref="SqlQueryBuilder"/>, isolated from the filter
/// tree — SEM-3 (explicit null placement), SEM-6 (the stable-key tiebreaker), SEM-1 applied to sort
/// keys, and AG-3/EXP-2's two page modes.
/// </summary>
public class SqlQueryBuilderPageAndSortTests
{
    private static FieldConfig Field(FieldType type, string column, CaseSensitivity? caseSensitivity = null) => new()
    {
        Type = type,
        Physical = new PhysicalMapping { Sql = new SqlMapping { Column = column } },
        Text = caseSensitivity is null ? null : new TextOptions { CaseSensitivity = caseSensitivity.Value }
    };

    private static readonly GridSchema Schema = new()
    {
        Source = "employees",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = Field(FieldType.Int64, "id"),
            ["lastName"] = Field(FieldType.String, "last_name"),
            ["code"] = Field(FieldType.String, "code", CaseSensitivity.Sensitive),
            ["salary"] = Field(FieldType.Decimal, "salary")
        }
    };

    private static SqlQuery Compile(IReadOnlyList<SortItem>? sort, PageSpec? page = null) =>
        SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = Schema,
                Sort = sort,
                Page = page ?? new RangePage { StartRow = 0, EndRow = 100 }
            },
            PostgresDialect.Instance);

    /// <summary>
    /// SC-1 / SEC-6 (Bug #2565's review): SEM-6's reduction drops every key after the stable key, and a dropped key is never looked up. A
    /// sort that names an undeclared column AFTER the stable key must still be refused, so the reduction leaves a sort with an unknown key
    /// alone and the compiler's own lookup rejects it, as it did before the reduction existed. Fixture <c>adversarial/028</c> is the wire shape.
    /// </summary>
    [Fact]
    public void An_Undeclared_Sort_Field_After_The_StableKey_Is_Still_Refused()
    {
        var exception = Assert.Throws<SqlCompilationException>(() => Compile([
            Key("id", SortDirection.Asc, NullsPosition.Last),
            Key("internalRank", SortDirection.Asc, NullsPosition.Last)
        ]));

        Assert.Contains("not declared in the GridSchema", exception.Message, StringComparison.Ordinal);
    }

    private static SortItem Key(string field, SortDirection direction, NullsPosition nulls) =>
        new() { Field = field, Direction = direction, NullsPosition = nulls };

    [Fact]
    public void No_Sort_At_All_Still_Orders_By_The_Stable_Key()
    {
        // SEM-6: without a total order, server-side paging silently returns overlapping or missing
        // rows between pages, so the tiebreaker is never optional.
        var query = Compile(sort: null);

        Assert.Equal("SELECT * FROM \"employees\" ORDER BY \"id\" ASC NULLS LAST LIMIT $1 OFFSET $2", query.Sql);
    }

    [Fact]
    public void The_Tiebreaker_Is_Appended_Last_And_Never_Interleaved()
    {
        var query = Compile([Key("salary", SortDirection.Desc, NullsPosition.First)]);

        Assert.Contains("ORDER BY \"salary\" DESC NULLS FIRST, \"id\" ASC NULLS LAST", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void The_Tiebreaker_Is_Not_Appended_Twice_When_The_Sort_Already_Ends_In_It()
    {
        var query = Compile([Key("lastName", SortDirection.Asc, NullsPosition.Last), Key("id", SortDirection.Asc, NullsPosition.Last)]);

        Assert.Contains(
            "ORDER BY \"last_name\" COLLATE craticula_ci ASC NULLS LAST, \"id\" ASC NULLS LAST LIMIT",
            query.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_Case_Insensitive_String_Key_Collates_Between_The_Column_And_The_Direction()
    {
        // SEM-1: sort and filter must use the SAME mechanism family on a field, and for sorting that
        // is always the collation — never ILIKE.
        var query = Compile([Key("lastName", SortDirection.Desc, NullsPosition.First)]);

        Assert.Contains("\"last_name\" COLLATE craticula_ci DESC NULLS FIRST", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Case_Sensitive_String_Key_And_A_Non_String_Key_Both_Sort_Uncollated()
    {
        var query = Compile([Key("code", SortDirection.Asc, NullsPosition.Last), Key("salary", SortDirection.Asc, NullsPosition.Last)]);

        Assert.DoesNotContain("COLLATE", query.Sql, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM-6: the APPENDED tiebreaker resolves exact even when the stable key is a case-insensitive
    /// string, while the user's own key on an equally case-insensitive column still collates — both
    /// in the same statement, which is what makes this a rule about the key's provenance and not
    /// about the field.
    ///
    /// <para>Determinism is the reason, not tidiness. <c>craticula_ci</c> is declared
    /// <c>deterministic = false</c>, so under it <c>'aB3'</c> and <c>'Ab3'</c> compare EQUAL: a
    /// collated tiebreaker leaves those rows tied, Postgres is then free to order them differently at
    /// <c>OFFSET 0</c> and <c>OFFSET 100</c>, and a row comes back twice or vanishes from both pages —
    /// exactly the failure SEM-6 exists to prevent, reintroduced by SEM-6's own mechanism. Spelling
    /// nothing sorts the key under its own storage collation, which is the one its declared uniqueness
    /// is stated in.</para>
    /// </summary>
    [Fact]
    public void The_Appended_Tiebreaker_Is_Exact_Even_When_The_Stable_Key_Is_A_Case_Insensitive_String()
    {
        var schema = new GridSchema
        {
            Source = "employees",
            StableKey = "id",
            Fields = new Dictionary<string, FieldConfig>
            {
                ["id"] = Field(FieldType.String, "id"),
                ["lastName"] = Field(FieldType.String, "last_name")
            }
        };

        var query = SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = schema,
                Sort = [Key("lastName", SortDirection.Asc, NullsPosition.Last)],
                Page = new RangePage { StartRow = 0, EndRow = 100 }
            },
            PostgresDialect.Instance);

        Assert.Contains(
            "ORDER BY \"last_name\" COLLATE craticula_ci ASC NULLS LAST, \"id\" ASC NULLS LAST LIMIT",
            query.Sql,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// ...and the exemption is scoped to the key this compiler MINTED: a user who explicitly sorts by
    /// the stable key gets SEM-1's ordinary treatment for that column, because nothing was appended.
    /// </summary>
    [Fact]
    public void A_User_Sort_That_Already_Ends_In_A_Case_Insensitive_Stable_Key_Still_Collates()
    {
        var schema = new GridSchema
        {
            Source = "employees",
            StableKey = "id",
            Fields = new Dictionary<string, FieldConfig> { ["id"] = Field(FieldType.String, "id") }
        };

        var query = SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = schema,
                Sort = [Key("id", SortDirection.Asc, NullsPosition.Last)],
                Page = new RangePage { StartRow = 0, EndRow = 100 }
            },
            PostgresDialect.Instance);

        Assert.Contains("ORDER BY \"id\" COLLATE craticula_ci ASC NULLS LAST", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Nulls_Position_Is_Emitted_Literally_And_Not_Re_Derived_From_The_Direction()
    {
        // SEM-3's must-not: the adapter's asc->last / desc->first mapping is the ADAPTER's rule; a
        // compiler honours whatever the IR carries, including the "unusual" combinations.
        var query = Compile([Key("salary", SortDirection.Asc, NullsPosition.First), Key("code", SortDirection.Desc, NullsPosition.Last)]);

        Assert.Contains("\"salary\" ASC NULLS FIRST, \"code\" DESC NULLS LAST", query.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Range_Page_Binds_Limit_Then_Offset_After_Every_Filter_Parameter()
    {
        var query = Compile([Key("salary", SortDirection.Asc, NullsPosition.Last)], new RangePage { StartRow = 200, EndRow = 250 });

        Assert.EndsWith("LIMIT $1 OFFSET $2", query.Sql, StringComparison.Ordinal);

        // LIMIT is the page SIZE (endRow is exclusive, AG-3), OFFSET is startRow.
        Assert.Equal([50, 200], query.Parameters);
    }

    [Fact]
    public void A_None_Page_Emits_No_Row_Range_Clause_And_Binds_Nothing()
    {
        var query = Compile(sort: null, page: new NonePage());

        Assert.Equal("SELECT * FROM \"employees\" ORDER BY \"id\" ASC NULLS LAST", query.Sql);
        Assert.Empty(query.Parameters);
    }

    /// <summary>
    /// EXP-1(b): an EMPTY projection is refused rather than rendered. It cannot arrive from
    /// <c>AgGridRequestParser</c>, which refuses an empty <c>exportColumns</c> at the wire — the
    /// reachable population is a hand-built request or a future non-AG-Grid adapter, exactly the
    /// population the <c>Projection</c>+<c>Group</c> guard beside it already exists for. Unguarded it
    /// renders <c>SELECT  FROM "employees"</c>, and the driver is the first thing that says so.
    /// </summary>
    [Fact]
    public void An_Empty_Export_Projection_Is_Refused_Rather_Than_Rendered_As_An_Empty_Select_List()
    {
        var ex = Assert.Throws<SqlCompilationException>(() => SqlQueryBuilder.Build(
            new SqlQueryRequest { Schema = Schema, Page = new NonePage(), Projection = [] },
            PostgresDialect.Instance));

        Assert.Contains("empty EXP-1(b) export projection", ex.Message, StringComparison.Ordinal);
    }
}
