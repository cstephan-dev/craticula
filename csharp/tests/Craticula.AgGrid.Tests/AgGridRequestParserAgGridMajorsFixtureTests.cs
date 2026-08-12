using System.Text.Json;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.AgGrid.Tests;

/// <summary>
/// Fixture-driven coverage against the real <c>spec/fixtures/ag-grid-majors/</c> suite (8 cases):
/// AG-4's "one payload per supported AG Grid major v31-v36 parses successfully through the adapter",
/// plus the two rejections every major can put on the wire — AG-5's Advanced Filter root and SEM-4's
/// native <c>inRange</c>.
///
/// <para>Every assertion here is bound to the fixture's <em>own</em> authored data — its
/// <c>agGridRequest.json</c>, its <c>schema.json</c>, its <c>expected/postgres.artifact.json</c>
/// parameters and <c>ORDER BY</c> clause, its <c>expected/search.artifact.json</c> sort terms, its
/// <c>expected/rejection.json</c> reason — rather than to literals transcribed into this file, and
/// never to a value re-derived from the implementation under test. That is what makes this a
/// conformance test instead of a parallel copy of the fixtures that can drift alongside the
/// implementation: if a hand-authored fixture and this parser ever disagree, an assertion fails.
/// An assertion that reuses a production helper to compute its own expectation cannot do that, so
/// there are none here — see the nullsPosition oracle below, which was one until a review caught
/// it.</para>
///
/// <para>Those fixtures are <b>derived</b> from each release tag's published TypeScript interfaces
/// and model-emitting code, not captured from a running grid — see the category README's provenance
/// section before treating a failure here as proof about a real browser payload.</para>
/// </summary>
public class AgGridRequestParserAgGridMajorsFixtureTests
{
    private const string Category = "ag-grid-majors";

    /// <summary>The six valid cases, and the reference-clone tag each one's shape was derived from.
    /// (The tag column is not decoration: it is asserted against each fixture's own
    /// <c>description.md</c> below, so a case can never quietly end up claiming a different major
    /// than the one this table exercises it as.)</summary>
    public static readonly TheoryData<string, string> AllSupportedMajorFixtures = new()
    {
        { "001-v31-combined-filter-dual-emission", "v31.0.0" },
        { "002-v32-modern-conditions-form", "v32.0.0" },
        { "003-v33-text-conditions-and-set-filter", "v33.0.0" },
        { "004-v34-grouped-request-with-date-conditions", "release-34.0.0" },
        { "005-v35-multi-filter-desc-sort-second-page", "release-35.0.0" },
        { "006-v36-date-preset-and-conditions", "release-36.0.0" }
    };

    /// <summary>The category's rejection-shaped cases: each carries <c>expected/rejection.json</c>
    /// instead of backend artifacts (see <c>spec/fixtures/README.md</c>), so they cannot ride the
    /// artifact-driven theories above. Both pin a wire shape ANY supported major can emit — 007 an
    /// Advanced Filter root (AG-5), 008 a native <c>inRange</c> whose inclusivity AG Grid never puts
    /// on the wire (SEM-4) — which is why neither is tied to a single tag.</summary>
    private static readonly string[] RejectionFixtureDirs =
    [
        "007-advanced-filter-model-rejected",
        "008-native-inrange-rejected",
    ];

    public static TheoryData<string> RejectionFixtures => [.. RejectionFixtureDirs];

    private static string Read(string fixtureDir, params string[] rest) =>
        File.ReadAllText(RepoPaths.Fixture([Category, fixtureDir, .. rest]));

    private static JsonDocument RequestOf(string fixtureDir) => JsonDocument.Parse(Read(fixtureDir, "agGridRequest.json"));

