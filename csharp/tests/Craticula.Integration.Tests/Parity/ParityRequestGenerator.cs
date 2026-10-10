using System.Globalization;
using System.Text.Json.Nodes;
using Craticula.Core.Schema;

namespace Craticula.Integration.Tests.Parity;

/// <summary>
/// Deterministic AG Grid wire requests for the parity property (T-3, <c>spec/semantics.md</c> PAR-1): a request is a pure
/// function of <c>(seed, index)</c>, so a failing case is replayed by those two numbers. Every request is an
/// <b>ordinary row request</b> (PAR-1, PAR-2): empty <c>rowGroupCols</c>, <c>valueCols</c> and <c>groupKeys</c>, no
/// <c>setValuesRequest</c>, no <c>exportFormat</c>. The wire shapes are the ones the operator and filter-tree fixtures use
/// (<c>spec/fixtures/operators/*</c>, <c>filter-tree/*</c>), so the real <c>AgGridRequestParser</c> accepts them.
///
/// <para><b>How a case is built.</b> Every case that has a filter starts from one catalog entry chosen by
/// <c>index % catalog size</c> (a field and an IR operator), so a run of a few hundred cases visits every operator on
/// every field type it applies to; the rest of the case is random. The filter is one of: a single native column
/// condition, a combined native condition (<c>conditions</c> array or legacy <c>condition1/2</c>), several columns at once,
/// a <c>customFilters</c> and/or/not tree, or a native model merged with a tree. A case with no filter occurs one time in
/// 25. A sort of 0 to 3 keys and a paging window follow.</para>
///
/// <para><b>Constructs this generator excludes, by PAR-3 register row</b> (each is covered by the fixture the register
/// names, not by this generator):</para>
/// <list type="bullet">
/// <item>string-keyed sorts: only <c>id</c>, <c>qty</c>, <c>score</c>, <c>price</c>, <c>active</c>, <c>born</c> and
/// <c>seen</c> are sort keys (SEM-1; <c>sort-paging/001</c>);</item>
/// <item>case-sensitive strings and mixed sensitivities: the schema declares none (SEM-1, divergence #5;
/// <c>dialect-parity/023</c>);</item>
/// <item>the empty string: no operand is empty (SQL-7, XEQ-2; <c>dialect-parity/019</c>);</item>
/// <item><c>searchMode: analyzed</c>: not in the schema (SEM-7; <c>dialect-parity/013</c>);</item>
/// <item>pattern metacharacters: <c>contains</c>-family operands are letters and digits cut from the pools
/// (SEM-7, MDB-11; <c>dialect-parity/020</c>);</item>
/// <item>datetimes finer than a millisecond: every bound is millisecond-aligned and the native wire form is a whole
/// second (SEM-4's SQLite rule; <c>dialect-parity/025</c>);</item>
/// <item>groupable fields: none (GRP-2, divergences #3 and #4; <c>group-aggregation/017</c>).</item>
/// </list>
/// Outside the register but also kept in range: integers stay within +/-2^53 (XEQ-2), a window is at most 100 rows and
/// starts below 10000 (SEC-3), and a tree stays under SEC-3's depth and leaf limits.
/// </summary>
internal static class ParityRequestGenerator
{
    /// <summary>Largest page the generator asks for; SEC-3's default ceiling is 1000.</summary>
    public const int MaxWindow = 100;

    private const int NoFilterEvery = 25;

    private static readonly string[] NumberLikeOps = ["eq", "neq", "gt", "gte", "lt", "lte"];

    // The operators per field, as IR operator names (spec/ir.schema.json binary/unary/set/range operators).
    private static readonly (string Field, FieldType Type, string[] Ops)[] Kinds =
    [
        ("label", FieldType.String, ["eq", "neq", "contains", "notContains", "startsWith", "endsWith", "in", "notIn", "blank", "notBlank"]),
        ("qty", FieldType.Int64, [.. NumberLikeOps, "in", "notIn", "inRange", "blank", "notBlank"]),
        ("score", FieldType.Double, [.. NumberLikeOps, "inRange", "blank", "notBlank"]),
        ("price", FieldType.Decimal, [.. NumberLikeOps, "inRange", "blank", "notBlank"]),
        ("active", FieldType.Boolean, ["true", "false", "eq", "neq", "blank", "notBlank"]),
        ("born", FieldType.Date, [.. NumberLikeOps, "inRange", "blank", "notBlank"]),
        ("seen", FieldType.Datetime, [.. NumberLikeOps, "inRange", "blank", "notBlank"])
    ];

