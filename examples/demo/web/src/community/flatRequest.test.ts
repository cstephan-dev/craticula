import { afterEach, describe, expect, it, vi } from 'vitest';
import type { IGetRowsParams } from 'ag-grid-community';
import type { QueryView } from '../api';
import { createFlatDatasource, requestToClipboardText, toFlatRequest } from './flatRequest';
import { createProviderGate } from '../providerGate';

const grid = {
  startRow: 100,
  endRow: 200,
  sortModel: [{ colId: 'amount', sort: 'desc' }],
  filterModel: { customer: { filterType: 'text', type: 'contains', filter: 'GmbH' } },
  context: { anything: true },
  successCallback: vi.fn(),
  failCallback: vi.fn(),
} as unknown as IGetRowsParams;

const view: QueryView = { kind: 'sql', dialect: 'postgres', text: 'SELECT 1', parameters: [] };
const settle = () => new Promise((resolve) => setTimeout(resolve, 0));

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
  createFlatDatasource('postgres', listener, isCurrent).getRows({ ...grid, successCallback: success, failCallback: fail } as IGetRowsParams);
  return { fetchMock, success, fail, listener };
}

afterEach(() => vi.unstubAllGlobals());

describe('the flat request', () => {
  it('carries only startRow, endRow, sortModel and filterModel (no group, value, pivot or context fields)', () => {
    expect(Object.keys(toFlatRequest(grid)).sort()).toEqual(['endRow', 'filterModel', 'sortModel', 'startRow']);
  });

  it('is copied as the exact JSON body, ready for a fixture', () => {
    const text = requestToClipboardText(toFlatRequest(grid));
    expect(JSON.parse(text)).toEqual({
      startRow: 100,
      endRow: 200,
      sortModel: grid.sortModel,
      filterModel: grid.filterModel,
    });
    expect(text.endsWith('\n')).toBe(true);
  });
});

describe('the Infinite Row Model datasource', () => {
  it('posts the flat request and hands rows and lastRow to the grid', async () => {
    const { fetchMock, success, fail, listener } = setup({
      '/rows': () => Response.json({ rows: [{ id: 1 }], lastRow: 1 }),
      '/query': () => Response.json(view),
    });
    await settle();

    const call = fetchMock.mock.calls.find(([url]) => url === '/api/postgres/rows') as unknown as [string, RequestInit];
    expect(JSON.parse(call[1].body as string)).toEqual(toFlatRequest(grid));
    expect(success).toHaveBeenCalledWith([{ id: 1 }], 1);
    expect(fail).not.toHaveBeenCalled();
    expect(listener.onQuery).toHaveBeenCalledWith(toFlatRequest(grid), view);
  });

  it('keeps an unknown last row unknown', async () => {
    const { success } = setup({
      '/rows': () => Response.json({ rows: [], lastRow: null }),
      '/query': () => Response.json(view),
    });
    await settle();
    expect(success).toHaveBeenCalledWith([], undefined);
  });

  it('fails the grid and reports the API error when the rows request is refused', async () => {
    const { success, fail, listener } = setup({
      '/rows': () => Response.json({ error: { kind: 'invalid-filter', message: 'nope' } }, { status: 400 }),
      '/query': () => Response.json(view),
    });
    await settle();
    expect(success).not.toHaveBeenCalled();
    expect(fail).toHaveBeenCalled();
    expect(listener.onRowsError.mock.calls[0][0]).toMatchObject({ kind: 'invalid-filter' });
  });

  it('stays quiet about a late answer once the user has switched provider', async () => {
    const gate = createProviderGate('mongo');
    const { fail, listener } = setup(
      {
        '/rows': () => Response.json({ error: { kind: 'invalid-filter', message: 'late' } }, { status: 400 }),
        '/query': () => Response.json(view),
      },
      () => gate.isCurrent('postgres'),
    );
    await settle();
    expect(fail).toHaveBeenCalled();
    expect(listener.onRowsError).not.toHaveBeenCalled();
    expect(listener.onQuery).not.toHaveBeenCalled();
  });
});
