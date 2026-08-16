using System.Text.Json.Nodes;
using Craticula.AgGrid;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Mongo.Tests;

/// <summary>
/// The sorting and paging halves of <see cref="MongoPipelineBuilder"/>, isolated from the filter
/// tree — SEM-3's null-bucket emulation (which MongoDB needs and no other backend in this project
/// does), SEM-6's stable-key tiebreaker, and AG-3/EXP-2's two page modes.
/// </summary>
public class MongoPipelineSortAndPagingTests
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
            ["salary"] = Field(FieldType.Decimal),
            ["city"] = Field(FieldType.String, "address.city")
        }
    };

    private static MongoQuery Compile(IReadOnlyList<SortItem>? sort, PageSpec? page = null) =>
        MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = Schema,
            Sort = sort,
            Page = page ?? new RangePage { StartRow = 0, EndRow = 100 }
        });

    private static SortItem Key(string field, SortDirection direction, NullsPosition nulls) =>
        new() { Field = field, Direction = direction, NullsPosition = nulls };

    private static string Json(JsonNode? node) => MongoFixtureCompiler.CanonicalJson(node);

    [Fact]
    public void No_Sort_At_All_Still_Orders_By_The_Stable_Key_With_Its_Own_Null_Bucket()
    {
        // SEM-6 plus its fourth Must-NOT: the tiebreaker is never optional, and neither is its
        // null-ordering marker — grid-schema.schema.json has no way to declare NOT NULL, so there is
        // no schema state that would license skipping the bucket.
        var pipeline = Compile(sort: null).Pipeline;

        Assert.Equal(
            """{"$addFields":{"__idNullBucket":{"$cond":[{"$eq":["$id",null]},1,0]}}}""",
            Json(pipeline[0]));
        Assert.Equal("""{"$sort":{"__idNullBucket":1,"id":1}}""", Json(pipeline[1]));
        Assert.Equal("""{"$unset":"__idNullBucket"}""", Json(pipeline[2]));
    }

    [Fact]
    public void The_Bucket_Key_Precedes_Its_Field_And_Every_Pair_Keeps_Request_Order()
    {
        // SEM-3's own canonical order: the bucket resolves null-vs-non-null placement BEFORE the
        // field's own value breaks ties among the non-nulls. $sort key order is significant, which is
        // why this suite compares the stage's serialized text rather than its members.
        var pipeline = Compile([
            Key("lastName", SortDirection.Asc, NullsPosition.Last),
            Key("salary", SortDirection.Desc, NullsPosition.First)
        ]).Pipeline;

        Assert.Equal(
            """{"$sort":{"__lastNameNullBucket":1,"lastName":1,"__salaryNullBucket":-1,"salary":-1,"__idNullBucket":1,"id":1}}""",
            Json(pipeline[1]));
    }

    [Fact]
    public void The_Buckets_Sign_Follows_Nulls_Position_Independently_Of_The_Fields_Direction()
    {
        // The bucket is 1 for null and 0 otherwise, so ascending on it puts nulls last WHICHEVER way
        // the real key runs. A compiler that copied the field's direction onto the bucket would place
        // nulls correctly on ascending keys and silently invert them on descending ones.
        var ascendingNullsFirst = Compile([Key("lastName", SortDirection.Asc, NullsPosition.First)]).Pipeline[1];
        var descendingNullsLast = Compile([Key("lastName", SortDirection.Desc, NullsPosition.Last)]).Pipeline[1];

        Assert.Equal(
            """{"$sort":{"__lastNameNullBucket":-1,"lastName":1,"__idNullBucket":1,"id":1}}""",
            Json(ascendingNullsFirst));
        Assert.Equal(
            """{"$sort":{"__lastNameNullBucket":1,"lastName":-1,"__idNullBucket":1,"id":1}}""",
            Json(descendingNullsLast));
    }

    [Fact]
    public void Several_Buckets_Are_Unset_In_One_Stage_As_An_Array()
    {
        var pipeline = Compile([Key("lastName", SortDirection.Asc, NullsPosition.Last)]).Pipeline;

        Assert.Equal("""{"$unset":["__lastNameNullBucket","__idNullBucket"]}""", Json(pipeline[2]));
    }

    [Fact]
    public void The_Bucket_Is_Named_For_The_Logical_Field_And_Computed_From_The_Physical_Path()
    {
        // SC-4: the synthetic field is the compiler's own, so it takes the colId; the $cond has to
        // read the stored document, so it takes the path. Conflating them would either leak the
        // physical name into a key or read a field that does not exist.
        var pipeline = Compile([Key("city", SortDirection.Asc, NullsPosition.Last)]).Pipeline;

        Assert.Equal(
            """{"$addFields":{"__cityNullBucket":{"$cond":[{"$eq":["$address.city",null]},1,0]},"__idNullBucket":{"$cond":[{"$eq":["$id",null]},1,0]}}}""",
            Json(pipeline[0]));
        Assert.Equal(
            """{"$sort":{"__cityNullBucket":1,"address.city":1,"__idNullBucket":1,"id":1}}""",
            Json(pipeline[1]));
    }

    [Fact]
    public void A_Sort_That_Already_Ends_In_The_Stable_Key_Gets_No_Second_Tiebreaker()
    {
        var pipeline = Compile([
            Key("lastName", SortDirection.Asc, NullsPosition.Last),
            Key("id", SortDirection.Desc, NullsPosition.First)
        ]).Pipeline;

        Assert.Equal(
            """{"$sort":{"__lastNameNullBucket":1,"lastName":1,"__idNullBucket":-1,"id":-1}}""",
            Json(pipeline[1]));
    }

    [Fact]
    public void The_Appended_Tiebreaker_Never_Asks_For_The_Collation_But_A_User_Key_On_It_Does()
    {
        // SEM-6, as narrowed by #1640's review: the APPENDED key resolves exact, because a stableKey
        // is unique under its storage collation and re-deciding row identity under another one leaves
        // exactly the ties the tiebreaker exists to break. A key the user chose on that same field is
        // an ordinary sort key and keeps SEM-1 in full.
        var stringStableKey = new GridSchema
        {
            Source = "employees",
            StableKey = "lastName",
            Fields = new Dictionary<string, FieldConfig> { ["lastName"] = Field(FieldType.String) }
        };

        MongoQuery Build(IReadOnlyList<SortItem>? sort) => MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = stringStableKey,
            Sort = sort,
            Page = new RangePage { StartRow = 0, EndRow = 100 }
        });

        Assert.Null(Build(null).Collation);
        Assert.NotNull(Build([Key("lastName", SortDirection.Asc, NullsPosition.Last)]).Collation);
    }

    // ---- MDB-9 / MDB-10: the two things a $sort document cannot express ---------------------------

    private static readonly GridSchema MixedCaseSensitivity = new()
    {
        Source = "employees",
        StableKey = "id",
        Fields = new Dictionary<string, FieldConfig>
        {
            ["id"] = Field(FieldType.Int64),
            ["lastName"] = Field(FieldType.String),
            ["employeeCode"] = Field(FieldType.String, caseSensitivity: CaseSensitivity.Sensitive)
        }
    };

    [Fact]
    public void A_Case_Sensitive_Sort_Key_Under_A_Collation_Is_Refused_Rather_Than_Folded()
    {
        // MDB-9's one unrenderable corner. A comparison can escape a pipeline-wide collation through
        // $expr; a $sort cannot — the aggregation language has no "order these documents" expression
        // — so ordering `employeeCode` beside a CI filter on `lastName` would silently sort "AB"
        // next to "ab" on the one field whose schema says not to. SQL-7(b)'s register: refuse.
        var failure = Assert.Throws<MongoCompilationException>(() => MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = MixedCaseSensitivity,
            Filter = new ComparisonNode
            {
                Field = "lastName",
                Op = ComparisonOperator.Eq,
                Value = new StringValue { Value = "smith" }
            },
            Sort = [Key("employeeCode", SortDirection.Asc, NullsPosition.Last)],
            Page = new RangePage { StartRow = 0, EndRow = 100 }
        }));

        Assert.Contains("employeeCode", failure.Message);
        Assert.Contains("MDB-9", failure.Message);
    }

    [Fact]
    public void The_Same_Sort_Key_Compiles_Fine_When_Nothing_Turns_The_Collation_On()
    {
        // The refusal above is about the CONFLICT, not about the sort key: with no case-insensitive
        // construct anywhere, "spell nothing" is exact on MongoDB and the same request compiles.
        var pipeline = MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = MixedCaseSensitivity,
            Sort = [Key("employeeCode", SortDirection.Asc, NullsPosition.Last)],
            Page = new RangePage { StartRow = 0, EndRow = 100 }
        }).Pipeline;

        Assert.Equal(
            """{"$sort":{"__employeeCodeNullBucket":1,"employeeCode":1,"__idNullBucket":1,"id":1}}""",
            Json(pipeline[1]));
    }

    [Fact]
    public void A_Repeated_Sort_Key_Is_Refused_Because_A_Sort_Document_Would_Keep_Only_The_Last()
    {
        // MDB-10. `$addFields`, `$sort` and `$unset` are documents, so `lastName asc, lastName desc`
        // does not "collapse to the first" — the SECOND entry overwrites the first, silently making
        // the secondary key primary, and `$unset` names the bucket twice. A grid can send a repeated
        // colId in `sortModel`, so this is reachable from the wire.
        var failure = Assert.Throws<MongoCompilationException>(() => Compile([
            Key("lastName", SortDirection.Asc, NullsPosition.Last),
            Key("lastName", SortDirection.Desc, NullsPosition.First)
        ]));

        Assert.Contains("names 'lastName' more than once", failure.Message);
    }

    // ---- paging (AG-3 / EXP-2) --------------------------------------------------------------------

    [Fact]
    public void A_Range_Page_Skips_The_Start_Row_And_Limits_The_Row_Count()
    {
        // AG-3: endRow is EXCLUSIVE, so the limit is the difference. An asymmetric window is used
        // deliberately — with startRow == count a compiler that bound endRow would still pass.
        var pipeline = Compile(sort: null, new RangePage { StartRow = 30, EndRow = 100 }).Pipeline;

        Assert.Equal("""{"$skip":30}""", Json(pipeline[^2]));
        Assert.Equal("""{"$limit":70}""", Json(pipeline[^1]));
    }

    [Fact]
    public void A_Zero_Offset_Still_Emits_The_Skip_Stage()
    {
        var pipeline = Compile(sort: null, new RangePage { StartRow = 0, EndRow = 100 }).Pipeline;

        Assert.Equal("""{"$skip":0}""", Json(pipeline[^2]));
    }

    [Fact]
    public void A_None_Page_Emits_Neither_Stage_So_The_Pipeline_Ends_After_The_Unset()
    {
        var pipeline = Compile(sort: null, new NonePage()).Pipeline;

        Assert.Equal(3, pipeline.Count);
        Assert.Equal("""{"$unset":"__idNullBucket"}""", Json(pipeline[^1]));
    }

    /// <summary>
    /// The builder's half of <c>sort-paging/012</c>, which the conformance suite excludes by name:
    /// the fixture resolves <c>page: none</c> from EXP-1's <c>exportScope: "all"</c>, and
    /// <see cref="AgGridRequestParser"/> has no <c>exportScope</c> handling. Everything except that
    /// one substitution runs end to end here, so the artifact is not left unexercised while the
    /// upstream gap stands — the exact arrangement
    /// <c>Craticula.Sql.Tests.SqlQueryBuilderPageAndSortTests</c> makes for the same fixture.
    /// </summary>
    [Fact]
    public void SortPaging012_Compiles_To_Its_Own_Artifact_Once_The_Page_Is_NonePage()
    {
        const string category = "sort-paging";
        const string fixtureDir = "012-page-none-unpaginated-export";

        var schema = GridSchema.Parse(File.ReadAllText(RepoPaths.Fixture(category, fixtureDir, "schema.json")));
        var parsed = AgGridRequestParser.Parse(
            File.ReadAllText(RepoPaths.Fixture(category, fixtureDir, "agGridRequest.json")),
            new AgGridParseOptions { Schema = schema });

        // The one substitution, and the whole content of the upstream gap: the parser reports the
        // RangePage the literal startRow/endRow describe, because it does not read exportScope.
        Assert.IsType<RangePage>(parsed.Page);

        var filter = GridFilterPipeline.Process(
            parsed.NativeFilter, parsed.CustomFilters, parsed.Page, schema, new ResourceLimits());

        var query = MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = schema,
            Filter = filter,
            Sort = parsed.Sort,
            Page = new NonePage()
        });

        Assert.Equal(
            MongoFixtureCompiler.CanonicalJson(
                File.ReadAllText(RepoPaths.Fixture(category, fixtureDir, "expected", "mongo.artifact.json"))),
            MongoFixtureCompiler.CanonicalJson(query.ToArtifact()));
    }
}
