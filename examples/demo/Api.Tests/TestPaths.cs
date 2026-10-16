namespace Demo.Api.Tests;

internal static class TestPaths
{
    /// <summary>Finds examples/demo/orders.schema.json by walking up from the test binary.</summary>
    public static string SchemaFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "orders.schema.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("orders.schema.json not found above " + AppContext.BaseDirectory);
    }
}