    /// <summary>
    /// The harness setup a port must reproduce: the fixture's own <c>schema.json</c>, plus — for the
    /// one case carrying an AG-2 date preset — the clock pinned to its own <c>requestContext.now</c>.
    /// A fixture without a <c>requestContext</c> needs no pinned clock, since nothing in it resolves
    /// against "now"; it still gets the schema, so field types are never guessed.
    /// </summary>
    private static AgGridParseOptions OptionsFor(string fixtureDir)
    {
        using var request = RequestOf(fixtureDir);
        var pinnedNow = request.RootElement.TryGetProperty("requestContext", out var context)
            ? context.GetProperty("now").GetString()
            : null;

        return new AgGridParseOptions
        {
            Clock = pinnedNow is null ? TimeProvider.System : FixedClock.At(pinnedNow),
            Schema = GridSchema.Parse(Read(fixtureDir, "schema.json"))
        };
    }

    private static AgGridParsedRequest Parse(string fixtureDir) =>
        AgGridRequestParser.Parse(Read(fixtureDir, "agGridRequest.json"), OptionsFor(fixtureDir));

    [Theory]
    [MemberData(nameof(AllSupportedMajorFixtures))]
    public void Every_Supported_Major_Parses_Into_A_Filter_Tree(string fixtureDir, string tag)
    {
        _ = tag;

        var parsed = Parse(fixtureDir);

        // AG-4's Definition of Done, literally: the payload parses. Every case here filters, and none
        // uses the customFilters extension — each is a payload a real grid of that major emits alone.
        Assert.NotNull(parsed.NativeFilter);
        Assert.Null(parsed.CustomFilters);
    }

    [Theory]
    [MemberData(nameof(AllSupportedMajorFixtures))]
    public void Every_Supported_Major_Produces_The_Filter_Values_Its_Own_Artifact_Pins(string fixtureDir, string tag)
    {
        _ = tag;

        var leafValues = LeafValues(Parse(fixtureDir).NativeFilter).ToList();

        // The fixture's own compiled artifact is the oracle: its bound parameters, in tree order, are
        // exactly the values the parsed IR's leaves carry. A parser that drops a condition, duplicates
        // one, or reorders them produces a different sequence and fails here.
        Assert.Equal(ExpectedFilterParams(fixtureDir), leafValues);
    }

