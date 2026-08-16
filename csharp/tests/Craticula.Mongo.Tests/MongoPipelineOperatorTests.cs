using System.Text.Json.Nodes;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Mongo.Tests;

/// <summary>
/// The filter half of <see cref="MongoPipelineBuilder"/>, isolated from sorting and paging: AR-6's
/// operator set, SEM-1's two mechanisms, SEM-2's null inclusion and <c>blank</c>/<c>notBlank</c>,
/// SEM-4's two date representations and SEM-7's escape set.
/// </summary>
public class MongoPipelineOperatorTests
{
    private static FieldConfig Field(FieldType type, string? path = null, CaseSensitivity? caseSensitivity = null) => new()
    {
        Type = type,
        Physical = path is null ? null : new PhysicalMapping { Mongo = new MongoMapping { Path = path } },
        Text = caseSensitivity is null ? null : new TextOptions { CaseSensitivity = caseSensitivity.Value }
    };

    private static readonly GridSchema Schema = new()
    {
        Source = "employees",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = Field(FieldType.Int64),
            ["lastName"] = Field(FieldType.String),
            ["code"] = Field(FieldType.String, caseSensitivity: CaseSensitivity.Sensitive),
            ["age"] = Field(FieldType.Int64),
            ["salary"] = Field(FieldType.Decimal),
            ["ratio"] = Field(FieldType.Double),
            ["active"] = Field(FieldType.Boolean),
            ["hireDate"] = Field(FieldType.Date),
            ["createdAt"] = Field(FieldType.Datetime),
            ["city"] = Field(FieldType.String, "address.city")
        }
    };

    private static MongoQuery Compile(FilterNode filter) =>
        MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = Schema,
            Filter = filter,
            Page = new RangePage { StartRow = 0, EndRow = 100 }
        });

    /// <summary>The <c>$match</c> body alone, compactly — every assertion below is about that one
    /// stage, and re-stating the SEM-3/SEM-6 scaffolding around it would bury the point.</summary>
    private static string Match(FilterNode filter) =>
        MongoFixtureCompiler.CanonicalJson(Compile(filter).Pipeline[0]["$match"]);

    private static ComparisonNode Leaf(string field, ComparisonOperator op, TypedValue? value = null) =>
        new() { Field = field, Op = op, Value = value };

    // ---- the collation family (SEM-1) -------------------------------------------------------------

    [Theory]
    [InlineData(ComparisonOperator.Eq, "$eq")]
    [InlineData(ComparisonOperator.Gt, "$gt")]
    [InlineData(ComparisonOperator.Gte, "$gte")]
    [InlineData(ComparisonOperator.Lt, "$lt")]
    [InlineData(ComparisonOperator.Lte, "$lte")]
    public void A_Non_Null_Inclusive_Scalar_Comparison_Is_A_Single_Field_Document(ComparisonOperator op, string token)
    {
        Assert.Equal($$$"""{"age":{"{{{token}}}":30}}""", Match(Leaf("age", op, new Int64Value { Value = 30 })));
    }

    [Fact]
    public void Neq_Includes_Null_And_Missing_Documents_Through_An_Explicit_Or()
    {
        // SEM-2: MongoDB's own $ne already matches a missing field, so the single-clause form happens
        // to be correct — and SEM-2 emits this composed form anyway, to keep the compiled shape
        // parallel to the SQL/ES ones and to stay valid in an $expr context.
        Assert.Equal(
            """{"$or":[{"age":{"$ne":30}},{"age":null}]}""",
            Match(Leaf("age", ComparisonOperator.Neq, new Int64Value { Value = 30 })));
    }

    [Fact]
    public void NotIn_Is_Null_Inclusive_And_In_Is_Not()
    {
        var values = new TypedValue[] { new StringValue { Value = "a" }, new StringValue { Value = "b" } };

        Assert.Equal(
            """{"lastName":{"$in":["a","b"]}}""",
            Match(new ComparisonNode { Field = "lastName", Op = ComparisonOperator.In, Values = values }));

        Assert.Equal(
            """{"$or":[{"lastName":{"$nin":["a","b"]}},{"lastName":null}]}""",
            Match(new ComparisonNode { Field = "lastName", Op = ComparisonOperator.NotIn, Values = values }));
    }

    [Fact]
    public void A_Case_Insensitive_String_Comparison_Adds_The_Query_Level_Collation()
    {
        var query = Compile(Leaf("lastName", ComparisonOperator.Eq, new StringValue { Value = "smith" }));

        Assert.Equal("""{"locale":"en","strength":2}""", MongoFixtureCompiler.CanonicalJson(query.Collation));
        Assert.False(query.CollationSuppressedByBucketIdentity);
    }

    [Fact]
    public void A_Case_Sensitive_String_Comparison_Spells_Nothing_At_All_When_Nothing_Else_Wants_A_Collation()
    {
        // SC-3's opt-in needs no token here, unlike SQL-6's SQL Server/MySQL/Oracle rows: MongoDB's
        // default comparison is already binary, so "no collation" IS the case-sensitive mechanism —
        // but only in a pipeline that carries no collation, which is the whole of this request. The
        // claim was stated unconditionally until #1643's review, and it is false the moment any other
        // construct turns the query-level option on: see
        // A_Case_Sensitive_Comparison_Beside_A_Case_Insensitive_One_Escapes_Into_Expr, which is the
        // same request plus one CI predicate.
        var query = Compile(Leaf("code", ComparisonOperator.Eq, new StringValue { Value = "AB" }));

        Assert.Null(query.Collation);
        Assert.Equal("""{"code":{"$eq":"AB"}}""", MongoFixtureCompiler.CanonicalJson(query.Pipeline[0]["$match"]));
    }

    // ---- MDB-9: a case-sensitive comparison beside a pipeline-wide collation -----------------------

    private static string MatchOf(params FilterNode[] children) =>
        MongoFixtureCompiler.CanonicalJson(Compile(new AndNode { Nodes = children }).Pipeline[0]["$match"]);

    [Fact]
    public void A_Case_Sensitive_Comparison_Beside_A_Case_Insensitive_One_Escapes_Into_Expr()
    {
        // MDB-9. MongoDB's collation is query-level with no per-stage form, so the CI predicate on
        // `lastName` switches it on for the whole pipeline — including the `code` predicate, whose
        // field is declared caseSensitivity: "sensitive" and which would otherwise compare "AB" equal
        // to "ab". Moving it into $expr takes it out of the query language, where aggregation
        // expressions compare by simple binary comparison and the collation does not apply.
        Assert.Equal(
            """{"$and":[{"lastName":{"$eq":"smith"}},{"$expr":{"$eq":["$code",{"$literal":"AB"}]}}]}""",
            MatchOf(
                Leaf("lastName", ComparisonOperator.Eq, new StringValue { Value = "smith" }),
                Leaf("code", ComparisonOperator.Eq, new StringValue { Value = "AB" })));
    }

    [Theory]
    [InlineData(ComparisonOperator.Neq, "$ne")]
    [InlineData(ComparisonOperator.Gt, "$gt")]
    [InlineData(ComparisonOperator.Gte, "$gte")]
    [InlineData(ComparisonOperator.Lt, "$lt")]
    [InlineData(ComparisonOperator.Lte, "$lte")]
    public void Every_Scalar_Operator_Takes_The_Same_Escape_Not_Just_Eq(ComparisonOperator op, string token)
    {
        // The mechanism is per FIELD, not per operator (SEM-1), so the escape cannot be `eq`-only.
        // `neq` additionally keeps SEM-2's null-inclusive $or around the escaped clause: $expr changes
        // where the comparison is evaluated, not whether null-valued documents match a negative one.
        var expected = op == ComparisonOperator.Neq
            ? $$$"""{"$and":[{"lastName":{"$eq":"smith"}},{"$or":[{"$expr":{"{{{token}}}":["$code",{"$literal":"AB"}]}},{"code":null}]}]}"""
            : $$$"""{"$and":[{"lastName":{"$eq":"smith"}},{"$expr":{"{{{token}}}":["$code",{"$literal":"AB"}]}}]}""";

        Assert.Equal(
            expected,
            MatchOf(
                Leaf("lastName", ComparisonOperator.Eq, new StringValue { Value = "smith" }),
                Leaf("code", op, new StringValue { Value = "AB" })));
    }

    [Fact]
    public void Set_Membership_Escapes_Too_And_Composes_Its_Negation_With_Not()
    {
        // The aggregation language has no $nin, so notIn becomes $not over $in — and the operand
        // order is reversed from the query language's field document, which is the one shape change
        // the escape forces.
        //
        // The membership array is an array EXPRESSION, so each member is bound separately (MDB-9):
        // the elements of an array in expression position are themselves parsed as expressions, so an
        // unwrapped `$`-prefixed member would resolve as a field path exactly as a scalar operand
        // would.
        var values = new TypedValue[] { new StringValue { Value = "AB" }, new StringValue { Value = "CD" } };
        var ci = Leaf("lastName", ComparisonOperator.Eq, new StringValue { Value = "smith" });

        Assert.Equal(
            """{"$and":[{"lastName":{"$eq":"smith"}},{"$expr":{"$in":["$code",[{"$literal":"AB"},{"$literal":"CD"}]]}}]}""",
            MatchOf(ci, new ComparisonNode { Field = "code", Op = ComparisonOperator.In, Values = values }));

        Assert.Equal(
            """{"$and":[{"lastName":{"$eq":"smith"}},{"$or":[{"$expr":{"$not":[{"$in":["$code",[{"$literal":"AB"},{"$literal":"CD"}]]}]}},{"code":null}]}]}""",
            MatchOf(ci, new ComparisonNode { Field = "code", Op = ComparisonOperator.NotIn, Values = values }));
    }

    [Fact]
    public void InRange_Escapes_As_A_Conjunction_Because_The_Expression_Language_Has_No_Field_Document()
    {
        Assert.Equal(
            """{"$and":[{"lastName":{"$eq":"smith"}},{"$expr":{"$and":[{"$gte":["$code",{"$literal":"AB"}]},{"$lt":["$code",{"$literal":"CD"}]}]}}]}""",
            MatchOf(
                Leaf("lastName", ComparisonOperator.Eq, new StringValue { Value = "smith" }),
                new ComparisonNode
                {
                    Field = "code",
                    Op = ComparisonOperator.InRange,
                    From = new StringValue { Value = "AB" },
                    To = new StringValue { Value = "CD" },
                    FromInclusive = true,
                    ToInclusive = false
                }));
    }

    // ---- MDB-9's binding: a `$`-prefixed value is a FIELD PATH in an aggregation expression -------

    /// <summary>The <c>$match</c> body of a request pairing the CI <c>lastName</c> predicate (which
    /// is what turns the pipeline-wide collation on) with one predicate on the case-sensitive
    /// <c>code</c> field (which is therefore what escapes into <c>$expr</c>).</summary>
    private static JsonNode BesideACollation(FilterNode sensitive) =>
        Compile(new AndNode
        {
            Nodes = [Leaf("lastName", ComparisonOperator.Eq, new StringValue { Value = "smith" }), sensitive]
        }).Pipeline[0]["$match"]!;

    [Fact]
    public void A_Dollar_Prefixed_Value_Is_Bound_As_A_Literal_Rather_Than_Resolving_As_A_Field_Path()
    {
        // MDB-9's security half. In an aggregation expression — and ONLY there — a string beginning
        // with `$` is a field path reference; MongoDB's own $literal reference gives
        // `{"$eq": ["$price", "$1"]}` as an equality check against the FIELD named `1`. So this
        // request, unwrapped, would compile to {"$eq": ["$code", "$salary"]} and compare the
        // employee code column against the SALARY column instead of against the four characters the
        // user typed: silently wrong rows, and a client naming any path the grid never exposed
        // (`"$salary.amount"`, `"$$ROOT"`) in a filter box.
        //
        // The query-language arms are unaffected and deliberately not duplicated here: nothing in
        // `{"code": {"$eq": "$salary"}}` is reinterpreted, which is why this is the one binding site.
        var match = BesideACollation(Leaf("code", ComparisonOperator.Eq, new StringValue { Value = "$salary" }));

        Assert.Equal(
            """{"$and":[{"lastName":{"$eq":"smith"}},{"$expr":{"$eq":["$code",{"$literal":"$salary"}]}}]}""",
            MongoFixtureCompiler.CanonicalJson(match));

        // Asserted structurally as well as textually, because the claim is not about spelling: the
        // value operand must be a DOCUMENT the expression parser cannot read as a path, and the field
        // operand must still be the bare reference.
        var operands = match["$and"]![1]!["$expr"]!["$eq"]!.AsArray();

        Assert.Equal("$code", (string?)operands[0]);
        Assert.Equal("$salary", (string?)Assert.IsType<JsonObject>(operands[1])["$literal"]);
    }

    [Fact]
    public void The_Set_And_Range_Escapes_Bind_Dollar_Prefixed_Values_Too()
    {
        // Every escape site, not just the scalar one — a member of an array expression and a range
        // bound are both operand positions and both parse `$salary` as a path.
        Assert.Equal(
            """{"$and":[{"lastName":{"$eq":"smith"}},{"$expr":{"$in":["$code",[{"$literal":"$salary"},{"$literal":"$$ROOT"}]]}}]}""",
            MongoFixtureCompiler.CanonicalJson(BesideACollation(new ComparisonNode
            {
                Field = "code",
                Op = ComparisonOperator.In,
                Values = [new StringValue { Value = "$salary" }, new StringValue { Value = "$$ROOT" }]
            })));

        Assert.Equal(
            """{"$and":[{"lastName":{"$eq":"smith"}},{"$expr":{"$and":[{"$gte":["$code",{"$literal":"$salary"}]},{"$lte":["$code",{"$literal":"$id"}]}]}}]}""",
            MongoFixtureCompiler.CanonicalJson(BesideACollation(new ComparisonNode
            {
                Field = "code",
                Op = ComparisonOperator.InRange,
                From = new StringValue { Value = "$salary" },
                To = new StringValue { Value = "$id" },
                FromInclusive = true,
                ToInclusive = true
            })));
    }

    [Fact]
    public void The_Only_Unbound_Dollar_String_Anywhere_Under_An_Expr_Is_The_Compiler_Minted_Field_Reference()
    {
        // The generalisation of the two tests above, and the one that survives a new escape site being
        // added: whatever the shape, the ONLY string the expression parser is allowed to resolve is
        // the path this compiler minted from the schema. Everything a client supplied must sit under a
        // $literal. A per-shape assertion would pass a future family that forgot the wrapper.
        var match = BesideACollation(new OrNode
        {
            Nodes =
            [
                Leaf("code", ComparisonOperator.Neq, new StringValue { Value = "$salary" }),
                new ComparisonNode
                {
                    Field = "code",
                    Op = ComparisonOperator.NotIn,
                    Values = [new StringValue { Value = "$hireDate" }]
                }
            ]
        });

        var unbound = new List<string>();
        Collect(match["$and"]![1]!, bound: false, unbound);

        Assert.Equal(new[] { "$code", "$code" }, unbound);

        static void Collect(JsonNode? node, bool bound, List<string> unbound)
        {
            switch (node)
            {
                case JsonObject document:
                    foreach (var (key, child) in document)
                    {
                        Collect(child, bound || key == "$literal", unbound);
                    }

                    return;

                case JsonArray array:
                    foreach (var child in array)
                    {
                        Collect(child, bound, unbound);
                    }

                    return;

                case JsonValue value when !bound
                    && value.TryGetValue<string>(out var text) && text.StartsWith('$'):
                    unbound.Add(text);
                    return;
            }
        }
    }

    [Fact]
    public void The_Unary_Boolean_Shorthand_Never_Reaches_The_Escape_Because_A_Boolean_Field_Has_No_Case()
    {
        // AR-6's `true`/`false` mint their own BooleanValue and route through the scalar path, so they
        // are the one place a NON-string value could in principle land in an operand array. They
        // cannot: SEM-1 resolves a non-string field to NotApplicable, never Sensitive, so the
        // shorthand stays in the query language even under a collation. Pinned because MDB-9's
        // "wrap every value whatever its type" rule reads as dead code without it — the rule is
        // deliberately independent of this resolver invariant, not justified by its absence.
        Assert.Equal(
            """{"$and":[{"lastName":{"$eq":"smith"}},{"active":{"$eq":true}}]}""",
            MongoFixtureCompiler.CanonicalJson(BesideACollation(Leaf("active", ComparisonOperator.True))));
    }

    [Fact]
    public void The_Substring_Family_Needs_No_Escape_Because_A_Collation_Never_Reaches_A_Regex()
    {
        // SEM-1 assigns the substring family the pattern-match mechanism precisely because a
        // query-level collation does not govern a $regex, so the case-sensitive `code` predicate is
        // already exact beside the collation the `lastName` predicate turned on. Pinned rather than
        // assumed: it is the reason MDB-9's escape covers four families and not five.
        Assert.Equal(
            """{"$and":[{"lastName":{"$eq":"smith"}},{"code":{"$regex":"AB"}}]}""",
            MatchOf(
                Leaf("lastName", ComparisonOperator.Eq, new StringValue { Value = "smith" }),
                Leaf("code", ComparisonOperator.Contains, new StringValue { Value = "AB" })));
    }

    [Fact]
    public void A_Non_Text_Comparison_Is_Left_In_The_Query_Language_Even_Under_A_Collation()
    {
        // A collation only governs string comparison, so an int64 predicate has nothing to escape
        // from — and escaping it anyway would move an indexable predicate into $expr for no reason.
        Assert.Equal(
            """{"$and":[{"lastName":{"$eq":"smith"}},{"age":{"$gt":30}}]}""",
            MatchOf(
                Leaf("lastName", ComparisonOperator.Eq, new StringValue { Value = "smith" }),
                Leaf("age", ComparisonOperator.Gt, new Int64Value { Value = 30 })));
    }

    [Fact]
    public void The_Wrapper_Shape_Is_Unchanged_By_The_Escape()
    {
        // The escaped predicate is still a case-sensitive comparison, so it contributes nothing to
        // the wrapper decision — the collation is present because of the OTHER predicate, and would
        // be with or without this one.
        var query = Compile(new AndNode
        {
            Nodes =
            [
                Leaf("lastName", ComparisonOperator.Eq, new StringValue { Value = "smith" }),
                Leaf("code", ComparisonOperator.Eq, new StringValue { Value = "AB" })
            ]
        });

        Assert.Equal("""{"locale":"en","strength":2}""", MongoFixtureCompiler.CanonicalJson(query.Collation));
        Assert.False(query.CollationSuppressedByBucketIdentity);
        Assert.IsType<JsonObject>(query.ToArtifact());
    }

    [Fact]
    public void A_Non_Text_Comparison_Never_Reaches_The_Collation_At_All()
    {
        // SEM-1 governs stringValue comparisons only. A pipeline with no text comparison in it uses
        // mongo-artifact.schema.json's bare-array shape, which spec/fixtures/filter-tree/004 pins
        // (its two fields are a double and an int64) after this story corrected it.
        Assert.Null(Compile(Leaf("age", ComparisonOperator.Gt, new Int64Value { Value = 18 })).Collation);
    }

    // ---- the substring family (SEM-1/SEM-7) -------------------------------------------------------

    [Theory]
    [InlineData(ComparisonOperator.Contains, """{"lastName":{"$regex":"art","$options":"i"}}""")]
    [InlineData(ComparisonOperator.StartsWith, """{"lastName":{"$regex":"^art","$options":"i"}}""")]
    [InlineData(ComparisonOperator.EndsWith, """{"lastName":{"$regex":"art$","$options":"i"}}""")]
    public void The_Substring_Family_Compiles_To_Regex_With_The_i_Option(ComparisonOperator op, string expected)
    {
        Assert.Equal(expected, Match(Leaf("lastName", op, new StringValue { Value = "art" })));
    }

    [Fact]
    public void NotContains_Keeps_The_Negation_On_The_Operator_And_Is_Still_Null_Inclusive()
    {
        // Two independent rules in one shape: SQL-3's "the negation belongs to the operator" (so it
        // is $not and not an outer $nor, which is a tree-level notNode's rendering) and SEM-2's
        // null inclusion (so the $or is still there — $not alone drops missing-field documents).
        Assert.Equal(
            """{"$or":[{"lastName":{"$not":{"$regex":"art","$options":"i"}}},{"lastName":null}]}""",
            Match(Leaf("lastName", ComparisonOperator.NotContains, new StringValue { Value = "art" })));
    }

    [Fact]
    public void A_Case_Sensitive_Substring_Match_Drops_The_Options_And_Adds_No_Collation()
    {
        // A query-level collation would not reach a $regex anyway (SEM-1), which is why the substring
        // family resolves to the PatternMatch mechanism and never contributes to the wrapper shape.
        var query = Compile(Leaf("code", ComparisonOperator.Contains, new StringValue { Value = "AB" }));

        Assert.Equal("""{"code":{"$regex":"AB"}}""", MongoFixtureCompiler.CanonicalJson(query.Pipeline[0]["$match"]));
        Assert.Null(query.Collation);
    }

    [Fact]
    public void The_Regex_Escapes_Every_Ascii_Character_Outside_A_Z_0_9_Not_Just_SEC_4s_Four()
    {
        // SEM-7's own Must-NOT: building the $regex from a SEC-4-escaped-only pattern leaves
        // `. + * ( ) [ ] { } | ^ $` live, which both widens the match set beyond the literal the user
        // typed and reopens the ReDoS surface SEC-5 excludes user-controlled regex to avoid.
        Assert.Equal(
            """{"lastName":{"$regex":"50\\%\\ off\\ \\(bonus\\)\\.\\*","$options":"i"}}""",
            Match(Leaf("lastName", ComparisonOperator.Contains, new StringValue { Value = "50% off (bonus).*" })));
    }

    /// <summary>The <c>$regex</c> pattern as it survives a round trip through the artifact text —
    /// serialize, re-parse, read back. Deliberately not <c>MongoRegexEscaping.Escape</c>'s return
    /// value: MDB-11's failure mode happens in the JSON <em>writer</em>, so an assertion on the C#
    /// string alone would have passed on the broken form.</summary>
    private static string RoundTrippedPattern(FilterNode filter)
    {
        var artifact = JsonNode.Parse(MongoFixtureCompiler.CanonicalJson(Compile(filter).Pipeline[0]["$match"]))!;
        return artifact["lastName"]!["$regex"]!.GetValue<string>();
    }

    [Fact]
    public void A_Non_Bmp_Character_Passes_Through_Unescaped_Rather_Than_Being_Split()
    {
        // MDB-11: the escape set stops at ASCII. The alternative reading — "every character outside
        // [A-Za-z0-9]", applied to UTF-16 code units — puts a backslash BETWEEN the two halves of a
        // surrogate pair, leaving two LONE surrogates that System.Text.Json writes as U+FFFD each, so
        // `contains "😀"` compiles to a pattern matching nothing at all, silently.
        var pattern = RoundTrippedPattern(
            Leaf("lastName", ComparisonOperator.Contains, new StringValue { Value = "a\U0001F600b" }));

        Assert.Equal("a\U0001F600b", pattern);
        Assert.DoesNotContain('�', pattern);
    }

    [Fact]
    public void A_Non_Ascii_Letter_Is_Not_Escaped_Either_And_Neighbouring_Ascii_Still_Is()
    {
        // The pair that shows the rule is "ASCII non-alphanumeric", not "not a letter": `é` is left
        // alone (PCRE2 has no metacharacter above U+007F, so escaping it could only change the
        // spelling) while the ASCII `.` beside it is still neutralised.
        Assert.Equal(
            "café\\.",
            RoundTrippedPattern(Leaf("lastName", ComparisonOperator.Contains, new StringValue { Value = "café." })));
    }

    [Fact]
    public void An_Anchor_Cannot_Be_Injected_Through_The_Value()
    {
        // The ^ the user typed is escaped; the ^ startsWith adds is not — which is only true because
        // the anchor is concatenated AFTER the escaping, never included in it.
        Assert.Equal(
            """{"lastName":{"$regex":"^\\^x","$options":"i"}}""",
            Match(Leaf("lastName", ComparisonOperator.StartsWith, new StringValue { Value = "^x" })));
    }

    [Fact]
    public void A_Substring_Match_On_A_Non_String_Field_Is_Refused_Rather_Than_Guessed()
    {
        var failure = Assert.Throws<MongoCompilationException>(() =>
            Compile(Leaf("age", ComparisonOperator.Contains, new StringValue { Value = "3" })));

        Assert.Contains("only has a defined rendering against a 'string' field", failure.Message);
    }

    // ---- the unary operators (SEM-2) --------------------------------------------------------------

    [Fact]
    public void Blank_And_NotBlank_On_A_String_Are_One_Operator_And_Its_Own_Negation()
    {
        // SEM-2's Must-NOT: notBlank is the compiled blank NEGATED, never re-derived. $nin is $in's
        // operator-level negation, so the two cannot drift — which is exactly what
        // spec/fixtures/dialect-parity/019 asserts, and what its artifact said something else about
        // until this story ($nor of a disjunction blank never compiles to).
        Assert.Equal("""{"lastName":{"$in":[null,""]}}""", Match(Leaf("lastName", ComparisonOperator.Blank)));
        Assert.Equal("""{"lastName":{"$nin":[null,""]}}""", Match(Leaf("lastName", ComparisonOperator.NotBlank)));
    }

    [Fact]
    public void Blank_And_NotBlank_On_A_Non_String_Test_Null_Only()
    {
        // SEM-2's fourth Must-NOT: the `= ''` half is text-specific. `{field: null}` already matches
        // a missing field as well as a stored null, so nothing more is needed on either side.
        Assert.Equal("""{"age":null}""", Match(Leaf("age", ComparisonOperator.Blank)));
        Assert.Equal("""{"age":{"$ne":null}}""", Match(Leaf("age", ComparisonOperator.NotBlank)));
    }

    [Theory]
    [InlineData(ComparisonOperator.True, true)]
    [InlineData(ComparisonOperator.False, false)]
    public void The_Unary_Boolean_Shorthand_Compiles_As_An_Equality(ComparisonOperator op, bool expected)
    {
        Assert.Equal(
            $$$"""{"active":{"$eq":{{{(expected ? "true" : "false")}}}}}""",
            Match(Leaf("active", op)));
    }

    // ---- composites (AR-5 / SEM-2) ----------------------------------------------------------------

    [Fact]
    public void A_Tree_Level_Not_Is_Nor_With_No_Null_Fan_Out()
    {
        // SEM-2: $nor already returns documents whose referenced field is null or missing, so the
        // per-field `OR col IS NULL` disjuncts the SQL rendering must add have no counterpart — and
        // adding a `{field: {$ne: null}}` guard "for parity" would EXCLUDE them, the exact
        // SQL-shaped regression SEM-2's second Must-NOT names.
        Assert.Equal(
            """{"$nor":[{"age":{"$eq":30}}]}""",
            Match(new NotNode { Node = Leaf("age", ComparisonOperator.Eq, new Int64Value { Value = 30 }) }));
    }

    [Fact]
    public void A_Single_Child_Composite_Is_Not_Collapsed()
    {
        // The tree's own depth is what spec/fixtures/filter-tree/011-013 pin against SEC-3's
        // boundary; collapsing would make a depth-8 case indistinguishable from a shallower one.
        Assert.Equal(
            """{"$and":[{"age":{"$eq":30}}]}""",
            Match(new AndNode { Nodes = [Leaf("age", ComparisonOperator.Eq, new Int64Value { Value = 30 })] }));
    }

    // ---- values (AR-7 / SEM-4) --------------------------------------------------------------------

    [Fact]
    public void A_Decimal_Is_A_Decimal128_Literal_And_A_Double_Is_A_Json_Number()
    {
        // AR-7: the same wire shape, distinguished only by the schema's declared type. Routing a
        // decimal through a JSON number would be an IEEE double and a silent precision loss no
        // artifact comparison could see once it had happened.
        Assert.Equal(
            """{"salary":{"$gt":{"$numberDecimal":"1000"}}}""",
            Match(Leaf("salary", ComparisonOperator.Gt, new DecimalValue { Value = "1000" })));

        Assert.Equal(
            """{"ratio":{"$gt":0.5}}""",
            Match(Leaf("ratio", ComparisonOperator.Gt, new DoubleValue { Value = 0.5 })));
    }

    [Fact]
    public void A_Date_Is_A_Plain_Iso_String_And_A_Datetime_Is_A_Bson_Date()
    {
        // SEM-4, pinned: every BSON Date is an instant, so a calendar date represented as one would
        // need promoting to "midnight in some zone" — a question a value that was never an instant
        // has no answer to. Lexicographic order on YYYY-MM-DD is already chronological.
        Assert.Equal(
            """{"hireDate":{"$gte":"1990-05-12"}}""",
            Match(Leaf("hireDate", ComparisonOperator.Gte, new DateValue { Value = "1990-05-12" })));

        Assert.Equal(
            """{"createdAt":{"$gte":{"$date":"2026-01-01T00:00:00Z"}}}""",
            Match(Leaf("createdAt", ComparisonOperator.Gte,
                new DatetimeValue { Value = "2026-01-01T00:00:00Z", Zone = "UTC" })));
    }

    [Fact]
    public void A_Datetime_Is_Normalised_To_Utc_And_Its_Zone_Is_Never_Emitted()
    {
        // SEM-4: `zone` is provenance recording how the adapter resolved the wire string, never an
        // instruction. Normalising is also what stops the same instant written +02:00 and written Z
        // from producing two different artifacts.
        Assert.Equal(
            """{"createdAt":{"$gte":{"$date":"2026-01-01T00:00:00Z"}}}""",
            Match(Leaf("createdAt", ComparisonOperator.Gte,
                new DatetimeValue { Value = "2026-01-01T02:00:00+02:00", Zone = "Europe/Paris" })));
    }

    [Fact]
    public void InRange_Reads_Both_Inclusivity_Flags_And_Never_Assumes_Between()
    {
        Assert.Equal(
            """{"age":{"$gt":18,"$lte":65}}""",
            Match(new ComparisonNode
            {
                Field = "age",
                Op = ComparisonOperator.InRange,
                From = new Int64Value { Value = 18 },
                To = new Int64Value { Value = 65 },
                FromInclusive = false,
                ToInclusive = true
            }));
    }

    [Fact]
    public void A_Value_Whose_Variant_Disagrees_With_The_Declared_Type_Is_Refused()
    {
        // Defense in depth against a caller that skipped SEM-5: `{"$numberDecimal": "19.99"}` and
        // `19.99` are different documents, and a compiler that guessed from the value's own shape
        // would produce a plausible-looking artifact.
        var failure = Assert.Throws<MongoCompilationException>(() =>
            Compile(Leaf("salary", ComparisonOperator.Eq, new DoubleValue { Value = 19.99 })));

        Assert.Contains("run TypeCoercionValidator", failure.Message);
    }

    // ---- schema resolution (SC-4) -----------------------------------------------------------------

    [Fact]
    public void A_Nested_Document_Path_Is_Used_Everywhere_The_Field_Appears()
    {
        Assert.Equal(
            """{"address.city":{"$eq":"Paris"}}""",
            Match(Leaf("city", ComparisonOperator.Eq, new StringValue { Value = "Paris" })));
    }

    [Fact]
    public void A_Field_The_Schema_Does_Not_Declare_Is_A_Caller_Precondition_Failure()
    {
        var failure = Assert.Throws<MongoCompilationException>(() =>
            Compile(Leaf("nope", ComparisonOperator.Eq, new StringValue { Value = "x" })));

        Assert.Contains("not declared in the GridSchema", failure.Message);
    }
}
