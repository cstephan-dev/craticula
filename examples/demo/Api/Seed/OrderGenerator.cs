namespace Demo.Api.Seed;

/// <summary>
/// Deterministic synthetic orders: the same seed gives the same rows on every machine. Nothing here is real
/// data. The first rows carry tricky customer text on purpose (quotes, a comma, a leading '=' for the CSV
/// hardening demo).
/// </summary>
public static class OrderGenerator
{
    public const int Seed = 20261016;
    public const int DefaultCount = 20_000;

    private static readonly string[] Countries =
        ["Germany", "France", "Spain", "Italy", "Netherlands", "Poland", "Sweden", "Portugal", "Ireland", "Austria"];

    private static readonly string[] Categories =
        ["Hardware", "Software", "Services", "Accessories", "Training"];

    private static readonly string[] Statuses =
        ["pending", "processing", "shipped", "delivered", "cancelled"];

    private static readonly string[] CustomerStems =
        ["Nordwind", "Blue Harbor", "Casa Verde", "Alpine", "Quantum", "Lumen", "Redwood", "Atlas", "Vega", "Orion",
         "Kestrel", "Maple", "Granite", "Sable", "Tidewater", "Juniper"];

    private static readonly string[] CustomerSuffixes = ["GmbH", "Ltd", "SA", "BV", "AB", "Inc", "Group", "Partners"];

    private static readonly string[] TrickyCustomers =
    [
        "O'Brien \"Big\" Corp",
        "=SUM(A1)",
        "Müller, Söhne & Co",
        "+1+1|cmd",
        "Line\nBreak Ltd"
    ];

    private static readonly DateTime Start = new(2024, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private const long SpanMilliseconds = 2L * 365 * 24 * 3600 * 1000;

    public static IReadOnlyList<OrderRow> Generate(int count = DefaultCount)
    {
        var random = new Random(Seed);
        var rows = new List<OrderRow>(count);

        for (var i = 1; i <= count; i++)
        {
            var orderedAt = Start.AddMilliseconds(random.NextInt64(SpanMilliseconds));
            var status = Statuses[random.Next(Statuses.Length)];

            string? customer = i <= TrickyCustomers.Length
                ? TrickyCustomers[i - 1]
                : $"{CustomerStems[random.Next(CustomerStems.Length)]} {CustomerSuffixes[random.Next(CustomerSuffixes.Length)]}";
            string? country = Countries[random.Next(Countries.Length)];
            string? category = Categories[random.Next(Categories.Length)];
            decimal? amount = random.Next(500, 2_500_000) / 100m;
            long? quantity = random.Next(1, 51);

            // Always draw, so the sequence of random numbers does not depend on an earlier branch.
            var nulls = new bool[5];
            for (var k = 0; k < nulls.Length; k++)
            {
                nulls[k] = random.NextDouble() < 0.03;
            }

            var shipDays = random.Next(1, 15);
            var isPriority = random.NextDouble() < 0.15;

            // The first rows keep their tricky customer; nulls never hide them.
            if (i > TrickyCustomers.Length && nulls[0]) { customer = null; }
            if (nulls[1]) { country = null; }
            if (nulls[2]) { category = null; }
            if (nulls[3]) { amount = null; }
            if (nulls[4]) { quantity = null; }

            DateOnly? shippedOn = status is "shipped" or "delivered"
                ? DateOnly.FromDateTime(orderedAt.AddDays(shipDays))
                : null;

            rows.Add(new OrderRow(
                i, $"ORD-{i:D6}", customer, country, category, status, amount, quantity, orderedAt, shippedOn, isPriority));
        }

        return rows;
    }
}
