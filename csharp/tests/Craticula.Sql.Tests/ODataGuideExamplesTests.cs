using Craticula.AgGrid;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

namespace Craticula.Sql.Tests;

/// <summary>
/// #2835: the docs site's OData migration guide (<c>site/pages/odata-migration.md</c>) shows, for each OData construct, an AG Grid request
/// (<c>site/examples/odata/NN-name.json</c>) and the PostgreSQL statement it compiles to (<c>NN-name.sql.txt</c>), both included in the
/// page as they are. This compiles every request against <c>schema.json</c> and compares the statement and its parameters with the
/// recorded file, so an example that stops compiling, or whose SQL changes, fails here instead of leaving the guide stale.
/// Set <c>CRATICULA_BLESS_EXAMPLES=1</c> to rewrite the recorded files after a deliberate change, then read the diff.
/// </summary>
public class ODataGuideExamplesTests
{
    private static string Folder => Path.Combine(RepoPaths.Root, "site", "examples", "odata");

    public static TheoryData<string> Requests()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.GetFiles(Folder, "*.json").Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(file) != "schema.json")
            {
                data.Add(Path.GetFileNameWithoutExtension(file));
            }
        }

        return data;
    }

    [Fact]
    public void The_Guide_Has_Examples()
    {
        Assert.True(Requests().Count >= 8, "the OData guide's example requests are missing");
    }

    [Theory]
    [MemberData(nameof(Requests))]
    public void Each_Example_Compiles_To_The_Recorded_Statement(string name)
    {
        var schema = GridSchema.Parse(File.ReadAllText(Path.Combine(Folder, "schema.json")));
        var requestJson = File.ReadAllText(Path.Combine(Folder, name + ".json"));

        var parsed = AgGridRequestParser.Parse(requestJson, new AgGridParseOptions { Schema = schema });
        var filter = GridFilterPipeline.Process(
            parsed.NativeFilter, parsed.CustomFilters, parsed.Page, schema, new ResourceLimits(), parsed.Group, parsed.Aggregations);
        var query = SqlQueryBuilder.Build(
            new SqlQueryRequest
            {
                Schema = schema, Filter = filter, Sort = parsed.Sort, Page = parsed.Page, Group = parsed.Group, Aggregations = parsed.Aggregations
            },
            PostgresDialect.Instance);

        var actual = query.Sql + Environment.NewLine + "-- parameters: " + string.Join(", ", query.Parameters.Select(Show));

        var recorded = Path.Combine(Folder, name + ".sql.txt");
        if (Environment.GetEnvironmentVariable("CRATICULA_BLESS_EXAMPLES") == "1")
        {
            File.WriteAllText(recorded, actual + "\n");
        }

        Assert.Equal(Normalize(File.ReadAllText(recorded)), Normalize(actual));
    }

    /// <summary>A parameter as the guide shows it: text in quotes, a list of values in brackets.</summary>
    private static string Show(object? value) => value switch
    {
        null => "NULL",
        string text => $"'{text}'",
        bool flag => flag ? "true" : "false",
        System.Collections.IEnumerable list => "[" + string.Join(", ", list.Cast<object?>().Select(Show)) + "]",
        _ => value.ToString() ?? "NULL"
    };

    private static string Normalize(string text) => text.Replace("\r\n", "\n").TrimEnd('\n');
}
