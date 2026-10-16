import { afterEach, describe, expect, it, vi } from 'vitest';
import type { IGetRowsParams } from 'ag-grid-community';
import type { IServerSideGetRowsParams } from 'ag-grid-enterprise';
import type { GridRequest, QueryView } from './api';
import { createDatasource, toWireRequest } from './datasource';
import { buildExportRequest } from './exportRequest';
import { createFlatDatasource, toFlatRequest } from './community/flatRequest';

const range = { filterType: 'number', type: 'inRange', filter: 100, filterTo: 500 };
const customer = { filterType: 'text', type: 'contains', filter: 'GmbH' };
const orAndRange = { filterType: 'number', operator: 'OR', conditions: [{ filterType: 'number', type: 'equals', filter: 7 }, range] };
const view: QueryView = { kind: 'sql', dialect: 'postgres', text: 'SELECT 1', parameters: [] };
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

const EXPECTED_RANGE = {
  type: 'comparison',
  field: 'quantity',
  op: 'inRange',
  from: { type: 'int64', value: 100 },
  to: { type: 'int64', value: 500 },
  fromInclusive: false,
  toInclusive: false,
};

function stubFetch() {
  const fetchMock = vi.fn(async (url: string) =>
    url.endsWith('/rows') ? Response.json({ rows: [], lastRow: 0 }) : Response.json(view),
  );
  vi.stubGlobal('fetch', fetchMock);
  return fetchMock;
}

const posted = (fetchMock: ReturnType<typeof stubFetch>, route: string) => {
  const call = fetchMock.mock.calls.find(([url]) => url === `/api/postgres/${route}`) as unknown as [string, RequestInit];
  return JSON.parse(call[1].body as string) as Record<string, unknown>;
};

afterEach(() => vi.unstubAllGlobals());

describe('the server-side page', () => {
  const request = (filterModel: Record<string, unknown>) =>
    ({ startRow: 0, endRow: 100, rowGroupCols: [], valueCols: [], pivotCols: [], pivotMode: false, groupKeys: [], filterModel, sortModel: [] }) as GridRequest;

  it('posts a range as customFilters and leaves the other filters in the filter model', async () => {
    const fetchMock = stubFetch();
    const success = vi.fn();
    const listener = { onRowsError: vi.fn(), onQuery: vi.fn() };
    createDatasource('postgres', listener).getRows({ request: request({ quantity: range, customer }), success, fail: vi.fn() } as unknown as IServerSideGetRowsParams);
    await settle();

    for (const route of ['rows', 'query']) {
      const body = posted(fetchMock, route);
      expect(body.filterModel).toEqual({ customer });
      expect(body.customFilters).toEqual(EXPECTED_RANGE);
    }
    expect(success).toHaveBeenCalled();
  });

  it('fails the block and shows the reason, sending nothing, for a range joined by OR', async () => {
    const fetchMock = stubFetch();
    const fail = vi.fn();
    const listener = { onRowsError: vi.fn(), onQuery: vi.fn() };
    createDatasource('postgres', listener).getRows({ request: request({ quantity: orAndRange }), success: vi.fn(), fail } as unknown as IServerSideGetRowsParams);
    await settle();

    expect(fetchMock).not.toHaveBeenCalled();
    expect(fail).toHaveBeenCalled();
    expect(listener.onRowsError.mock.calls[0][0]).toMatchObject({ kind: 'unsupported-filter' });
    expect(listener.onQuery.mock.calls[0][1]).toMatchObject({ kind: 'unsupported-filter' });
  });

  it('never lets a native in-range condition go out, even an empty one that produces no custom filter', () => {
    const empty = { quantity: { ...range, filter: null, filterTo: null }, customer };
    const wire = toWireRequest(request(empty));
    expect(wire.filterModel).toEqual({ customer });
    expect(wire.customFilters).toBeUndefined();
  });

  it('keeps a request without a range exactly as it was', () => {
    const plain = request({ customer });
    expect(toWireRequest(plain)).toEqual(plain);
  });

  it('puts the translated range into the CSV export too', () => {
    const body = buildExportRequest({ quantity: range, customer }, [], new Set(['quantity']));
    expect(body.filterModel).toEqual({ customer });
    expect(body.customFilters).toEqual(EXPECTED_RANGE);
    expect(buildExportRequest({ quantity: range }, [], new Set()).filterModel).toBeNull();
    expect(() => buildExportRequest({ quantity: orAndRange }, [], new Set())).toThrow(/OR/);
  });
});

describe('the Community page', () => {
  const grid = (filterModel: Record<string, unknown>) =>
    ({ startRow: 0, endRow: 100, sortModel: [], filterModel, successCallback: vi.fn(), failCallback: vi.fn() }) as unknown as IGetRowsParams;

  it('posts the translated request but reports the raw request the grid sent', async () => {
    const fetchMock = stubFetch();
    const listener = { onRowsError: vi.fn(), onQuery: vi.fn() };
    const params = grid({ quantity: range });
    createFlatDatasource('postgres', listener).getRows(params);
    await settle();

    const body = posted(fetchMock, 'rows');
    expect(body.filterModel).toEqual({});
    expect(body.customFilters).toEqual(EXPECTED_RANGE);
    expect(listener.onQuery).toHaveBeenCalledWith(toFlatRequest(params), view);
    expect(JSON.stringify(toFlatRequest(params))).toContain('"inRange"');
  });

  it('fails the block, sending nothing, for a range joined by OR', async () => {
    const fetchMock = stubFetch();
    const params = grid({ quantity: orAndRange });
    const listener = { onRowsError: vi.fn(), onQuery: vi.fn() };
    createFlatDatasource('postgres', listener).getRows(params);
    await settle();

    expect(fetchMock).not.toHaveBeenCalled();
    expect(params.failCallback).toHaveBeenCalled();
    expect(listener.onRowsError.mock.calls[0][0]).toMatchObject({ kind: 'unsupported-filter' });
    // the query panel (and Copy request) move to the refused request instead of keeping the previous one
    expect(listener.onQuery.mock.calls[0][0]).toEqual(toFlatRequest(params));
    expect(listener.onQuery.mock.calls[0][1]).toMatchObject({ kind: 'unsupported-filter' });
  });
});