    private static readonly IReadOnlyList<Leaf> Catalog =
        [.. Kinds.SelectMany(kind => kind.Ops.Select(op => new Leaf(kind.Field, kind.Type, op)))];

    // Sort keys: every field except the string one (register row 1). id is added by hand as a final tiebreaker key.
    private static readonly string[] SortFields = ["qty", "score", "price", "active", "born", "seen"];

    // Mostly small starts: the datasets are a few hundred rows and a filter keeps a fraction of them, so a start of 60
    // or more lands past the end of most results and compares two empty windows. One window in FarWindowEvery starts
    // far past any result (SEC-3's offsets), which keeps the empty-window path, and only that, deliberately.
    private static readonly int[] WindowStarts = [0, 0, 0, 0, 0, 0, 1, 2, 3, 5, 8, 12];

    private const int FarWindowEvery = 50;
    private const int FarWindowStart = 200;

    private static readonly int[] WindowSizes = [1, 3, 5, 10, 10, 25, 25, 50, 100];

    /// <summary>One filter leaf: a field of a type with an IR operator.</summary>
    private readonly record struct Leaf(string Field, FieldType Type, string Op);

    /// <summary>A drawn operand. <c>Value</c> is a <c>string</c>, <c>long</c>, <c>double</c>, <c>decimal</c>, <c>bool</c>,
    /// <c>DateOnly</c> or UTC <c>DateTime</c> matching <c>Type</c>.</summary>
    private readonly record struct Scalar(FieldType Type, object Value);

    /// <summary>The number of distinct (field, operator) catalog entries; a run of this many consecutive indexes visits all.</summary>
    public static int CatalogSize => Catalog.Count;

    /// <summary>The wire request JSON for case <paramref name="index"/> of <paramref name="seed"/>.</summary>
    public static string Generate(long seed, int index)
    {
        var rng = new Random(ParityRandom.Mix(seed, index));
        var first = Catalog[index % Catalog.Count];

        JsonNode? filterModel = null;
        JsonNode? customFilters = null;

        if (index % NoFilterEvery != NoFilterEvery - 1)
        {
            (filterModel, customFilters) = BuildFilter(rng, first);
        }

        var window = Window(rng);
        var request = new JsonObject
        {
            ["startRow"] = window.Start,
            ["endRow"] = window.Start + window.Size,
            ["rowGroupCols"] = new JsonArray(),
            ["valueCols"] = new JsonArray(),
            ["pivotCols"] = new JsonArray(),
            ["pivotMode"] = false,
            ["groupKeys"] = new JsonArray(),
            ["filterModel"] = filterModel,
            ["sortModel"] = SortModel(rng)
        };

        if (customFilters is not null)
        {
            request["customFilters"] = customFilters;
        }

        return request.ToJsonString();
    }

    // ---- filter shapes ---------------------------------------------------------------------------------------------

    private static (JsonNode? FilterModel, JsonNode? CustomFilters) BuildFilter(Random rng, Leaf first)
    {
        // 0 native single, 1 native combined (one column), 2 native multi-column, 3 custom tree,
        // 4 native multi-column merged with a custom tree, 5 custom single leaf.
        var shape = rng.Next(6);
        if (!IsNative(first) && shape is 0 or 1 or 2)
        {
            shape = 3; // the catalog entry has no native wire form (inRange, notIn, boolean eq/neq): carry it in a tree
        }

        switch (shape)
        {
            case 0:
                return (new JsonObject { [first.Field] = NativeCondition(rng, first) }, null);

            case 1:
                var column = IsCombinable(first) ? first : RandomCombinable(rng);
                return (new JsonObject { [column.Field] = CombinedNative(rng, column) }, null);

            case 2:
                return (MultiColumn(rng, first), null);

            case 4:
            {
                var model = IsNative(first) ? MultiColumn(rng, first) : MultiColumn(rng, RandomNative(rng));
                var tree = IsNative(first) ? Tree(rng, RandomLeaves(rng, 1, 2)) : Tree(rng, [first, .. RandomLeaves(rng, 0, 1)]);
                return (model, tree);
            }

            case 5:
                return (null, CustomLeaf(rng, first));

            default:
                return (null, Tree(rng, [first, .. RandomLeaves(rng, 1, rng.Next(4) == 0 ? 5 : 2)]));
        }
    }

