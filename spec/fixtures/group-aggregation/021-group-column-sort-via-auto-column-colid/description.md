# 021 - Grouped level sorted through the auto group column's colId

**Proves:** BM-2, AR-9, GRP-4, AG-6

Identical to `002` in every respect except its `sortModel`: same `rowGroupCols`, same one consumed
`groupKey`, same `valueCols`, same `schema.json`. What it adds is the single most common interaction
a grouped SSRM grid has — **the user clicks the group column's header** — and the wire shape that
produces, which until #1640 no fixture carried at all (every other grouped case sends
`sortModel: []` or sorts an aggregate).

## The wire shape, read from AG Grid's source rather than recalled

Two facts, both read at `release-34.0.0` and unchanged across the supported v31-v36 range:

1. **The colId is AG Grid's, not the schema's.** `packages/ag-grid-community/src/columns/columnUtils.ts`
   declares `export const GROUP_AUTO_COLUMN_ID = 'ag-Grid-AutoColumn'`, and
   `packages/ag-grid-community/src/sort/sortService.ts`'s `getSortModel()` writes
   `colId: column.getId()` verbatim — so sorting the row-group display column puts
   `"ag-Grid-AutoColumn"` in `sortModel`, matching **no** `GridSchema.fields` key.
   `packages/ag-grid-enterprise/src/serverSideRowModel/listeners/sortListener.ts` passes that model
   to the SSRM request untouched. Under `groupDisplayType: 'multipleColumns'`,
   `packages/ag-grid-enterprise/src/rowHierarchy/autoColService.ts`'s `createOneAutoCol` mints one
   column per row-group column with
   ``colId = `${GROUP_AUTO_COLUMN_ID}-${rowGroupCol.getId()}` `` — note the suffix is the source
   column's **`ColumnVO.id`**, which is what the adapter looks it up by.

2. **Sort coupling emits the display column plus EVERY row-group column — 1 + N entries, not a
   pair.** `gridOptionsUtils._isColumnsSortingCoupledToGroup` is true by default (it is false only
   for `treeData` or a custom `autoGroupColumnDef.comparator`), so
   `sortService.setSortForColumn` sets the sort on `[column, ...sortableRowGroupColumns]` — the
   display column *and* every source column
   `showRowGroupColsService.getSourceColumnsForGroupColumn` returns for it. For the **default single
   auto column** that is all of them: `autoColService.createBaseColDef` gives it
   `showRowGroup: rowGroupCol?.getColId() ?? true`, i.e. the literal `true`, and that service answers
   `true` with `rowGroupColsSvc.columns.slice(0)`. `getIndexedSortMap` then gives every one of them
   the display column's own index, so `getColumnsWithSortingOrdered`'s stable sort emits the display
   column first (it is inserted into the index map first) followed by the row-group columns in
   `rowGroupCols` order. Hence this fixture's **four**-entry `sortModel`, all `desc`: with
   `rowGroupCols = [region, segment, product]`, one click on the group header sends
   `[ag-Grid-AutoColumn, region, segment, product]`. That is what the grid actually sends — a
   two-entry model is what a `multipleColumns` grid sends, not this one.

   *(#1640's review caught this: the fixture originally carried the two-entry model, which its own
   `rowGroupCols` could never produce, and the adapter's collapse was written to match it. With the
   real payload the adapter kept `region` and `product` as live sort keys and `GRP-4` rejected the
   request — so the fixture's whole subject, the commonest grouped interaction, still failed.)*

## What the artifacts pin

- **Resolution (AG-6).** `ag-Grid-AutoColumn` resolves to the group column *this request's own level
  buckets by* — GRP-1's `groupCols[groupKeys.length]`, i.e. `segment` — before the IR is built. It is
  a translation between two colId spaces, so it belongs in the AG Grid adapter and nowhere else.
- **Collapse of the coupled set.** The three entries AG Grid coupled to the display column —
  `region`, `segment`, `product`, i.e. the *whole* `rowGroupCols` field list — are dropped, because
  the display column already stands for all of them: AG Grid treats them as **one logical key**. The
  **first** occurrence is what survives, so the user's key precedence is preserved. Without this the
  statement would carry `segment` twice and, worse, would carry `region` (already pinned to one value
  by the drill-down) and `product` (a level this response does not produce) as live sort keys, which
  GRP-4 rejects outright.
- **SEM-3/SEM-6.** `desc` takes the adapter's pinned `nulls first` default, and SEM-6 does **not**
  append the tiebreaker a second time because the sort already ends in the group column (GRP-4). So
  the whole ordering clause is one key — which is exactly how it differs from `002`'s, and what makes
  this case fail if the resolution silently dropped the entry instead of mapping it.
- **GRP-2** is untouched here: the drill-down `WHERE "region" = $1` stays exact and the grouped
  `ORDER BY` stays bare, for `002`'s reasons.

## Known divergence note (AGG-5)

The Search arm's composite source carries `"order": "desc"` (002 leaves it absent for the ascending
default) and `missing_bucket: true`. AGG-5's accepted divergence — the composite null bucket always
sorts **first** regardless of direction, with no `missing_order` control in the pinned 7.10-era
subset — happens to *agree* with the other backends in this case, since `desc` requests nulls first
anyway. It is still a coincidence of this case's direction, not a resolution of AGG-5.
