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
    public void A_Repeated_Sort_Key_Is_Dropped_So_The_First_Entry_Stays_Primary()
    {
        // MDB-10's first collision, superseded by SEM-6 (Bug #2565). `$addFields`, `$sort` and `$unset` are
        // documents, so a repeated `lastName` would have overwritten the first entry and silently made the
        // secondary key primary; it used to be refused. The effective sort now lists a field once, so the
        // request is served and the first occurrence decides: the pipeline equals the one for the first key alone.
        var repeated = Compile([
            Key("lastName", SortDirection.Asc, NullsPosition.Last),
            Key("lastName", SortDirection.Desc, NullsPosition.First)
        ]).Pipeline;
        var once = Compile([Key("lastName", SortDirection.Asc, NullsPosition.Last)]).Pipeline;

        Assert.Equal(once.Select(Json), repeated.Select(Json));
        Assert.Equal(
            """{"$sort":{"__lastNameNullBucket":1,"lastName":1,"__idNullBucket":1,"id":1}}""",
            Json(repeated[1]));
        Assert.Equal("""{"$unset":["__lastNameNullBucket","__idNullBucket"]}""", Json(repeated[2]));
    }

    [Fact]
    public void Keys_After_The_StableKey_Are_Dropped_Because_The_Sort_Is_Already_Total()
    {
        // SEM-6 (Bug #2565): the stable key is unique, so a key after it can never break a tie. The `$sort`
        // document ends at `id`, with no second `id` entry that would overwrite the user's own direction.
        var pipeline = Compile([
            Key("id", SortDirection.Desc, NullsPosition.First),
            Key("lastName", SortDirection.Asc, NullsPosition.Last)
        ]).Pipeline;

        Assert.Equal("""{"$sort":{"__idNullBucket":-1,"id":-1}}""", Json(pipeline[1]));
        Assert.Equal("""{"$unset":"__idNullBucket"}""", Json(pipeline[2]));
    }

    /// <summary>SC-1 / SEC-6 (Bug #2565's review): a dropped key is never looked up, so a sort naming an undeclared column after the stable key must be left unreduced and refused.</summary>
    [Fact]
    public void An_Undeclared_Sort_Field_After_The_StableKey_Is_Still_Refused()
    {
        var exception = Assert.Throws<MongoCompilationException>(() => Compile([
            Key("id", SortDirection.Asc, NullsPosition.Last),
            Key("internalRank", SortDirection.Asc, NullsPosition.Last)
        ]));

        Assert.Contains("not declared", exception.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// SEM-6's exception (#2809): a case-insensitive TEXT stable key with a key after it keeps the user's key and appends the exact tiebreaker on the
    /// same field, which a `$sort` document cannot hold twice (MDB-10). The request is refused, and the message names the real cause: the user named
    /// the stable key once. Numeric and case-sensitive stable keys stop at the key and are not affected.
    /// </summary>
    [Fact]
    public void A_CaseInsensitive_Text_StableKey_With_A_Key_After_It_Is_Refused_With_A_Message_That_Names_The_Cause()
    {
        var textKeySchema = Schema with
        {
            StableKey = "lastName",
            Fields = new Dictionary<string, FieldConfig>
            {
                ["lastName"] = Field(FieldType.String),
                ["salary"] = Field(FieldType.Decimal)
            }
        };

        var exception = Assert.Throws<MongoCompilationException>(() => MongoPipelineBuilder.Build(new MongoQueryRequest
        {
            Schema = textKeySchema,
            Sort =
            [
                Key("lastName", SortDirection.Asc, NullsPosition.Last),
                Key("salary", SortDirection.Asc, NullsPosition.Last)
            ],
            Page = new RangePage { StartRow = 0, EndRow = 100 }
        }));

        Assert.Contains("needs 'lastName' twice", exception.Message, StringComparison.Ordinal);
        Assert.Contains("exact tiebreaker", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_Null_Sort_Item_Is_A_SortItemValidationException_Not_A_Crash()
    {
        Assert.Throws<SortItemValidationException>(() => Compile([Key("id", SortDirection.Asc, NullsPosition.Last), null!]));
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
    /// EXP-1(b): an EMPTY projection is refused rather than rendered. It cannot arrive from
    /// <c>AgGridRequestParser</c>, which refuses an empty <c>exportColumns</c> at the wire — the
    /// reachable population is a hand-built request or a future non-AG-Grid adapter, exactly the
    /// population the <c>Projection</c>+<c>Group</c> guard beside it already exists for. Unguarded it
    /// renders <c>{"$project": {"_id": 0}}</c>, which is not "project nothing" but a stage that
    /// suppresses everything, and which older server versions reject outright.
    /// </summary>
    [Fact]
    public void An_Empty_Export_Projection_Is_Refused_Rather_Than_Rendered_As_A_Bare_Id_Suppression()
    {
        var ex = Assert.Throws<MongoCompilationException>(() => MongoPipelineBuilder.Build(
            new MongoQueryRequest { Schema = Schema, Page = new NonePage(), Projection = [] }));

        Assert.Contains("empty EXP-1(b) export projection", ex.Message, StringComparison.Ordinal);
    }
}
