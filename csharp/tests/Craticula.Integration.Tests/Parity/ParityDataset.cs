using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Craticula.Integration.Tests.Parity;

/// <summary>
/// A seeded dataset for the parity schema (T-3, PAR-1): a pure function of <c>(seed, rows)</c>.
///
/// <para>Cells use XEQ-2's CLR forms: <c>long</c>, <c>double</c>, <c>string</c> (a decimal's exact digits, a
/// <c>label</c>, a <c>yyyy-MM-dd</c> date, a <c>yyyy-MM-ddTHH:mm:ss.fffZ</c> instant with all three fractional digits,
/// which is also SEM-4's SQLite host obligation), <c>bool</c> and <c>null</c>. The stableKey <c>id</c> is unique and
/// never null (XEQ-2, SEM-6), drawn in no particular order so insertion order cannot stand in for the ORDER BY. Every
/// other field is NULL in about one row in eight and otherwise draws from <see cref="ParityPools"/>, which repeat
/// values on purpose so sorts have ties for the tiebreaker to break.</para>
/// </summary>
internal sealed class ParityDataset
{
    private const int NullOneIn = 8;

    private ParityDataset(long seed, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows)
    {
        Seed = seed;
        Rows = rows;
    }

    public long Seed { get; }

    /// <summary>One dictionary per row, keyed by logical colId, every schema field present.</summary>
    public IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows { get; }

    public static ParityDataset Generate(long seed, int rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);

        var rng = new Random(ParityRandom.Mix(seed, -1));
        var ids = new HashSet<long>();
        var result = new List<IReadOnlyDictionary<string, object?>>(rows);

        while (result.Count < rows)
        {
            var id = rng.Next(1, rows * 20);
            if (!ids.Add(id))
            {
                continue;
            }

            result.Add(new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["id"] = (long)id,
                ["label"] = Maybe(rng, () => ParityPools.Labels[rng.Next(ParityPools.Labels.Length)]),
                ["qty"] = Maybe(rng, () => ParityPools.Quantities[rng.Next(ParityPools.Quantities.Length)]),
                ["score"] = Maybe(rng, () => ParityPools.Scores[rng.Next(ParityPools.Scores.Length)]),
                ["price"] = Maybe(rng, () => ParityPools.Prices[rng.Next(ParityPools.Prices.Length)]
                    .ToString(CultureInfo.InvariantCulture)),
                ["active"] = Maybe(rng, () => rng.Next(2) == 0),
                ["born"] = Maybe(rng, () => ParityPools.Dates[rng.Next(ParityPools.Dates.Length)]
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
                ["seen"] = Maybe(rng, () => ParityValues.FormatInstant(ParityPools.Instants[rng.Next(ParityPools.Instants.Length)]))
            });
        }

        return new ParityDataset(seed, result);
    }

    /// <summary>The seed as a <c>dataset.json</c> document (the shape of <c>fixtures/schemas/dataset.schema.json</c>).</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("rows");
            foreach (var row in Rows)
            {
                writer.WriteStartObject();
                writer.WriteStartObject("row");
                foreach (var field in ParitySchema.FieldNames)
                {
                    writer.WritePropertyName(field);
                    switch (row[field])
                    {
                        case null: writer.WriteNullValue(); break;
                        case long l: writer.WriteNumberValue(l); break;
                        case double d: writer.WriteNumberValue(d); break;
                        case bool b: writer.WriteBooleanValue(b); break;
                        case string s: writer.WriteStringValue(s); break;
                        default: throw new InvalidOperationException($"Unexpected cell type {row[field]!.GetType()}.");
                    }
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static object? Maybe(Random rng, Func<object> draw) => rng.Next(NullOneIn) == 0 ? null : draw();
}

/// <summary>Seed mixing and value spellings shared by the dataset and the request generator.</summary>
internal static class ParityRandom
{
    /// <summary>
    /// A SplitMix64 step over <c>(seed, index)</c> to a non-negative <see cref="int"/>, so neighbouring seeds and indexes
    /// give unrelated <see cref="Random"/> streams. A case is a pure function of these two numbers (PAR-1).
    /// </summary>
    public static int Mix(long seed, int index)
    {
        unchecked
        {
            var z = (ulong)seed + (0x9E3779B97F4A7C15UL * (ulong)(index + 1L));
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (int)(z & 0x7FFFFFFF);
        }
    }
}

internal static class ParityValues
{
    /// <summary>Always three fractional digits, UTC: XEQ-2's datetime form and SEM-4's SQLite host obligation.</summary>
    public static string FormatInstant(DateTime utc) =>
        utc.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
}
