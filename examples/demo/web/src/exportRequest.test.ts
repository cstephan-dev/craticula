import { describe, expect, it } from 'vitest';
import { buildExportRequest } from './exportRequest';

const ids = new Set(['id', 'amount']);

describe('export request', () => {
  it('is a leaf, unpaged csv request carrying the current filter and sort order', () => {
    const body = buildExportRequest(
      { status: { filterType: 'set', values: ['shipped'] } },
      [
        { colId: 'amount', sort: 'desc', sortIndex: 1 },
        { colId: 'id', sort: 'asc', sortIndex: 0 },
        { colId: 'ag-Grid-AutoColumn', sort: 'asc', sortIndex: 2 },
        { colId: 'status', sort: null },
      ],
      ids,
    );
    expect(body).toMatchObject({
      startRow: null,
      endRow: null,
      rowGroupCols: [],
      valueCols: [],
      groupKeys: [],
      exportFormat: 'csv',
      exportScope: 'all',
      filterModel: { status: { filterType: 'set', values: ['shipped'] } },
      sortModel: [
        { colId: 'id', sort: 'asc' },
        { colId: 'amount', sort: 'desc' },
      ],
    });
  });

  it('sends a null filter model when nothing is filtered', () => {
    expect(buildExportRequest({}, [], ids).filterModel).toBeNull();
  });
});