    [Theory]
    [MemberData(nameof(AllSupportedMajorFixtures))]
    public void Every_Supported_Majors_Envelope_Parses_Into_The_Sort_Group_Aggregation_And_Page_It_Declares(
        string fixtureDir, string tag)
    {
        _ = tag;

        using var request = RequestOf(fixtureDir);
        var root = request.RootElement;
        var parsed = Parse(fixtureDir);

        // sortModel -> SortItem[], with SEM-3's nullsPosition filled in by the adapter (AG Grid's own
        // SortModelItem carries no null-placement field, in any major).
        //
        // The nullsPosition oracle is the fixture's own compiled artifacts, NOT
        // SemanticsResolver.DefaultNullsPosition: the parser calls that same method, so asserting
        // against it would be the implementation agreeing with itself. Both backends pin the answer
        // independently — Postgres as the ORDER BY term's NULLS FIRST/LAST, Search as the sort
        // entry's missing: "_first"/"_last" — and a hand-authored fixture is free to disagree with
        // the parser, which is the whole point.
        var wireSort = root.GetProperty("sortModel").EnumerateArray().ToList();
        var orderByTerms = PostgresOrderByTerms(fixtureDir);
        var searchSortTerms = SearchSortTerms(fixtureDir);

        Assert.Equal(wireSort.Count, parsed.Sort?.Count ?? 0);
        for (var i = 0; i < wireSort.Count; i++)
        {
            var expectedDirection = wireSort[i].GetProperty("sort").GetString() == "asc"
                ? SortDirection.Asc
                : SortDirection.Desc;

            Assert.Equal(wireSort[i].GetProperty("colId").GetString(), parsed.Sort![i].Field);
            Assert.Equal(expectedDirection, parsed.Sort[i].Direction);

            // The wire keys are listed first and SEM-6's derived tiebreaker is appended after them,
            // so ORDER BY term i corresponds to sortModel entry i.
            var expectedNulls = NullsPositionIn(orderByTerms[i]);
            Assert.Equal(expectedNulls, parsed.Sort[i].NullsPosition);

            // ...and the Search artifact must not have been authored to a different answer. (Absent
            // on the grouped case, whose ordering lives in a bucket_sort/composite instead.)
            if (searchSortTerms is { } terms)
            {
                Assert.Equal(
                    expectedNulls == NullsPosition.First ? "_first" : "_last",
                    terms[i].EnumerateObject().Single().Value.GetProperty("missing").GetString());
            }
        }

        // rowGroupCols/valueCols -> GroupSpec/Aggregation[] (BM-2/AR-8), keyed on each ColumnVO's
        // 'field'. Absent group columns must leave Group null rather than an empty spec.
        var wireGroupCols = root.GetProperty("rowGroupCols").EnumerateArray().Select(FieldOf).ToList();
        Assert.Equal(wireGroupCols.Count == 0 ? null : wireGroupCols, parsed.Group?.GroupCols);
        Assert.Equal(
            root.GetProperty("groupKeys").GetArrayLength(),
            parsed.Group?.GroupKeys.Count ?? 0);

        var wireValueCols = root.GetProperty("valueCols").EnumerateArray().ToList();
        Assert.Equal(wireValueCols.Count, parsed.Aggregations?.Count ?? 0);
        for (var i = 0; i < wireValueCols.Count; i++)
        {
            Assert.Equal(FieldOf(wireValueCols[i]), parsed.Aggregations![i].Field);
            Assert.Equal(
                wireValueCols[i].GetProperty("aggFunc").GetString(),
                parsed.Aggregations[i].Func.ToString().ToLowerInvariant());
        }

        // AG-3: startRow/endRow -> RangePage, endRow exclusive.
        var page = Assert.IsType<RangePage>(parsed.Page);
        Assert.Equal(root.GetProperty("startRow").GetInt32(), page.StartRow);
        Assert.Equal(root.GetProperty("endRow").GetInt32(), page.EndRow);

        // ...and the artifact's own trailing parameters agree on what that page is: the suite-wide
        // convention is LIMIT (endRow - startRow) OFFSET startRow, both bound as parameters.
        Assert.Equal(
            [page.EndRow - page.StartRow, page.StartRow],
            PostgresParams(fixtureDir).TakeLast(2).Select(p => p.GetInt32()));
    }

    [Theory]
    [MemberData(nameof(AllSupportedMajorFixtures))]
    public void Every_Supported_Major_Filters_Only_Fields_Its_Own_Schema_Declares(string fixtureDir, string tag)
    {
        _ = tag;

        var schema = GridSchema.Parse(Read(fixtureDir, "schema.json"));

        foreach (var field in LeafFields(Parse(fixtureDir).NativeFilter))
        {
            Assert.True(
                schema.Fields.ContainsKey(field),
                $"{fixtureDir}: the request filters '{field}', which its own schema.json does not declare.");
        }
    }

    /// <summary>
    /// The substantive point of case 001, and the reason v31 needs its own fixture at all: a real v31
    /// combined model carries <c>condition1</c>, <c>condition2</c> AND <c>conditions</c> at the same
    /// time (<c>SimpleFilter.getModelFromUi()</c> emits all three), where <c>conditions</c> holds
    /// every condition and the legacy pair holds only the first two. Reading the legacy pair silently
    /// drops the third; reading both double-counts the first two. Only reading <c>conditions</c> is
    /// correct — which is what <see cref="AgGridRequestParser"/> does by checking it first.
    /// </summary>
    [Fact]
    public void Fixture001_Reads_Conditions_And_Ignores_The_Legacy_Pair_v31_Emits_Alongside_It()
    {
        const string fixtureDir = "001-v31-combined-filter-dual-emission";

        using var request = RequestOf(fixtureDir);
        var entry = request.RootElement.GetProperty("filterModel").EnumerateObject().Single().Value;

        // Guard the fixture itself: it only proves anything while it really is dual-emission, with
        // more conditions than the legacy pair can hold.
        Assert.True(entry.TryGetProperty("condition1", out var condition1));
        Assert.True(entry.TryGetProperty("condition2", out _));
        var wireConditions = entry.GetProperty("conditions").EnumerateArray().ToList();
        Assert.True(
            wireConditions.Count > 2,
            "This case is only meaningful while `conditions` holds more than the legacy pair can carry.");

        var orNode = Assert.IsType<OrNode>(Parse(fixtureDir).NativeFilter);

        Assert.Equal(wireConditions.Count, orNode.Nodes.Count);
        Assert.Equal(
            wireConditions.Select(condition => condition.GetProperty("filter").GetString()),
            orNode.Nodes.Select(node => Assert.IsType<StringValue>(Assert.IsType<ComparisonNode>(node).Value).Value));

        // The legacy pair's first slot is a duplicate of conditions[0]; it must appear once, not twice.
        var duplicated = condition1.GetProperty("filter").GetString();
        Assert.Equal(1, LeafValues(orNode).Count(value => value == duplicated));
    }