    /// <summary>Distinct columns, each with its own native condition; AG Grid ANDs the columns.</summary>
    private static JsonObject MultiColumn(Random rng, Leaf first)
    {
        var model = new JsonObject { [first.Field] = NativeCondition(rng, first) };
        for (var extra = rng.Next(6) == 0 ? 2 : 1; extra > 0; extra--)
        {
            var leaf = RandomNative(rng);
            if (!model.ContainsKey(leaf.Field))
            {
                model[leaf.Field] = rng.Next(3) == 0 && IsCombinable(leaf) ? CombinedNative(rng, leaf) : NativeCondition(rng, leaf);
            }
        }

        return model;
    }

    /// <summary>Two or three conditions on one column joined by AND or OR, in either of AG Grid's two shapes.</summary>
    private static JsonObject CombinedNative(Random rng, Leaf column)
    {
        // A conjunction of conditions on one column is the usual way to ask for nothing (blank AND x, equals 3 AND equals 5),
        // so most combinations are ORs, an AND has two conditions, and no AND carries a blank test.
        var and = column.Op is not ("blank" or "notBlank") && rng.Next(5) < 2;
        var ops = NativeOps(column.Type).Where(op => op is not ("in" or "true" or "false") && !(and && op is "blank" or "notBlank")).ToArray();
        var count = and ? 2 : rng.Next(2, 4);
        var conditions = Enumerable.Range(0, count)
            .Select(i => NativeCondition(rng, column with { Op = i == 0 && IsCombinable(column) ? column.Op : ops[rng.Next(ops.Length)] }))
            .ToList();

        var combined = new JsonObject
        {
            ["filterType"] = conditions[0]!["filterType"]!.GetValue<string>(),
            ["operator"] = and ? "AND" : "OR"
        };

        if (count == 2 && rng.Next(2) == 0)
        {
            combined["condition1"] = conditions[0];
            combined["condition2"] = conditions[1];
        }
        else
        {
            combined["conditions"] = new JsonArray(conditions.ToArray());
        }

        return combined;
    }

    /// <summary>One AG Grid column condition (text, number, date or set filter) for a natively expressible leaf.</summary>
    private static JsonNode NativeCondition(Random rng, Leaf leaf)
    {
        if (leaf.Op == "in")
        {
            var values = Enumerable.Range(0, rng.Next(1, 5))
                .Select(_ => Draw(rng, leaf.Type, "eq", wholeSecond: true))
                .Select(s => (JsonNode?)JsonValue.Create(Invariant(s.Value)))
                .ToArray();
            return new JsonObject { ["filterType"] = "set", ["values"] = new JsonArray(values) };
        }

        if (leaf.Op is "true" or "false")
        {
            // A boolean column is filtered as a set selection (spec/fixtures/operators/044, 045).
            return new JsonObject { ["filterType"] = "set", ["values"] = new JsonArray(JsonValue.Create(leaf.Op)) };
        }

        var type = NativeType(leaf.Op);
        switch (leaf.Type)
        {
            case FieldType.String:
                return leaf.Op is "blank" or "notBlank"
                    ? new JsonObject { ["filterType"] = "text", ["type"] = type }
                    : new JsonObject { ["filterType"] = "text", ["type"] = type, ["filter"] = Draw(rng, leaf.Type, leaf.Op, true).Value as string };

            case FieldType.Date or FieldType.Datetime:
            {
                var node = new JsonObject { ["filterType"] = "date", ["type"] = type, ["dateFrom"] = null, ["dateTo"] = null };
                if (leaf.Op is not ("blank" or "notBlank"))
                {
                    // AG Grid's date wire form is whole-second ('yyyy-MM-dd HH:mm:ss'); a finer bound needs customFilters.
                    var value = Draw(rng, leaf.Type, leaf.Op, wholeSecond: true).Value;
                    node["dateFrom"] = value is DateTime instant
                        ? instant.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)
                        : ((DateOnly)value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                }

                return node;
            }

            default: // int64, double, decimal
            {
                var node = new JsonObject { ["filterType"] = "number", ["type"] = type };
                if (leaf.Op is not ("blank" or "notBlank"))
                {
                    node["filter"] = Draw(rng, leaf.Type, leaf.Op, true).Value switch
                    {
                        long l => JsonValue.Create(l),
                        double d => JsonValue.Create(d),
                        decimal m => JsonValue.Create(m),
                        _ => throw new InvalidOperationException("Not a number.")
                    };
                }

                return node;
            }
        }
    }

