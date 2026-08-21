# 023 — two `valueCols` resolving to one output name

## What makes this adversarial
Both `valueCols` entries carry `id: "total"` while naming **different fields** (`amount` and
`quantity`). AGG-2 sets `aggregation.alias` from `ColumnVO.id` whenever it differs from
`ColumnVO.field`, so both aggregations get the alias `total`, and GRP-4 resolves both to the output
name `total`.

The result is not a syntax error anywhere. On MongoDB a `$group` stage is a document and on
Elasticsearch an `aggs` container is a JSON object, so the second key **replaces** the first and one
accumulator disappears from a pipeline that is otherwise perfect. On SQL the statement is
`SELECT SUM("amount") AS "total", SUM("quantity") AS "total" …`, which every one of the five v1
engines accepts; the grid then reads `total` out of the response and gets whichever column the driver
handed back — a number belonging to a different field, with no error anywhere in the stack.

AG Grid's own column model cannot produce this: a `ColumnVO.id` is a grid column's identity and is
unique by construction. It is therefore purely a **wire-shape** attack, exactly like `018`/`020`'s
value-injection shapes — nothing upstream of the server prevents a client from sending it.

Two aggregations over the *same* field with *different* functions (`sum` and `avg` on `amount`) are
**not** this case and stay legal: their default output names are `amount_sum` and `amount_avg`, which
do not collide. The rule is about the resolved output name, not about the field.

## Which validation stage must catch it
**Request parsing.** `AggregationValidator` runs inside `AgGridRequestParser.Parse`, alongside the
`filterNode`/`sortItem`/`groupSpec`/`pageSpec` structural validators and before the request reaches
the allowlist. The check lives in that shared validator rather than in a compiler on purpose (#1886):
two of the three compilers already refused the shape and one — `Craticula.Sql` — did not, so putting
it upstream gives all four backends (the EF Core adapter included) the same answer and closes the
grouped-`ORDER BY` ambiguity a duplicated name creates.

## Requirement ids
AGG-2 (`alias` from `ColumnVO.id`), GRP-4 (output-name resolution), SEC-2, SEC-6, AR-8.