    /// <summary>
    /// Case 006's v36-only ingredient: a date preset is resolved by the ADAPTER into the absolute
    /// half-open range AG Grid's own published spec table defines, so no compiler ever sees an
    /// unresolved preset. The bounds are read from the fixture's own artifact, not restated here.
    /// </summary>
    [Fact]
    public void Fixture006_Resolves_The_v36_Only_Preset_To_The_Half_Open_Range_Its_Own_Artifact_Pins()
    {
        const string fixtureDir = "006-v36-date-preset-and-conditions";

        var andNode = Assert.IsType<AndNode>(Parse(fixtureDir).NativeFilter);
        var resolved = Assert.IsType<ComparisonNode>(andNode.Nodes[0]);
        var parameters = PostgresParams(fixtureDir);

        Assert.Equal(ComparisonOperator.InRange, resolved.Op);
        Assert.True(resolved.FromInclusive, "AG Grid's preset spec table is half-open [from, to).");
        Assert.False(resolved.ToInclusive, "AG Grid's preset spec table is half-open [from, to).");
        Assert.Equal(parameters[0].GetString(), Assert.IsType<DateValue>(resolved.From).Value);
        Assert.Equal(parameters[1].GetString(), Assert.IsType<DateValue>(resolved.To).Value);
    }

    /// <summary>
    /// Presets were introduced in <c>release-36.0.0</c> and do not exist in v31-v35, so a fixture
    /// claiming to be a v31-v35 payload must not contain one — it would be a payload that major
    /// cannot emit. Checked against <see cref="DatePresetResolver.PresetNames"/> rather than a
    /// hand-listed vocabulary.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllSupportedMajorFixtures))]
    public void Only_The_v36_Fixture_Carries_A_Date_Preset(string fixtureDir, string tag)
    {
        var presetsFound = WireConditions(fixtureDir)
            .Where(condition => condition.TryGetProperty("type", out var type)
                                && DatePresetResolver.PresetNames.Contains(type.GetString()))
            .Select(condition => condition.GetProperty("type").GetString())
            .ToList();

        if (tag == "release-36.0.0")
        {
            Assert.NotEmpty(presetsFound);
        }
        else
        {
            Assert.True(
                presetsFound.Count == 0,
                $"{fixtureDir} claims tag {tag}, but carries date preset(s) [{string.Join(", ", presetsFound)}] " +
                $"— presets only exist from release-36.0.0 onwards.");
        }
    }

    [Theory]
    [MemberData(nameof(AllSupportedMajorFixtures))]
    public void Every_Supported_Majors_Description_Cites_The_Tag_This_Table_Runs_It_As(string fixtureDir, string tag)
    {
        // The tag is the whole provenance claim of a derived fixture: if description.md and this
        // table ever name different tags, one of them is wrong and the case proves nothing.
        Assert.Contains(tag, Read(fixtureDir, "description.md"), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(RejectionFixtures))]
    public void A_Rejection_Fixture_Is_Rejected_With_The_Reason_Its_Own_Oracle_Pins(string fixtureDir)
    {
        using var oracle = JsonDocument.Parse(Read(fixtureDir, "expected", "rejection.json"));
        var expectedStage = oracle.RootElement.GetProperty("rejectedAt").GetString();
        var expectedReason = oracle.RootElement.GetProperty("reason").GetString();

        Assert.Equal("requestParsing", expectedStage);

        var ex = Assert.Throws<AgGridRequestParseException>(
            () => AgGridRequestParser.Parse(Read(fixtureDir, "agGridRequest.json"), OptionsFor(fixtureDir)));

        Assert.Equal(expectedReason, ex.Reason);
    }

