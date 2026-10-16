using System.Security.Cryptography;
using System.Text;
using Craticula.Core.Schema;
using Demo.Api.Seed;

namespace Demo.Api.Tests;

public class OrderGeneratorTests
{
    [Fact]
    public void Schema_parses_with_both_mappings_and_the_grouping_capabilities()
    {
        var schema = GridSchema.Parse(File.ReadAllText(TestPaths.SchemaFile()));

        Assert.Equal("orders", schema.Source);
        Assert.Equal(11, schema.Fields.Count);
        Assert.All(schema.Fields.Values, f =>
        {
            Assert.NotNull(f.Physical?.Sql?.Column);
            Assert.NotNull(f.Physical?.Mongo?.Path);
        });
        Assert.True(schema.Fields["country"].Groupable);
        Assert.True(schema.Fields["category"].Groupable);
        Assert.True(schema.Fields["status"].Groupable);
        Assert.Equal(5, schema.Fields["amount"].Aggregatable.Count);
        Assert.Equal(5, schema.Fields["quantity"].Aggregatable.Count);
    }

    [Fact]
    public void Generator_is_deterministic_and_has_20000_rows()
    {
        var first = OrderGenerator.Generate();
        var second = OrderGenerator.Generate();

        Assert.Equal(20_000, first.Count);
        Assert.Equal(first.Count, second.Count);
        Assert.Equal(Hash(first[0]), Hash(second[0]));
        Assert.Equal(Hash(first[^1]), Hash(second[^1]));
        Assert.Equal(first.Select(Hash), second.Select(Hash));
        Assert.Equal(20_000, first.Select(r => r.Id).Distinct().Count());
    }

    [Fact]
    public void Generator_has_nulls_decimals_with_two_places_and_utc_millisecond_instants()
    {
        var rows = OrderGenerator.Generate();

        Assert.Contains(rows, r => r.Customer is null);
        Assert.Contains(rows, r => r.Country is null);
        Assert.Contains(rows, r => r.Category is null);
        Assert.Contains(rows, r => r.Amount is null);
        Assert.Contains(rows, r => r.Quantity is null);
        Assert.Contains(rows, r => r.ShippedOn is null);
        Assert.Contains(rows, r => r.ShippedOn is not null);
        Assert.Contains(rows, r => r.IsPriority);
        Assert.Contains(rows, r => !r.IsPriority);
        Assert.All(rows.Where(r => r.Amount is not null), r => Assert.Equal(r.Amount, decimal.Round(r.Amount!.Value, 2)));
        Assert.All(rows, r => Assert.Equal(DateTimeKind.Utc, r.OrderedAt.Kind));
        Assert.All(rows, r => Assert.Equal(0, r.OrderedAt.Ticks % TimeSpan.TicksPerMillisecond));
        Assert.Equal(new[] { "cancelled", "delivered", "pending", "processing", "shipped" }, rows.Select(r => r.Status).Distinct().Order().ToArray());
    }

    [Fact]
    public void Generator_carries_the_tricky_text_values()
    {
        var customers = OrderGenerator.Generate().Select(r => r.Customer).ToHashSet();

        Assert.Contains("O'Brien \"Big\" Corp", customers);
        Assert.Contains("=SUM(A1)", customers);
    }

    private static string Hash(OrderRow row) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(row.ToString())));
}
