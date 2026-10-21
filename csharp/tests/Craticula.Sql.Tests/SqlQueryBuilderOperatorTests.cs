using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql.Tests;

/// <summary>
/// Hand-built, per-operator coverage of <see cref="SqlQueryBuilder"/> against
/// <see cref="PostgresDialect"/>. Complements <see cref="PostgresConformanceFixtureTests"/> rather
/// than duplicating it: the fixture suite proves the end-to-end path reproduces the real oracle,
/// while these cases isolate ONE rendering rule at a time — including the shapes no fixture happens
/// to reach (a case-sensitive substring match, a set membership on a case-sensitive field, an IR
/// value type used with an unexpected field type) — so a regression points at the rule that broke
/// instead of at whichever fixture happened to exercise it first.
/// </summary>
public class SqlQueryBuilderOperatorTests
{
    // ---- the fixture-free schema these cases compile against --------------------------------------

    private static FieldConfig Field(FieldType type, string column, CaseSensitivity? caseSensitivity = null) => new()
    {
        Type = type,
        Physical = new PhysicalMapping { Sql = new SqlMapping { Column = column } },
        Text = caseSensitivity is null ? null : new TextOptions { CaseSensitivity = caseSensitivity.Value }
    };

    private static readonly GridSchema Schema = new()
    {
        Source = "items",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = Field(FieldType.Int64, "id"),
            ["status"] = Field(FieldType.String, "status"),
            ["code"] = Field(FieldType.String, "code", CaseSensitivity.Sensitive),
            ["age"] = Field(FieldType.Int64, "age"),
            ["price"] = Field(FieldType.Double, "price"),
            ["unitCost"] = Field(FieldType.Decimal, "unit_cost"),
            ["isActive"] = Field(FieldType.Boolean, "is_active"),
            ["birthDate"] = Field(FieldType.Date, "birth_date"),
            ["createdAt"] = Field(FieldType.Datetime, "created_at")
        }
    };

    /// <summary>Compiles <paramref name="filter"/> with a fixed sort/page, and returns only the
    /// <c>WHERE</c> clause's text so each case asserts on the one predicate it is about.</summary>
    private static (string Where, IReadOnlyList<object?> Parameters) Where(FilterNode filter)
    {
        var query = Compile(filter);

        const string prefix = "SELECT * FROM \"items\" WHERE ";
        const string suffix = " ORDER BY \"id\" ASC NULLS LAST LIMIT $";
        var start = prefix.Length;
        var end = query.Sql.IndexOf(suffix, StringComparison.Ordinal);
        Assert.True(end > start, $"Unexpected statement shape: {query.Sql}");

        // The paging parameters are always the last two and are asserted by their own test below.
        return (query.Sql[start..end], query.Parameters.Take(query.Parameters.Count - 2).ToList());
    }

    private static SqlQuery Compile(FilterNode? filter, PageSpec? page = null, IReadOnlyList<SortItem>? sort = null) =>
        SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = Schema,
                Filter = filter,
                Sort = sort,
                Page = page ?? new RangePage { StartRow = 0, EndRow = 100 }
            },
            PostgresDialect.Instance);

    private static ComparisonNode Comparison(string field, ComparisonOperator op, TypedValue? value = null) =>
        new() { Field = field, Op = op, Value = value };

    // ---- binary comparisons -----------------------------------------------------------------------

    [Theory]
    [InlineData(ComparisonOperator.Eq, "=")]
    [InlineData(ComparisonOperator.Gt, ">")]
    [InlineData(ComparisonOperator.Gte, ">=")]
    [InlineData(ComparisonOperator.Lt, "<")]
    [InlineData(ComparisonOperator.Lte, "<=")]
    public void A_Non_Null_Inclusive_Binary_Comparison_Renders_Bare(ComparisonOperator op, string symbol)
    {
        var (where, parameters) = Where(Comparison("age", op, new Int64Value { Value = 30 }));

        Assert.Equal($"\"age\" {symbol} $1", where);
        Assert.Equal([30L], parameters);
    }

    [Fact]
    public void Neq_Is_Null_Inclusive_And_Always_Wrapped()
    {
        // SEM-2: a bare `<>` silently drops every NULL row (three-valued logic), which disagrees with
        // what AG Grid's own client-side row model shows for the same filter. SQL-2 pins the wrap.
        var (where, _) = Where(Comparison("age", ComparisonOperator.Neq, new Int64Value { Value = 30 }));

        Assert.Equal("(\"age\" <> $1 OR \"age\" IS NULL)", where);
    }

    [Fact]
    public void A_Case_Insensitive_String_Comparison_Collates_The_Column_But_Not_Its_Is_Null_Disjunct()
    {
        // SEM-1's exact-match family uses a COLLATION, never ILIKE. The IS NULL disjunct is a null
        // test, not a comparison, so no collation attaches to it.
        var (where, _) = Where(Comparison("status", ComparisonOperator.Neq, new StringValue { Value = "x" }));

        Assert.Equal("(\"status\" COLLATE craticula_ci <> $1 OR \"status\" IS NULL)", where);
    }

    [Fact]
    public void A_Case_Sensitive_String_Field_Gets_No_Collation()
    {
        var (where, _) = Where(Comparison("code", ComparisonOperator.Eq, new StringValue { Value = "AB-1" }));

        Assert.Equal("\"code\" = $1", where);
    }

    // ---- substring family -------------------------------------------------------------------------

    [Theory]
    [InlineData(ComparisonOperator.Contains, "\"status\" ILIKE '%' || $1 || '%' ESCAPE '\\'")]
    [InlineData(ComparisonOperator.StartsWith, "\"status\" ILIKE $1 || '%' ESCAPE '\\'")]
    [InlineData(ComparisonOperator.EndsWith, "\"status\" ILIKE '%' || $1 ESCAPE '\\'")]
    public void The_Substring_Family_Uses_ILIKE_With_An_Explicit_Escape_Clause(ComparisonOperator op, string expected)
    {
        var (where, _) = Where(Comparison("status", op, new StringValue { Value = "a" }));

        Assert.Equal(expected, where);
    }

    [Fact]
    public void NotContains_Renders_The_Negated_Operator_Not_An_Outer_Not()
    {
        // SQL-3: every other negative operator keeps its negation in the operator (`<>`, `<> ALL`),
        // so NOT ILIKE is the consistent spelling — and it keeps ESCAPE attached to the pattern
        // expression it belongs to (SEM-7).
        var (where, _) = Where(Comparison("status", ComparisonOperator.NotContains, new StringValue { Value = "a" }));

        Assert.Equal("(\"status\" NOT ILIKE '%' || $1 || '%' ESCAPE '\\' OR \"status\" IS NULL)", where);
    }

    [Fact]
    public void A_Case_Sensitive_Field_Substring_Match_Falls_Back_To_Plain_LIKE()
    {
        // Not pinned by any fixture (every substring case in the suite is on a case-insensitive
        // field), but it follows directly from SEM-1 resolving the mechanism to None here: ILIKE
        // would fold case on a field whose schema explicitly opted out of that.
        var (where, _) = Where(Comparison("code", ComparisonOperator.Contains, new StringValue { Value = "a" }));

        Assert.Equal("\"code\" LIKE '%' || $1 || '%' ESCAPE '\\'", where);
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("50% off", "50\\% off")]
    [InlineData("a_b", "a\\_b")]
    [InlineData("back\\slash", "back\\\\slash")]
    [InlineData("100%_\\", "100\\%\\_\\\\")]
    public void SEC_4_Escapes_Only_The_Three_LIKE_Wildcard_Characters_In_The_Bound_Value(string raw, string expected)
    {
        // The escaping happens in the PARAMETER, never by inlining the value — SEC-2. Characters that
        // matter to a regex but not to LIKE (parentheses, dots, spaces) are deliberately untouched:
        // over-escaping would change what the user searched for.
        var (_, parameters) = Where(Comparison("status", ComparisonOperator.Contains, new StringValue { Value = raw }));

        Assert.Equal([expected], parameters);
    }

    [Theory]
    [InlineData(FieldType.Int64)]
    [InlineData(FieldType.Date)]
    public void A_Substring_Operator_Against_A_Non_String_Field_Is_Rejected_Rather_Than_Guessed(FieldType declared)
    {
        // The rule is about the field's DECLARED type, so the case that discriminates it from "the
        // value happens not to be a string" is an UNCOERCED StringValue on a non-string column: a
        // guard reading only the value's variant would let it through and render
        // `"age" LIKE '%' || $1 || '%' ESCAPE '\'` — a plausible-looking, case-SENSITIVE substring
        // match against a numeric column (SemanticsResolver resolves the pattern-match mechanism for
        // `string` fields only, so the mismatch is silent too).
        var field = declared == FieldType.Int64 ? "age" : "birthDate";

        var uncoerced = Assert.Throws<SqlCompilationException>(
            () => Compile(Comparison(field, ComparisonOperator.Contains, new StringValue { Value = "42" })));

        Assert.Contains($"is declared '{declared}'", uncoerced.Message, StringComparison.Ordinal);
        Assert.Contains("substring match", uncoerced.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Substring_Operator_Carrying_A_Non_String_Value_Is_Rejected_Even_Against_A_String_Field()
    {
        // The other half: the field is fine, the value is not — a caller that skipped SEM-5 coercion.
        var ex = Assert.Throws<SqlCompilationException>(
            () => Compile(Comparison("status", ComparisonOperator.Contains, new Int64Value { Value = 42 })));

        Assert.Contains("TypeCoercionValidator", ex.Message, StringComparison.Ordinal);
    }

    // ---- unary operators ----------------------------------------------------------------------------

    [Fact]
    public void Blank_On_A_String_Field_Is_Null_Or_Empty_And_NotBlank_Negates_That_Exact_Text()
    {
        // SQL-2: the doubled parentheses are SEM-2's "negate the compiled blank condition, never
        // re-derive it" made visible — collapsing them is the first step toward the two drifting.
        // Postgres-specific, and SQL-7 is why that qualifier matters: on Oracle the inner form is a
        // bare IS NULL. DialectIndependenceTests covers both branches across every dialect.
        Assert.Equal("(\"status\" IS NULL OR \"status\" = '')", Where(Comparison("status", ComparisonOperator.Blank)).Where);
        Assert.Equal("NOT ((\"status\" IS NULL OR \"status\" = ''))", Where(Comparison("status", ComparisonOperator.NotBlank)).Where);
    }

    [Fact]
    public void Blank_On_A_Non_Text_Field_Is_Null_Only()
    {
        // SEM-2's fourth must-not: the `= ''` clause is text-specific.
        Assert.Equal("\"age\" IS NULL", Where(Comparison("age", ComparisonOperator.Blank)).Where);
        Assert.Equal("NOT (\"age\" IS NULL)", Where(Comparison("age", ComparisonOperator.NotBlank)).Where);
    }

    [Theory]
    [InlineData(ComparisonOperator.True, true)]
    [InlineData(ComparisonOperator.False, false)]
    public void The_Unary_Boolean_Shorthand_Mints_A_Bound_Parameter_Rather_Than_Inlining_A_Literal(
        ComparisonOperator op, bool expected)
    {
        // AR-6's true/false carry no value of their own; SEC-2 still wants the compared value bound.
        var (where, parameters) = Where(Comparison("isActive", op));

        Assert.Equal("\"is_active\" = $1", where);
        Assert.Equal([expected], parameters);
    }

    // ---- set membership ------------------------------------------------------------------------------

    [Fact]
    public void In_Binds_One_Array_Parameter_So_The_Placeholder_Count_Never_Depends_On_The_Value_Count()
    {
        var query = Compile(new ComparisonNode
        {
            Field = "status",
            Op = ComparisonOperator.In,
            Values = [new StringValue { Value = "a" }, new StringValue { Value = "b" }, new StringValue { Value = "c" }]
        });

        Assert.Contains("\"status\" COLLATE craticula_ci = ANY($1)", query.Sql, StringComparison.Ordinal);

        // SQL-4: three values, still one parameter — the nested array.
        var values = Assert.IsAssignableFrom<IReadOnlyList<object?>>(query.Parameters[0]);
        Assert.Equal(["a", "b", "c"], values);
        Assert.Equal(3, query.Parameters.Count);
    }

    [Fact]
    public void NotIn_Is_The_Negated_Set_Operator_And_Is_Null_Inclusive()
    {
        var (where, _) = Where(new ComparisonNode
        {
            Field = "code",
            Op = ComparisonOperator.NotIn,
            Values = [new StringValue { Value = "a" }]
        });

        Assert.Equal("(\"code\" <> ALL($1) OR \"code\" IS NULL)", where);
    }

    // ---- set membership: the array is cast to the column type for the three string-encoded types (#2304)

    private static TypedValue Sample(string field) => field switch
    {
        "unitCost" => new DecimalValue { Value = "19.99" },
        "birthDate" => new DateValue { Value = "2024-03-01" },
        "createdAt" => new DatetimeValue { Value = "2024-03-01T10:00:00Z", Zone = "UTC" },
        "age" => new Int64Value { Value = 30 },
        "price" => new DoubleValue { Value = 1.5 },
        "isActive" => new BooleanValue { Value = true },
        _ => new StringValue { Value = "a" }
    };

    [Theory]
    [InlineData("unitCost", "numeric[]")]
    [InlineData("birthDate", "date[]")]
    [InlineData("createdAt", "timestamptz[]")]
    public void In_And_NotIn_Cast_The_Array_To_The_Column_Type_For_Decimal_Date_And_Datetime(string field, string arrayType)
    {
        var column = Schema.Fields[field].Physical!.Sql!.Column;

        var (inWhere, inParameters) = Where(new ComparisonNode
        {
            Field = field, Op = ComparisonOperator.In, Values = [Sample(field), Sample(field)]
        });
        Assert.Equal($"\"{column}\" = ANY($1::{arrayType})", inWhere);

        var (notInWhere, _) = Where(new ComparisonNode
        {
            Field = field, Op = ComparisonOperator.NotIn, Values = [Sample(field)]
        });
        Assert.Equal($"(\"{column}\" <> ALL($1::{arrayType}) OR \"{column}\" IS NULL)", notInWhere);

        // Still ONE array parameter, still the string encoding (AR-7): the cast is in the SQL text only.
        var array = Assert.IsAssignableFrom<IReadOnlyList<object?>>(Assert.Single(inParameters));
        Assert.Equal(2, array.Count);
        Assert.All(array, element => Assert.IsType<string>(element));
    }

    [Theory]
    [InlineData("status", "\"status\" COLLATE craticula_ci")]
    [InlineData("code", "\"code\"")]
    [InlineData("age", "\"age\"")]
    [InlineData("price", "\"price\"")]
    [InlineData("isActive", "\"is_active\"")]
    public void In_And_NotIn_Leave_The_Array_Uncast_For_String_Int64_Double_And_Boolean(string field, string left)
    {
        var (inWhere, _) = Where(new ComparisonNode { Field = field, Op = ComparisonOperator.In, Values = [Sample(field)] });
        Assert.Equal($"{left} = ANY($1)", inWhere);

        var (notInWhere, _) = Where(new ComparisonNode { Field = field, Op = ComparisonOperator.NotIn, Values = [Sample(field)] });
        Assert.DoesNotContain("::", notInWhere, StringComparison.Ordinal);
        Assert.Contains($"{left} <> ALL($1)", notInWhere, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Null_Element_Stays_Null_In_The_Cast_Array()
    {
        var (where, parameters) = Where(new ComparisonNode
        {
            Field = "unitCost",
            Op = ComparisonOperator.In,
            Values = [new DecimalValue { Value = "1.50" }, new NullValue()]
        });

        Assert.Equal("\"unit_cost\" = ANY($1::numeric[])", where);
        var array = Assert.IsAssignableFrom<IReadOnlyList<object?>>(Assert.Single(parameters));
        Assert.Equal(["1.50", null], array);
    }

    [Theory]
    [InlineData("mssql")]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    [InlineData("oracle")]
    public void The_Other_Four_Dialects_Render_A_Decimal_Set_Exactly_As_Before(string name)
    {
        ISqlDialect dialect = name switch
        {
            "mssql" => MssqlDialect.Instance,
            "mysql" => MySqlDialect.Instance,
            "sqlite" => SqliteDialect.Instance,
            _ => OracleDialect.Instance
        };
        var filter = new ComparisonNode
        {
            Field = "unitCost",
            Op = ComparisonOperator.In,
            Values = [new DecimalValue { Value = "1.50" }, new DecimalValue { Value = "2.00" }]
        };

        var query = SqlQueryBuilder.Build(
            new SqlQueryRequest { Schema = Schema, Filter = filter, Page = new RangePage { StartRow = 0, EndRow = 10 } },
            dialect);

        Assert.DoesNotContain("::", query.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("ANY", query.Sql, StringComparison.Ordinal);
        Assert.Contains(" IN (", query.Sql, StringComparison.Ordinal);
        Assert.Contains("1.50", query.Parameters.Select(p => p?.ToString()));
    }

    // ---- inRange -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(true, true, "(\"age\" >= $1 AND \"age\" <= $2)")]
    [InlineData(true, false, "(\"age\" >= $1 AND \"age\" < $2)")]
    [InlineData(false, true, "(\"age\" > $1 AND \"age\" <= $2)")]
    [InlineData(false, false, "(\"age\" > $1 AND \"age\" < $2)")]
    public void InRange_Spells_Each_Bound_From_Its_Own_Explicit_Inclusivity_Flag(
        bool fromInclusive, bool toInclusive, string expected)
    {
        var (where, _) = Where(new ComparisonNode
        {
            Field = "age",
            Op = ComparisonOperator.InRange,
            From = new Int64Value { Value = 1 },
            To = new Int64Value { Value = 9 },
            FromInclusive = fromInclusive,
            ToInclusive = toInclusive
        });

        Assert.Equal(expected, where);
    }

    // ---- scalar comparison: the placeholder is cast to the column type for the three string-encoded types (#2320, SQL-8)

    [Theory]
    [InlineData("unitCost", "numeric")]
    [InlineData("birthDate", "date")]
    [InlineData("createdAt", "timestamptz")]
    public void Scalar_Comparisons_Cast_The_Placeholder_For_Decimal_Date_And_Datetime(string field, string type)
    {
        var column = Schema.Fields[field].Physical!.Sql!.Column;

        foreach (var (op, symbol) in new[]
        {
            (ComparisonOperator.Eq, "="), (ComparisonOperator.Gt, ">"), (ComparisonOperator.Gte, ">="),
            (ComparisonOperator.Lt, "<"), (ComparisonOperator.Lte, "<=")
        })
        {
            var (where, parameters) = Where(Comparison(field, op, Sample(field)));
            Assert.Equal($"\"{column}\" {symbol} $1::{type}", where);

            // The cast is in the SQL text only: still one string parameter (AR-7).
            Assert.IsType<string>(Assert.Single(parameters));
        }

        var (neq, _) = Where(Comparison(field, ComparisonOperator.Neq, Sample(field)));
        Assert.Equal($"(\"{column}\" <> $1::{type} OR \"{column}\" IS NULL)", neq);
    }

    [Theory]
    [InlineData("status", "\"status\" COLLATE craticula_ci")]
    [InlineData("code", "\"code\"")]
    [InlineData("age", "\"age\"")]
    [InlineData("price", "\"price\"")]
    [InlineData("isActive", "\"is_active\"")]
    public void Scalar_Comparisons_Leave_The_Placeholder_Uncast_For_String_Int64_Double_And_Boolean(string field, string left)
    {
        var (where, _) = Where(Comparison(field, ComparisonOperator.Gte, Sample(field)));

        Assert.Equal($"{left} >= $1", where);
    }

    [Theory]
    [InlineData("unitCost", "numeric")]
    [InlineData("birthDate", "date")]
    [InlineData("createdAt", "timestamptz")]
    public void InRange_Casts_Both_Bounds_And_A_Date_Preset_Shape_Is_The_Same_Half_Open_Range(string field, string type)
    {
        var column = Schema.Fields[field].Physical!.Sql!.Column;

        // A date preset (AG-2) is expanded to exactly this half-open inRange before the SQL compiler sees it.
        var (where, parameters) = Where(new ComparisonNode
        {
            Field = field,
            Op = ComparisonOperator.InRange,
            From = Sample(field),
            To = Sample(field),
            FromInclusive = true,
            ToInclusive = false
        });

        Assert.Equal($"(\"{column}\" >= $1::{type} AND \"{column}\" < $2::{type})", where);
        Assert.Equal(2, parameters.Count);
    }

    [Fact]
    public void A_Null_Ordering_Value_Stays_A_Bound_Null_Under_The_Cast()
    {
        var (where, parameters) = Where(Comparison("unitCost", ComparisonOperator.Gt, new NullValue()));

        Assert.Equal("\"unit_cost\" > $1::numeric", where);
        Assert.Equal([null], parameters);
    }

    /// <summary>GRP-7: <c>eq</c> / <c>neq</c> against a nullValue is the null test, with no parameter.</summary>
    [Theory]
    [InlineData(ComparisonOperator.Eq, "\"unit_cost\" IS NULL")]
    [InlineData(ComparisonOperator.Neq, "\"unit_cost\" IS NOT NULL")]
    public void Equality_With_A_NullValue_Is_The_Null_Test(ComparisonOperator op, string expected)
    {
        var (where, parameters) = Where(Comparison("unitCost", op, new NullValue()));

        Assert.Equal(expected, where);
        Assert.Empty(parameters);
    }

    [Theory]
    [InlineData("mssql")]
    [InlineData("mysql")]
    [InlineData("sqlite")]
    [InlineData("oracle")]
    public void The_Other_Four_Dialects_Render_Scalar_Comparisons_On_The_Three_Types_Without_A_Cast(string name)
    {
        ISqlDialect dialect = name switch
        {
            "mssql" => MssqlDialect.Instance,
            "mysql" => MySqlDialect.Instance,
            "sqlite" => SqliteDialect.Instance,
            _ => OracleDialect.Instance
        };

        foreach (var field in new[] { "unitCost", "birthDate", "createdAt" })
        {
            var query = SqlQueryBuilder.Build(
                new SqlQueryRequest
                {
                    Schema = Schema,
                    Filter = Comparison(field, ComparisonOperator.Gt, Sample(field)),
                    Page = new RangePage { StartRow = 0, EndRow = 100 }
                },
                dialect);

            Assert.DoesNotContain("::", query.Sql, StringComparison.Ordinal);
            Assert.DoesNotContain("CAST(", query.Sql, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ---- tree shape ------------------------------------------------------------------------------------

    [Fact]
    public void A_Composite_Wraps_Itself_And_Renders_Its_Children_Bare()
    {
        var (where, _) = Where(new AndNode
        {
            Nodes =
            [
                Comparison("age", ComparisonOperator.Gt, new Int64Value { Value = 1 }),
                Comparison("age", ComparisonOperator.Lt, new Int64Value { Value = 9 })
            ]
        });

        Assert.Equal("(\"age\" > $1 AND \"age\" < $2)", where);
    }

    [Fact]
    public void A_Single_Child_Composite_Still_Wraps()
    {
        // SQL-2 pins this: collapsing it would make spec/fixtures/filter-tree/011-013's nesting
        // unreproducible, and it is what lets a nested composite compose without its parent
        // inspecting it.
        var (where, _) = Where(new OrNode { Nodes = [Comparison("age", ComparisonOperator.Gt, new Int64Value { Value = 1 })] });

        Assert.Equal("(\"age\" > $1)", where);
    }

    [Fact]
    public void A_Tree_Level_Not_Wraps_The_Child_And_Fans_Out_One_Is_Null_Per_Distinct_Field()
    {
        var (where, _) = Where(new NotNode
        {
            Node = new AndNode
            {
                Nodes =
                [
                    Comparison("status", ComparisonOperator.Eq, new StringValue { Value = "a" }),
                    Comparison("age", ComparisonOperator.Lt, new Int64Value { Value = 9 }),

                    // A repeated field contributes no second disjunct.
                    Comparison("status", ComparisonOperator.Eq, new StringValue { Value = "b" })
                ]
            }
        });

        Assert.Equal(
            "(NOT ((\"status\" COLLATE craticula_ci = $1 AND \"age\" < $2 AND \"status\" COLLATE craticula_ci = $3)) " +
            "OR \"status\" IS NULL OR \"age\" IS NULL)",
            where);
    }

    [Fact]
    public void The_Not_Fan_Out_Follows_First_Appearance_Order_Not_Alphabetical_Or_Hash_Order()
    {
        var (where, _) = Where(new NotNode
        {
            Node = new OrNode
            {
                Nodes =
                [
                    Comparison("age", ComparisonOperator.Gt, new Int64Value { Value = 1 }),
                    Comparison("status", ComparisonOperator.Eq, new StringValue { Value = "a" })
                ]
            }
        });

        Assert.EndsWith("OR \"age\" IS NULL OR \"status\" IS NULL)", where, StringComparison.Ordinal);
    }

    // ---- parameter encoding -------------------------------------------------------------------------------

    [Fact]
    public void A_Decimal_Binds_As_Its_Numeric_String_And_A_Double_As_A_Number()
    {
        // AR-7: the two are the same wire shape and differ only by the schema's declared type.
        // Binding a decimal through a floating type is a silent precision loss, so it stays a string.
        var (_, decimalParameters) = Where(Comparison("unitCost", ComparisonOperator.Eq, new DecimalValue { Value = "19.99" }));
        var (_, doubleParameters) = Where(Comparison("price", ComparisonOperator.Eq, new DoubleValue { Value = 19.99 }));

        Assert.Equal(["19.99"], decimalParameters);
        Assert.Equal([19.99d], doubleParameters);
    }

    [Theory]
    // Already UTC: unchanged, including the trailing Z.
    [InlineData("2026-01-01T00:00:00Z", "2026-01-01T00:00:00Z")]
    // SEM-4: an offset-bearing instant is the SAME instant — normalised so one instant has one
    // artifact, never two spellings.
    [InlineData("2026-01-01T02:00:00+02:00", "2026-01-01T00:00:00Z")]
    // Sub-second precision survives; it is part of the instant, not of the spelling.
    [InlineData("2026-01-01T00:00:00.5Z", "2026-01-01T00:00:00.5Z")]
    public void A_Datetime_Binds_As_A_UTC_Instant_And_Never_Carries_Its_Zone(string value, string expected)
    {
        var (where, parameters) = Where(Comparison("createdAt", ComparisonOperator.Gt,
            new DatetimeValue { Value = value, Zone = "Europe/Paris" }));

        Assert.Equal([expected], parameters);

        // SEM-4: zone is provenance recording how the wire string was resolved — never an
        // instruction to the database, so nothing about it reaches the SQL.
        Assert.DoesNotContain("Paris", where, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Date_Binds_As_Its_Plain_Calendar_String_With_No_Time_Or_Zone_Promotion()
    {
        var (_, parameters) = Where(Comparison("birthDate", ComparisonOperator.Eq, new DateValue { Value = "1990-05-12" }));

        Assert.Equal(["1990-05-12"], parameters);
    }

    [Fact]
    public void Binding_A_Value_Whose_Variant_Disagrees_With_The_Declared_Type_Fails_Loudly()
    {
        // The decimal/double confusion above is invisible in the output, so an uncoerced value is
        // rejected rather than rendered into a plausible-looking artifact.
        var ex = Assert.Throws<SqlCompilationException>(
            () => Compile(Comparison("unitCost", ComparisonOperator.Eq, new DoubleValue { Value = 19.99 })));

        Assert.Contains("TypeCoercionValidator", ex.Message, StringComparison.Ordinal);
    }

    // ---- schema resolution ---------------------------------------------------------------------------------

    [Fact]
    public void Every_Identifier_Is_Quoted_Including_The_Table()
    {
        var query = Compile(Comparison("age", ComparisonOperator.Eq, new Int64Value { Value = 1 }));

        Assert.StartsWith("SELECT * FROM \"items\" WHERE \"age\" = $1", query.Sql, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("qualifiedColumn")]
    [InlineData("expression")]
    public void The_Two_Unimplemented_SqlMapping_Shapes_Are_Rejected_With_A_Typed_Exception(string shape)
    {
        // 752/752 field declarations in spec/fixtures/ use the plain `column` form, so these two have
        // no oracle at all. A clean rejection keeps the gap visible; a guessed rendering would not.
        var schema = new GridSchema
        {
            Source = "items",
            StableKey = "id",
            Fields = new Dictionary<string, FieldConfig>
            {
                ["id"] = Field(FieldType.Int64, "id"),
                ["age"] = new()
                {
                    Type = FieldType.Int64,
                    Physical = new PhysicalMapping
                    {
                        Sql = shape == "qualifiedColumn"
                            ? new SqlMapping { QualifiedColumn = "t.age" }
                            : new SqlMapping { Expression = "EXTRACT(YEAR FROM age(dob))" }
                    }
                }
            }
        };

        var ex = Assert.Throws<SqlCompilationException>(() => SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = schema,
                Filter = Comparison("age", ComparisonOperator.Eq, new Int64Value { Value = 1 }),
                Page = new RangePage { StartRow = 0, EndRow = 100 }
            },
            PostgresDialect.Instance));

        Assert.Contains(shape, ex.Message, StringComparison.Ordinal);
        Assert.Contains("does not implement", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Field_The_Schema_Does_Not_Declare_Is_A_Loud_Precondition_Failure()
    {
        var ex = Assert.Throws<SqlCompilationException>(
            () => Compile(Comparison("unlisted", ComparisonOperator.Eq, new Int64Value { Value = 1 })));

        Assert.Contains("not declared in the GridSchema", ex.Message, StringComparison.Ordinal);
    }

    // ---- SEM-8: an empty set is a constant -------------------------------------------------------

    [Fact]
    public void An_Empty_In_Renders_The_False_Constant_And_Binds_Nothing()
    {
        var (where, parameters) = Where(new ComparisonNode { Field = "status", Op = ComparisonOperator.In, Values = [] });

        Assert.Equal("1 = 0", where);
        Assert.Empty(parameters);
    }

    [Fact]
    public void An_Empty_NotIn_Renders_The_True_Constant_With_No_Null_Disjunct()
    {
        var (where, parameters) = Where(new ComparisonNode { Field = "status", Op = ComparisonOperator.NotIn, Values = [] });

        Assert.Equal("1 = 1", where);
        Assert.Empty(parameters);
    }

    [Fact]
    public void A_Not_Around_An_Empty_In_Keeps_SEM_2s_Null_Fan_Out()
    {
        var (where, _) = Where(new NotNode
        {
            Node = new ComparisonNode { Field = "status", Op = ComparisonOperator.In, Values = [] }
        });

        Assert.Equal("(NOT (1 = 0) OR \"status\" IS NULL)", where);
    }
}
