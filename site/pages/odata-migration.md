# Migrating from OData

This page is for a team whose grid talks to an OData service today and wants to move to Craticula. It shows, construct by
construct, what you wrote in OData and the request Craticula takes instead. Every example here is a file that a test compiles,
so the request and the statement below it are real and cannot go stale.

Two things are different from the start:

- **Craticula takes the request AG Grid already sends.** There is no URL query string to build. The grid's server-side row model
  posts a JSON body (`filterModel`, `sortModel`, `startRow`, `endRow`, `rowGroupCols`, `valueCols`, `groupKeys`), and
  Craticula compiles it. The "OData" side below is what your service received; the "Craticula" side is what the grid sends.
- **Craticula compiles; it does not serve.** It returns a statement and its parameters. You run it with your own driver, and
  you write the endpoint (see the [C# quickstart](quickstart-csharp.html)).

All examples use one small synthetic schema, `orders` (id, customer, amount, status, region, country), and print the PostgreSQL
statement. The other databases get their own spelling of the same request.

{{include site/examples/odata/schema.json json}}

## `$filter`

### A comparison on one column

OData: `$filter=status eq 'active'`. The same holds for `ne`, `gt`, `ge`, `lt`, `le`: they become the filter types `equals`,
`notEqual`, `greaterThan`, `greaterThanOrEqual`, `lessThan`, `lessThanOrEqual`.

{{include site/examples/odata/01-filter-equals.json json}}

{{include site/examples/odata/01-filter-equals.sql.txt sql}}

The comparison is `COLLATE craticula_ci`: **text compares case-insensitively by default** ([SEM-1](semantics.html#sem-1)). In OData
the result usually depends on the service and its database. If your users relied on exact case, declare the field
`caseSensitivity: "sensitive"` in the schema.

### `contains`, `startswith`, `endswith`, and `and` across columns

OData: `$filter=contains(customer,'acme') and amount gt 100`. The text functions are the filter types `contains`, `startsWith`
and `endsWith` (and their negations `notContains`; `endswith` has no negation filter type, use the `customFilters` form below).
Filters on different columns are joined with AND, one `filterModel` entry per column.

{{include site/examples/odata/02-filter-two-columns.json json}}

{{include site/examples/odata/02-filter-two-columns.sql.txt sql}}

The `contains` match is escaped: the user's `%` and `_` are literal characters, never wildcards ([SEM-7](semantics.html#sem-7)).

### `or` on one column

OData: `$filter=amount gt 100 or amount lt 10`. Two conditions on the same column go in the column's `conditions` array with
`operator: "OR"` (or `"AND"`).

{{include site/examples/odata/03-filter-or-on-one-column.json json}}

{{include site/examples/odata/03-filter-or-on-one-column.sql.txt sql}}

### `in`, `not`, and any other nesting of `and`, `or`, `not`

OData: `$filter=status in ('active','pending')` or `$filter=not (status eq 'blocked')`. AG Grid's own filters cannot say this, so
Craticula accepts an extension next to `filterModel`: `customFilters`, a tree of `and`, `or`, `not` and comparison nodes
over the closed set of 17 operators: `eq`, `neq`, `gt`, `gte`, `lt`, `lte`, `contains`, `notContains`, `startsWith`, `endsWith`,
`in`, `notIn`, `inRange`, `blank`, `notBlank`, `true`, `false`. The tree is merged with `filterModel` by AND. Its depth and size are
limited (the resource limits of SEC-3), so a huge `$filter` is refused instead of compiled.

{{include site/examples/odata/04-filter-in-list.json json}}

{{include site/examples/odata/04-filter-in-list.sql.txt sql}}

{{include site/examples/odata/05-filter-not.json json}}

{{include site/examples/odata/05-filter-not.sql.txt sql}}

Read the second statement closely. `not (status eq 'blocked')` also returns the rows where `status` is `NULL`. Craticula's
negative operators include null rows on purpose ([SEM-2](semantics.html#sem-2)), so the grid behaves like AG Grid's own in-memory
filter. SQL's three-valued logic would drop those rows, and an OData service may or may not. Check the old behaviour with a test
before you migrate a filter that excludes values.

### `eq null` and `ne null`

OData: `$filter=region eq null`. This is the `blank` filter type (`notBlank` for `ne null`).

{{include site/examples/odata/06-filter-null.json json}}

{{include site/examples/odata/06-filter-null.sql.txt sql}}

For a text column `blank` means null **or** the empty string, which is what a grid user expects of "blank"; on a number or date
column it is only `NULL`. If you need exactly null on a text column, there is no filter type for it today.

### Ranges and booleans

`$filter=amount ge 10 and amount le 20` on one column is `inRange` in `customFilters`, with the inclusivity you mean written out (the
native in-range of AG Grid is refused because its bounds carry none).

{{include site/examples/odata/10-filter-range.json json}}

{{include site/examples/odata/10-filter-range.sql.txt sql}}

A boolean property, `$filter=active eq true`, is the grid's set filter on a boolean column, which compiles to the `true` operator.

{{include site/examples/odata/11-filter-boolean.json json}}

{{include site/examples/odata/11-filter-boolean.sql.txt sql}}

Date and date-time comparisons take ISO values (`createdAt gt 2026-01-01T00:00:00Z`); a date-time resolves in the zone the request
carries or the parser options name, UTC by default ([SEM-4](semantics.html#sem-4)).

## `$orderby`, `$top`, `$skip`, `$count`

OData: `$orderby=amount desc,id&$top=20&$skip=40`. `$orderby` is `sortModel`, first entry first. `$skip` is `startRow` and
`$skip + $top` is `endRow` (exclusive), so `$skip=40&$top=20` is `startRow: 40`, `endRow: 60`.

{{include site/examples/odata/07-orderby-and-paging.json json}}

{{include site/examples/odata/07-orderby-and-paging.sql.txt sql}}

Craticula always ends the sort with the schema's **stable key** ([SEM-6](semantics.html#sem-6)), so two rows never trade places
between pages. In OData a `$skip/$top` without a total order can skip or repeat rows. Null placement is also spelled out
([SEM-3](semantics.html#sem-3)): here `amount DESC NULLS FIRST`.

`$count=true` (the `@odata.count` of the answer) is the row count the execution adapters return next to the page (`RowCount`,
and AG Grid's `lastRow`). The count costs a second query, so ask for it when you need it.

## `$apply`: `groupby` and `aggregate`

OData: `$apply=groupby((country),aggregate(amount with sum as total))`. The grid's row grouping sends `rowGroupCols` (the
group columns, outermost first), `valueCols` (the aggregates: `sum`, `min`, `max`, `avg`, `count`) and `groupKeys` (the groups the
user has opened).

{{include site/examples/odata/08-apply-groupby-aggregate.json json}}

{{include site/examples/odata/08-apply-groupby-aggregate.sql.txt sql}}

A request returns **one level**. With `groupby((country,region))` the grid asks for the countries first; when the user opens
Germany it sends `groupKeys: [Germany]` and gets the regions of Germany, and one level further it gets the rows:

{{include site/examples/odata/09-apply-drill-down.json json}}

{{include site/examples/odata/09-apply-drill-down.sql.txt sql}}

Differences to know:

- `count` counts the non-null values of the column, not rows ([AGG-1](semantics.html#agg-1)). To count rows, count the stable key.
- The sum of a group with no values is `0`, and `avg`, `min`, `max` are `NULL` ([AGG-6](semantics.html#agg-6)).
- The group whose key is null is a group of its own, and you open it with a null group key ([GRP-7](semantics.html#grp-7)).
- A grouped level's order is by its group column or an aggregate; ordering groups by an aggregate is not available on the search
  engines (a composite aggregation lists groups in key order only).

## What has no equivalent

| OData | Why not, and what to do |
|---|---|
| `$expand`, navigation properties, `$filter` across a relationship | A schema reads one source (a table, a collection or an index) and every column is a field of it. Build a view or an index that joins the data and declare the view as the source. |
| `$select` | The columns are the schema's. A request cannot ask for a subset of them; hide a column by not declaring it. An export can name its columns. |
| `$search` | There is no free-text search across fields. Use the text filter on a column, or a search engine index with the `contains` filter. |
| `any` and `all` (lambda operators) | They need a collection property, which a flat source does not have. Denormalize, or filter in a view. |
| Arithmetic (`add`, `sub`, `mul`, `div`, `mod`) and `$compute` | The operator set is closed so every operator has a tested meaning on every database (AR-6 of the technical requirements). Expose the computed value as a column of the source. |
| Functions in `$filter` and `$orderby` (`tolower`, `toupper`, `trim`, `concat`, `substring`, `length`, `indexof`, `year`, `month`, `day`, `round`, `now`) | The same reason. Case-insensitivity is already the default for text, and a date part is a column or a range on the date. |
| `$apply` steps other than one `groupby` with `aggregate` (`filter(...)`, `topcount`, `concat`, `join`, `compute`, `rollup`) | One grouping request has one level and the five aggregates. Put the filter in `filterModel`, and compute the rest after the query. |
| `countdistinct`, `$count` inside `aggregate`, custom aggregates | The aggregate set is closed: `sum`, `min`, `max`, `avg`, `count`. |
| `$batch`, `$format`, `$metadata`, `$value`, actions and functions | They are about the transport and the service, not the query. Craticula compiles a query and has no service layer. |

## Next

- The rules these examples rely on: [semantics](semantics.html) and the [rule index](rules.html).
- Every setting of the schema: the [GridSchema reference](grid-schema.html).
- Run the same requests on a real database: the [C# quickstart](quickstart-csharp.html).
