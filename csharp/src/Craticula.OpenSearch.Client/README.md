# Craticula.OpenSearch.Client

The OpenSearch execution adapter of Craticula. It compiles the canonical IR with `Craticula.Search` and runs the
search body through `OpenSearch.Client`'s low-level client, with deep paging (point in time and a cursor cache)
and streamed export.

## Install

```
dotnet add package Craticula.OpenSearch.Client
```

Depends on `OpenSearch.Client` `[2.1.0, 3.0)` and `Craticula.Search`.

## Example

```csharp
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.OpenSearch.Client;
using Craticula.Search;
using OpenSearch.Net;

public static class Example
{
    // One cache per host process, shared by every request of a grid session.
    private static readonly InMemorySearchCursorCache Cache = new();

    public static Task<OpenSearchGridPage> Fetch(
        IOpenSearchLowLevelClient client, string gridSessionId, GridSchema schema,
        FilterNode? filter, IReadOnlyList<SortItem>? sort, PageSpec page) =>
        OpenSearchGridQuery.FetchPageAsync(
            client,
            new SearchQueryRequest { Schema = schema, Filter = filter, Sort = sort, Page = page },
            gridSessionId,
            Cache);
}
```

## Compatibility

Implements Craticula spec version 0.1.0 (the language-neutral semantics and conformance fixtures in the
[repository](https://github.com/pluries/craticula), `spec/`). Targets .NET 10.

## License

Apache-2.0. Source and issues: <https://github.com/pluries/craticula>.
