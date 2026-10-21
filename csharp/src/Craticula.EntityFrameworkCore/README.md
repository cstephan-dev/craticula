# Craticula.EntityFrameworkCore

The Entity Framework Core execution adapter of Craticula. It compiles the canonical IR straight to `IQueryable<T>`
expression trees and runs them, so no SQL text is produced here. It works with any relational provider (you choose
the provider package) and supports paging, row counts and streamed export.

## Install

```
dotnet add package Craticula.EntityFrameworkCore
```

Depends on `Microsoft.EntityFrameworkCore.Relational` `[10.0.11, 11.0)`.

## Example

```csharp
using Craticula.Core.Ir;
using Craticula.Core.Schema;
using Craticula.EntityFrameworkCore;

public static class Example
{
    public static async Task<EfCoreGridPage> Fetch<TRow>(
        IQueryable<TRow> source, GridSchema schema, FilterNode? filter, IReadOnlyList<SortItem>? sort, PageSpec page)
        where TRow : class
    {
        var request = new EfCoreQueryRequest { Schema = schema, Filter = filter, Sort = sort, Page = page };

        // EfCoreProviderSemantics.Sqlite is the one measured preset; declare your own for other engines.
        return await EfCoreGridQuery.FetchPageAsync(
            source, request, EfCoreProviderSemantics.Sqlite, includeRowCount: true);
    }
}
```

## Two host obligations that fail silently

Both run without error and return wrong rows, so the adapter documents them; it cannot see the host's model or collations.

- **SQLite instants.** Store a `datetime` as `yyyy-MM-ddTHH:mm:ss.fffZ` text (SEM-4) and map every `DateTime` to and from that text in your
  `DbContext` (`configurationBuilder.Properties<DateTime>().HaveConversion<YourStoredInstantConverter>()`). EF's own mapping binds a
  bound as `yyyy-MM-dd HH:mm:ss`; on the bound's own date `'T'` sorts after a space, so a non-midnight range bound counts the whole
  date as later and a datetime-sorted export repeats or drops rows at chunk edges.
- **`PatternMatchFoldsCase = false`** unless every searchable column's collation folds case. `true` sends a bare `LIKE` that follows the
  column's collation, so a binary-collated column (SQL Server `..._BIN2`) matches exact case only: `contains "germ"` finds no `Germany`.
  `false` lowers both sides and is right for every column.

`EfCoreHostObligationTests` shows each: wrong rows without it, right rows with it.

## Datetime cells

A `datetime` cell leaves a page or an export row as a UTC instant, whatever kind the provider materialised (SEM-4, result side): a
UTC value is kept, a local one is converted, a zone-less one (SQL Server `datetime2`, MySQL `DATETIME`, SQLite text) is read as UTC.
A host needs no UTC value converter for the way out. The values it stores must still be UTC readings.

## Compatibility

Implements Craticula spec version 0.1.0 (the language-neutral semantics and conformance fixtures in the
[repository](https://github.com/pluries/craticula), `spec/`). Targets .NET 10.

## License

Apache-2.0. Source and issues: <https://github.com/pluries/craticula>.
