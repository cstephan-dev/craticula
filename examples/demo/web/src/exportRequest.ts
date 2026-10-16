import type { ColumnState } from 'ag-grid-enterprise';
import type { SortModelItem } from 'ag-grid-enterprise';
import { translateInRange, type CustomFilterNode } from './inRange';

/**
 * The body of `POST /api/<provider>/export.csv`: a rows request with no page (the whole result), no grouping (the export
 * is leaf rows) and the CSV extension. Same shape as Api.Tests/Requests.cs `StatusExport`.
 */
export interface ExportRequest {
  startRow: null;
  endRow: null;
  rowGroupCols: [];
  valueCols: [];
  pivotCols: [];
  pivotMode: false;
  groupKeys: [];
  filterModel: Record<string, unknown> | null;
  /** The grid's in-range filters, translated (inRange.ts); absent when there are none. */
  customFilters?: CustomFilterNode;
  sortModel: SortModelItem[];
  exportFormat: 'csv';
  exportScope: 'all';
}

/** Sorted columns in sort order. Only real data columns count: the auto group column is not a schema field. */
export function sortModelFrom(state: ColumnState[], fieldIds: ReadonlySet<string>): SortModelItem[] {
  return state
    .filter((column) => column.sort != null && fieldIds.has(column.colId))
    .sort((a, b) => (a.sortIndex ?? 0) - (b.sortIndex ?? 0))
    .map((column) => ({ colId: column.colId, sort: column.sort as 'asc' | 'desc' }));
}

export function buildExportRequest(
  filterModel: Record<string, unknown> | null,
  state: ColumnState[],
  fieldIds: ReadonlySet<string>,
): ExportRequest {
  const translated = translateInRange(filterModel);
  const hasFilter = Object.keys(translated.filterModel).length > 0;
  return {
    startRow: null,
    endRow: null,
    rowGroupCols: [],
    valueCols: [],
    pivotCols: [],
    pivotMode: false,
    groupKeys: [],
    filterModel: hasFilter ? translated.filterModel : null,
    ...(translated.customFilters ? { customFilters: translated.customFilters } : {}),
    sortModel: sortModelFrom(state, fieldIds),
    exportFormat: 'csv',
    exportScope: 'all',
  };
}
