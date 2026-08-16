# Export ignores an active drill-down (`groupKeys`) — leaf rows, no bucket predicate

**Proves:** the third member of EXP-1(a)'s group-export carve-out. `010` and `011` pin that
`rowGroupCols` and `valueCols` are discarded, but both send `groupKeys: []` — so until this fixture
the *drill-down key's* discard was asserted by nothing, and a compiler that honoured it would have
passed the whole category.

## Scenario

Two group levels (`region`, then `status`) with **one key consumed** —
`groupKeys: [{ "type": "string", "value": "EMEA" }]` — plus a `sum` aggregation, and
`exportFormat: "csv"` / `exportScope: "all"`. Without the export members this is a *partially
consumed* path (`groupKeys.length < rowGroupCols.length`) — the shape GRP-1 dispatches to a **grouped
level**, here on `status` under a `region = 'EMEA'` drill-down predicate, exactly as
`group-aggregation/002` pins for a non-export request.

## Key expectations

1. **No drill-down predicate.** The compiled artifacts carry no `region` comparison anywhere:
   Postgres has no `WHERE` clause at all, Mongo no `$match` stage, Search `match_all`. A compiler
   that kept the key would show it in exactly those three places.
2. **No grouped level.** No `GROUP BY`/`$group`/`composite`, and no `sum` aggregate — the request's
   `valueCols` is as inert as `010`'s.
3. **The three arms are byte-identical to `010`'s and `011`'s.** That identity *is* the pin: the only
   wire difference between the three fixtures is group state, and EXP-1(a) discards all of it. Seven
   columns in schema declaration order (the default projection, since no `exportColumns` is sent),
   `ORDER BY "id" ASC NULLS LAST` from SEM-6, no row-range clause from EXP-2's `page: none`.

## Why discarded rather than refused

Dropping a drill-down key exports strictly more rows than the grid is showing. That was once the
argument for rejecting this request — but it is equally true of dropping `rowGroupCols`, which
EXP-1(a) mandates, so it does not separate the three members. What decides it is the obvious host
integration: an export request built by copying the grid's last `getRows` payload and adding
`exportFormat` carries whatever path the user had expanded, and refusing it would hard-fail every
export taken from an expanded grid. See EXP-1(a) in `spec/semantics.md`, which also records the one
combination that stays genuinely incoherent and unpinned (`exportScope: "page"` over a grouped view,
where the row range was counted in grouped rows and the export emits leaf rows).

## SEM-6 tiebreaker

`sortModel` is empty and the group state is gone before any sort is resolved, so the compiler appends
the schema's `stableKey` (`id`) as the sole sort key — the leaf-row tiebreaker, not the grouped-level
exemption, because after the carve-out there is no grouped level left to exempt.
