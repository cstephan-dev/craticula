import { afterEach, describe, expect, it, vi } from 'vitest';
import type { IServerSideGetRowsParams } from 'ag-grid-enterprise';
import type { ApiError, GridRequest, QueryView } from './api';
import { createDatasource, createProviderGate, toGridRows, toWireRequest } from './datasource';

const request = {
  startRow: 0,
  endRow: 100,
  rowGroupCols: [],
  valueCols: [],
  pivotCols: [],
  pivotMode: false,
  groupKeys: [],
  filterModel: { customer: { filterType: 'text', type: 'contains', filter: 'GmbH' } },
  sortModel: [{ colId: 'amount', sort: 'desc' }],
} as GridRequest;

const view: QueryView = { kind: 'sql', dialect: 'postgres', text: 'SELECT 1', parameters: [] };

function setup(routes: Record<string, () => Response | Promise<Response>>, isCurrent: () => boolean = () => true) {
  const fetchMock = vi.fn(async (url: string) => {
    const route = Object.keys(routes).find((r) => url.endsWith(r));
    if (!route) {
      throw new Error(`unexpected ${url}`);
    }
    return routes[route]();
  });
  vi.stubGlobal('fetch', fetchMock);

  const success = vi.fn();
  const fail = vi.fn();
  const listener = { onRowsError: vi.fn(), onQuery: vi.fn() };
  const params = { request, success, fail } as unknown as IServerSideGetRowsParams;
  createDatasource('postgres', listener, isCurrent).getRows(params);
  return { fetchMock, success, fail, listener };
}

const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

afterEach(() => vi.unstubAllGlobals());

describe('server-side datasource', () => {
  it('posts the grid request and maps rows/lastRow to rowData/rowCount', async () => {
    const { fetchMock, success, fail, listener } = setup({
      '/rows': () => Response.json({ rows: [{ id: 1 }], lastRow: 1 }),
      '/query': () => Response.json(view),
    });
    await settle();

    const rowsCall = fetchMock.mock.calls.find(([url]) => url === '/api/postgres/rows') as unknown as [string, RequestInit];
    expect(JSON.parse(rowsCall[1].body as string)).toEqual(request);
    expect(success).toHaveBeenCalledWith({ rowData: [{ id: 1 }], rowCount: 1 });
    expect(fail).not.toHaveBeenCalled();
    expect(listener.onQuery).toHaveBeenCalledWith(request, view);
  });

  it('omits rowCount when the last row is unknown and forwards groupLevelInfo', async () => {
    const { success } = setup({
      '/rows': () => Response.json({ rows: [], lastRow: null, groupLevelInfo: { n: 1 } }),
      '/query': () => Response.json(view),
    });
    await settle();
    expect(success).toHaveBeenCalledWith({ rowData: [], rowCount: undefined, groupLevelInfo: { n: 1 } });
  });

  it('calls fail and reports the typed error on a 400', async () => {
    const { success, fail, listener } = setup({
      '/rows': () => Response.json({ error: { kind: 'invalid-filter', message: 'bad' } }, { status: 400 }),
      '/query': () => Response.json({ error: { kind: 'invalid-filter', message: 'bad' } }, { status: 400 }),
    });
    await settle();

    expect(success).not.toHaveBeenCalled();
    expect(fail).toHaveBeenCalledOnce();
    const error = listener.onRowsError.mock.calls[0][0] as ApiError;
    expect(error).toMatchObject({ status: 400, kind: 'invalid-filter' });
    expect(listener.onQuery.mock.calls[0][1]).toMatchObject({ kind: 'invalid-filter' });
  });
});

describe('answers from a provider that is no longer current', () => {
  const error400 = () => Response.json({ error: { kind: 'invalid-filter', message: 'bad' } }, { status: 400 });

  it('does not set the banner when a rows request fails after a provider switch', async () => {
    let current = true;
    const { fail, listener } = setup({ '/rows': error400, '/query': () => Response.json(view) }, () => current);
    current = false; // the user switched provider before the answer arrived
    await settle();

    expect(fail).toHaveBeenCalledOnce(); // the grid still gets its own fail(); it drops it for a stale datasource
    expect(listener.onRowsError).not.toHaveBeenCalled();
  });

  it('does not replace the query panel with a late /query from the old provider', async () => {
    let current = true;
    const { listener } = setup({ '/rows': () => Response.json({ rows: [], lastRow: 0 }), '/query': () => Response.json(view) }, () => current);
    current = false;
    await settle();

    expect(listener.onQuery).not.toHaveBeenCalled();
  });

  it('does not report a late /query failure from the old provider either', async () => {
    let current = true;
    const { listener } = setup({ '/rows': () => Response.json({ rows: [], lastRow: 0 }), '/query': error400 }, () => current);
    current = false;
    await settle();

    expect(listener.onQuery).not.toHaveBeenCalled();
  });
});

describe('provider gate', () => {
  it('is current only for the selected provider', () => {
    const gate = createProviderGate('postgres');
    expect(gate.isCurrent('postgres')).toBe(true);
    gate.select('mongo');
    expect(gate.isCurrent('postgres')).toBe(false);
    expect(gate.isCurrent('mongo')).toBe(true);
  });
});

describe('wire adaptation', () => {
  it('types the group keys and leaves a request without keys equal', () => {
    expect(toWireRequest(request)).toEqual(request);
    const drill = { ...request, groupKeys: ['Germany', null] } as unknown as GridRequest;
    expect(toWireRequest(drill).groupKeys).toEqual([{ type: 'string', value: 'Germany' }, { type: 'null' }]);
  });

  it('renames <id>_<aggFunc> keys to the column id', () => {
    const grouped = {
      ...request,
      valueCols: [
        { id: 'amount', aggFunc: 'sum' },
        { id: 'quantity', aggFunc: 'max' },
      ],
    } as unknown as GridRequest;
    expect(toGridRows(grouped, [{ country: 'Spain', amount_sum: 5, quantity_max: 9 }])).toEqual([
      { country: 'Spain', amount: 5, quantity: 9 },
    ]);
    expect(toGridRows(request, [{ id: 1 }])).toEqual([{ id: 1 }]);
  });
});