    // ---- customFilters trees (IR filter nodes, spec/ir.schema.json) -------------------------------------------------

    /// <summary>
    /// An and/or/not tree over <paramref name="leaves"/>. At most six leaves, a NOT only above depth 4 and never directly
    /// over another NOT, so the tree stays inside SEC-3's default depth (8) and leaf (64) limits.
    /// </summary>
    private static JsonNode Tree(Random rng, IReadOnlyList<Leaf> leaves) => Build(rng, leaves, depth: 1, allowNot: true);

    private static JsonNode Build(Random rng, IReadOnlyList<Leaf> leaves, int depth, bool allowNot)
    {
        JsonNode node;
        if (leaves.Count == 1)
        {
            node = CustomLeaf(rng, leaves[0]);
        }
        else
        {
            // Split into 2 groups (3 when there are enough leaves).
            var groups = leaves.Count >= 3 && rng.Next(3) == 0 ? 3 : 2;
            var children = new List<JsonNode?>();
            var taken = 0;
            for (var g = 0; g < groups; g++)
            {
                var remaining = leaves.Count - taken;
                var size = g == groups - 1 ? remaining : rng.Next(1, remaining - (groups - g - 1) + 1);
                children.Add(Build(rng, [.. leaves.Skip(taken).Take(size)], depth + 1, allowNot: true));
                taken += size;
            }

            node = new JsonObject { ["type"] = rng.Next(3) == 0 ? "and" : "or", ["nodes"] = new JsonArray(children.ToArray()) };
        }

        return allowNot && depth < 4 && rng.Next(4) == 0
            ? new JsonObject { ["type"] = "not", ["node"] = node }
            : node;
    }

    private static JsonNode CustomLeaf(Random rng, Leaf leaf)
    {
        var node = new JsonObject { ["type"] = "comparison", ["field"] = leaf.Field, ["op"] = leaf.Op };
        switch (leaf.Op)
        {
            case "blank" or "notBlank" or "true" or "false":
                break;

            case "in" or "notIn":
                node["values"] = new JsonArray(Enumerable.Range(0, rng.Next(1, 5))
                    .Select(_ => (JsonNode?)Typed(Draw(rng, leaf.Type, "eq", wholeSecond: false))).ToArray());
                break;

            case "inRange":
            {
                var a = Draw(rng, leaf.Type, "gte", wholeSecond: false);
                var b = Draw(rng, leaf.Type, "lte", wholeSecond: false);
                var (from, to) = Comparer<object>.Create(CompareScalars).Compare(a.Value, b.Value) <= 0 ? (a, b) : (b, a);
                node["from"] = Typed(from);
                node["to"] = Typed(to);
                // A range whose bounds coincide is empty unless both ends are inclusive.
                var coincide = Comparer<object>.Create(CompareScalars).Compare(from.Value, to.Value) == 0;
                node["fromInclusive"] = coincide || rng.Next(4) != 0;
                node["toInclusive"] = coincide || rng.Next(4) != 0;
                break;
            }

            default:
                node["value"] = leaf.Type == FieldType.Boolean
                    ? Typed(new Scalar(FieldType.Boolean, rng.Next(2) == 0))
                    : Typed(Draw(rng, leaf.Type, leaf.Op, wholeSecond: false));
                break;
        }

        return node;
    }

