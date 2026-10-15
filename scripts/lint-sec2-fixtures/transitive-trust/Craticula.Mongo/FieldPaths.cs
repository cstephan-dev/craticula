namespace Fixture.Mongo;

public sealed record FieldConfig(string? Path);

public sealed record Schema(IReadOnlyDictionary<string, FieldConfig> Fields);

public sealed record Request(string ColId);

public static class FieldPaths
{
    private static FieldConfig ResolveField(string field, Schema schema) =>
        schema.Fields.TryGetValue(field, out var config) ? config : throw new ArgumentException("undeclared field");

    // The validating function: ResolveField throws for an undeclared field, so either side is declared.
    private static string ResolvePath(string field, Schema schema) => ResolveField(field, schema).Path ?? field;

    // Safe or not depending on its callers: its parameter is traced to every call site.
    private static string FieldReference(string path) => $"${path}";

    public static string Declared(Request request, Schema schema) => FieldReference(ResolvePath(request.ColId, schema));

    public static string Raw(Request request)
    {
        var path = request.ColId;
        return FieldReference(path);
    }
}