    [Fact]
    public void The_Fixture_Directories_On_Disk_Match_The_Table_Above()
    {
        // Guards the usual fixture-test rot: a case added to spec/fixtures/ but never wired into
        // either theory would otherwise be silently unexercised.
        var onDisk = Directory
            .EnumerateDirectories(RepoPaths.Fixture(Category))
            .Select(Path.GetFileName)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        var declared = AllSupportedMajorFixtures
            .Select(row => (string?)row[0])
            .Concat(RejectionFixtureDirs)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(declared, onDisk);
    }

    // ---- fixture readers -------------------------------------------------------------------------

    private static string FieldOf(JsonElement columnVo) =>
        columnVo.TryGetProperty("field", out var field) ? field.GetString()! : columnVo.GetProperty("id").GetString()!;

    /// <summary>
    /// The fixture's Postgres <c>ORDER BY</c> terms, split on commas — e.g.
    /// <c>["\"salary\" DESC NULLS FIRST", "\"id\" ASC NULLS LAST"]</c>. This is the fixture's own independent
    /// statement of SEM-3 null placement: it was hand-authored from the spec, not produced by the
    /// parser, so it can contradict the parser and fail the test.
    /// </summary>
    private static string[] PostgresOrderByTerms(string fixtureDir)
    {
        using var artifact = JsonDocument.Parse(Read(fixtureDir, "expected", "postgres.artifact.json"));
        var sql = artifact.RootElement.GetProperty("sql").GetString()!;

        var clause = sql[(sql.IndexOf(" ORDER BY ", StringComparison.Ordinal) + " ORDER BY ".Length)..];
        var limitAt = clause.IndexOf(" LIMIT ", StringComparison.Ordinal);
        if (limitAt >= 0)
        {
            clause = clause[..limitAt];
        }

        return [.. clause.Split(',').Select(term => term.Trim())];
    }

    /// <summary>SEM-3's null placement as the fixture's Postgres artifact spells it out. Deliberately
    /// strict: a term with no explicit <c>NULLS</c> clause fails rather than defaulting, because
    /// leaving null placement to the backend's default is the very thing SEM-3 forbids.</summary>
    private static NullsPosition NullsPositionIn(string orderByTerm) => orderByTerm switch
    {
        _ when orderByTerm.EndsWith(" NULLS FIRST", StringComparison.Ordinal) => NullsPosition.First,
        _ when orderByTerm.EndsWith(" NULLS LAST", StringComparison.Ordinal) => NullsPosition.Last,
        _ => throw new Xunit.Sdk.XunitException(
            $"ORDER BY term '{orderByTerm}' states no NULLS placement — SEM-3 requires it to be explicit.")
    };

    /// <summary>The fixture's Search <c>sort</c> entries, or <see langword="null"/> for a grouped case
    /// whose artifact orders buckets instead of documents.</summary>
    private static IReadOnlyList<JsonElement>? SearchSortTerms(string fixtureDir)
    {
        using var artifact = JsonDocument.Parse(Read(fixtureDir, "expected", "search.artifact.json"));

        // Cloned: see PostgresParams below.
        return artifact.RootElement.TryGetProperty("sort", out var sort)
            ? sort.EnumerateArray().Select(term => term.Clone()).ToList()
            : null;
    }