    private static JsonObject Typed(Scalar scalar) => scalar.Type switch
    {
        FieldType.String => new JsonObject { ["type"] = "string", ["value"] = (string)scalar.Value },
        FieldType.Int64 => new JsonObject { ["type"] = "int64", ["value"] = (long)scalar.Value },
        FieldType.Double => new JsonObject { ["type"] = "double", ["value"] = (double)scalar.Value },
        FieldType.Decimal => new JsonObject { ["type"] = "decimal", ["value"] = ((decimal)scalar.Value).ToString(CultureInfo.InvariantCulture) },
        FieldType.Boolean => new JsonObject { ["type"] = "boolean", ["value"] = (bool)scalar.Value },
        FieldType.Date => new JsonObject { ["type"] = "date", ["value"] = ((DateOnly)scalar.Value).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) },
        FieldType.Datetime => new JsonObject { ["type"] = "datetime", ["value"] = ParityValues.FormatInstant((DateTime)scalar.Value), ["zone"] = "UTC" },
        _ => throw new InvalidOperationException($"Unhandled type {scalar.Type}.")
    };

    // ---- operands --------------------------------------------------------------------------------------------------

    /// <summary>
    /// An operand for <paramref name="op"/> on a field of <paramref name="type"/>: usually a pooled value (so it can match),
    /// sometimes a neighbour of one (so a boundary is hit from both sides). <paramref name="wholeSecond"/> floors a datetime
    /// to the second, the native wire form.
    /// </summary>
    private static Scalar Draw(Random rng, FieldType type, string op, bool wholeSecond)
    {
        // A neighbour of a pooled value is stored by nobody: it is a boundary probe for the ordering operators, but an
        // empty result for an equality, so equality takes one only now and then.
        var near = rng.Next(op is "eq" or "neq" ? 12 : 4) == 0;
        switch (type)
        {
            case FieldType.String:
                return new Scalar(type, StringOperand(rng, op));

            case FieldType.Int64:
                return new Scalar(type, ParityPools.Quantities[rng.Next(ParityPools.Quantities.Length)] + (near ? rng.Next(-1, 2) : 0));

            case FieldType.Double:
                return new Scalar(type, ParityPools.Scores[rng.Next(ParityPools.Scores.Length)] + (near ? 0.25 * rng.Next(-1, 2) : 0.0));

            case FieldType.Decimal:
                return new Scalar(type, ParityPools.Prices[rng.Next(ParityPools.Prices.Length)] + (near ? 0.01m * rng.Next(-1, 2) : 0m));

            case FieldType.Boolean:
                return new Scalar(type, rng.Next(2) == 0);

            case FieldType.Date:
                return new Scalar(type, ParityPools.Dates[rng.Next(ParityPools.Dates.Length)].AddDays(near ? rng.Next(-3, 4) : 0));

            default:
            {
                var instant = ParityPools.Instants[rng.Next(ParityPools.Instants.Length)];
                if (wholeSecond)
                {
                    instant = instant.AddMilliseconds(-instant.Millisecond);
                }

                if (near)
                {
                    // One millisecond either side of a boundary (the case SEM-4's SQLite rule exists for), or a second or an hour.
                    instant = rng.Next(3) switch
                    {
                        0 when !wholeSecond => instant.AddMilliseconds(rng.Next(2) == 0 ? -1 : 1),
                        1 => instant.AddSeconds(rng.Next(2) == 0 ? -1 : 1),
                        _ => instant.AddHours(rng.Next(2) == 0 ? -1 : 1)
                    };
                }

                return new Scalar(type, instant);
            }
        }
    }

    /// <summary>
    /// A non-empty string of letters and digits cut from the pooled labels (register rows: no <c>''</c>, no pattern
    /// metacharacters), in a random case: the field is case-insensitive (SEM-1), so every engine must agree regardless.
    /// </summary>
    private static string StringOperand(Random rng, string op)
    {
        var label = rng.Next(30) == 0 ? "zulu" : ParityPools.Labels[rng.Next(ParityPools.Labels.Length)];
        var length = rng.Next(1, Math.Min(4, label.Length) + 1);
        var text = op switch
        {
            "contains" or "notContains" => label.Substring(rng.Next(0, label.Length - length + 1), length),
            "startsWith" => label[..length],
            "endsWith" => label[^length..],
            _ => label
        };

        return rng.Next(3) switch
        {
            0 => text.ToLowerInvariant(),
            1 => text.ToUpperInvariant(),
            _ => text
        };
    }

    private static int CompareScalars(object? a, object? b) => ((IComparable)a!).CompareTo(b);

    private static string Invariant(object value) => value switch
    {
        string s => s,
        long l => l.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException("Only string and int64 set filters are generated.")
    };

    // ---- leaf selection --------------------------------------------------------------------------------------------

    private static IEnumerable<string> NativeOps(FieldType type) => type switch
    {
        FieldType.String => ["eq", "neq", "contains", "notContains", "startsWith", "endsWith", "blank", "notBlank", "in"],
        FieldType.Int64 => [.. NumberLikeOps, "blank", "notBlank", "in"],
        FieldType.Boolean => ["true", "false"],
        _ => [.. NumberLikeOps, "blank", "notBlank"]
    };

    /// <summary>True when AG Grid's own column filter can express the leaf (inRange, notIn and boolean eq/neq cannot).</summary>
    private static bool IsNative(Leaf leaf) => NativeOps(leaf.Type).Contains(leaf.Op);

    /// <summary>True when the leaf can be one of several conditions under a column's AND/OR (set selections cannot).</summary>
    private static bool IsCombinable(Leaf leaf) => IsNative(leaf) && leaf.Op is not ("in" or "true" or "false") && leaf.Type != FieldType.Boolean;

    private static Leaf RandomNative(Random rng)
    {
        while (true)
        {
            var leaf = Catalog[rng.Next(Catalog.Count)];
            if (IsNative(leaf))
            {
                return leaf;
            }
        }
    }

    private static Leaf RandomCombinable(Random rng)
    {
        while (true)
        {
            var leaf = Catalog[rng.Next(Catalog.Count)];
            if (IsCombinable(leaf))
            {
                return leaf;
            }
        }
    }

    private static IReadOnlyList<Leaf> RandomLeaves(Random rng, int min, int max) =>
        [.. Enumerable.Range(0, rng.Next(min, max + 1)).Select(_ => Catalog[rng.Next(Catalog.Count)])];

    private static string NativeType(string op) => op switch
    {
        "eq" => "equals",
        "neq" => "notEqual",
        "gt" => "greaterThan",
        "gte" => "greaterThanOrEqual",
        "lt" => "lessThan",
        "lte" => "lessThanOrEqual",
        _ => op // contains, notContains, startsWith, endsWith, blank, notBlank share AG Grid's spelling
    };

    // ---- sort and window -------------------------------------------------------------------------------------------

    private static JsonArray SortModel(Random rng)
    {
        var keys = rng.Next(20) switch { < 4 => 0, < 12 => 1, < 17 => 2, _ => 3 };
        var chosen = SortFields.OrderBy(_ => rng.Next()).Take(keys).ToList();
        var model = new JsonArray();
        foreach (var field in chosen)
        {
            model.Add(new JsonObject { ["colId"] = field, ["sort"] = rng.Next(2) == 0 ? "asc" : "desc" });
        }

        // The stableKey as an explicit last key now and then: SEM-6 then appends no second tiebreaker (sort-paging/010).
        if (rng.Next(7) == 0)
        {
            model.Add(new JsonObject { ["colId"] = "id", ["sort"] = rng.Next(2) == 0 ? "asc" : "desc" });
        }

        return model;
    }

    /// <summary>A window with <c>startRow &lt; endRow</c>, at most <see cref="MaxWindow"/> rows, starting far below SEC-3's offsets.</summary>
    private static (int Start, int Size) Window(Random rng) =>
        (rng.Next(FarWindowEvery) == 0 ? FarWindowStart : WindowStarts[rng.Next(WindowStarts.Length)], WindowSizes[rng.Next(WindowSizes.Length)]);
}
