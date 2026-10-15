# Craticula.AgGrid

The AG Grid frontend adapter of Craticula. It parses a Server-Side Row Model request (the `getRows` JSON, AG Grid
v31 to v36) into the canonical IR that the Craticula compilers consume, and maps a page of results back to the
shape the grid expects. It does not talk to any database.

## Install

```
dotnet add package Craticula.AgGrid
```

## Example

```csharp
using Craticula.AgGrid;
using Craticula.Core.Ir;
using Craticula.Core.Schema;

public static class Example
{
    public static (AgGridParsedRequest Request, FilterNode? Filter) Parse(string requestJson, GridSchema schema)
    {
        AgGridParsedRequest parsed =
            AgGridRequestParser.Parse(requestJson, new AgGridParseOptions { Schema = schema });

        FilterNode? filter = GridFilterPipeline.Process(
            parsed.NativeFilter, parsed.CustomFilters, parsed.Page, schema, new ResourceLimits(),
            parsed.Group, parsed.Aggregations);

        return (parsed, filter);
    }
}
```

Pass the parsed request and the filter to a compiler package (`Craticula.Sql`, `Craticula.Mongo` or
`Craticula.Search`) or to an execution adapter.

## Compatibility

Implements Craticula spec version 0.1.0 (the language-neutral semantics and conformance fixtures in the
[repository](https://github.com/pluries/craticula), `spec/`). Targets .NET 10.

## License

Apache-2.0. Source and issues: <https://github.com/pluries/craticula>.