    private static IReadOnlyList<JsonElement> PostgresParams(string fixtureDir)
    {
        using var artifact = JsonDocument.Parse(Read(fixtureDir, "expected", "postgres.artifact.json"));

        // Cloned: a JsonElement does not outlive the JsonDocument that owns it, and this one is
        // disposed on the way out.
        return artifact.RootElement.GetProperty("params").EnumerateArray().Select(p => p.Clone()).ToList();
    }

    /// <summary>
    /// The filter values a fixture's Postgres artifact binds, flattened and rendered as text: every
    /// parameter except the trailing <c>LIMIT</c>/<c>OFFSET</c> pair, with an <c>in</c>-list
    /// parameter (bound as one array) expanded into its elements so it lines up with the one IR leaf
    /// carrying several values.
    /// </summary>
    private static IEnumerable<string> ExpectedFilterParams(string fixtureDir) =>
        PostgresParams(fixtureDir)
            .SkipLast(2)
            .SelectMany(parameter => parameter.ValueKind == JsonValueKind.Array
                ? parameter.EnumerateArray().Select(Text)
                : [Text(parameter)]);

    private static string Text(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    /// <summary>Every condition object on the wire, including the ones nested inside a combined
    /// <c>conditions</c> array, a legacy <c>condition1</c>/<c>condition2</c> pair, or a <c>multi</c>
    /// filter's stack.</summary>
    private static IEnumerable<JsonElement> WireConditions(string fixtureDir)
    {
        using var request = RequestOf(fixtureDir);
        var filterModel = request.RootElement.GetProperty("filterModel");

        // Cloned out of the JsonDocument: the document is disposed when this method returns, and a
        // JsonElement does not outlive its owner.
        return filterModel.EnumerateObject().SelectMany(column => Descend(column.Value)).ToList();

        static IEnumerable<JsonElement> Descend(JsonElement entry)
        {
            foreach (var childProperty in (string[])["conditions", "filterModels"])
            {
                if (entry.TryGetProperty(childProperty, out var children) && children.ValueKind == JsonValueKind.Array)
                {
                    return children.EnumerateArray()
                        .Where(child => child.ValueKind == JsonValueKind.Object)
                        .SelectMany(Descend)
                        .Select(child => child.Clone())
                        .ToList();
                }
            }

            if (entry.TryGetProperty("condition1", out var condition1) && entry.TryGetProperty("condition2", out var condition2))
            {
                return [condition1.Clone(), condition2.Clone()];
            }

            return [entry.Clone()];
        }
    }

    // ---- IR readers ------------------------------------------------------------------------------

    /// <summary>Every leaf value in the tree, in tree order, rendered the way the Postgres artifact
    /// binds it (a <c>decimal</c>/<c>date</c> bound is a string parameter, not a JSON number).</summary>
    private static IEnumerable<string> LeafValues(FilterNode? node) => Leaves(node).SelectMany(leaf =>
    {
        var values = new List<string>();
        if (leaf.Value is { } value)
        {
            values.Add(Render(value));
        }

        if (leaf.From is { } from)
        {
            values.Add(Render(from));
        }

        if (leaf.To is { } to)
        {
            values.Add(Render(to));
        }

        if (leaf.Values is { } list)
        {
            values.AddRange(list.Select(Render));
        }

        return values;
    });

    private static IEnumerable<string> LeafFields(FilterNode? node) => Leaves(node).Select(leaf => leaf.Field);

    private static IEnumerable<ComparisonNode> Leaves(FilterNode? node) => node switch
    {
        null => [],
        ComparisonNode comparison => [comparison],
        NotNode not => Leaves(not.Node),
        CompositeFilterNode composite => composite.Nodes.SelectMany(Leaves),
        _ => throw new Xunit.Sdk.XunitException($"Unhandled filter node type {node.GetType().Name}.")
    };

    private static string Render(TypedValue value) => value switch
    {
        StringValue text => text.Value,
        DateValue date => date.Value,
        DatetimeValue datetime => datetime.Value,
        DecimalValue @decimal => @decimal.Value,
        Int64Value int64 => int64.Value.ToString(),
        _ => throw new Xunit.Sdk.XunitException($"Unhandled typed value {value.GetType().Name}.")
    };
}
