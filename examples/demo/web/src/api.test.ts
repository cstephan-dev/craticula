import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  ApiError,
  PROVIDERS,
  fetchHealth,
  fetchQuery,
  fetchRows,
  fetchValues,
  providerToSelect,
  visibleProviders,
  type WireRequest,
} from './api';

const request = { startRow: 0, endRow: 100 } as WireRequest;

function stubFetch(impl: () => Promise<Response>) {
  const mock = vi.fn(impl);
  vi.stubGlobal('fetch', mock);
  return mock;
}

afterEach(() => vi.unstubAllGlobals());

describe('api error mapping', () => {
  it('maps a 400 error body to a typed error', async () => {
    stubFetch(async () =>
      Response.json({ error: { kind: 'unknown-field', message: "Field 'x' is not in the schema." } }, { status: 400 }),
    );
    const failure = await fetchRows('postgres', request).catch((e: unknown) => e);
    expect(failure).toBeInstanceOf(ApiError);
    expect(failure).toMatchObject({ status: 400, kind: 'unknown-field', message: "Field 'x' is not in the schema." });
  });

  it('maps a network failure to backend-unavailable', async () => {
    stubFetch(async () => {
      throw new TypeError('fetch failed');
    });
    await expect(fetchHealth()).rejects.toMatchObject({ status: 0, kind: 'backend-unavailable' });
  });

  it('maps a non-JSON server error to a generic typed error', async () => {
    stubFetch(async () => new Response('Bad gateway', { status: 502 }));
    await expect(fetchRows('mongo', request)).rejects.toMatchObject({ status: 502, kind: 'backend-unavailable' });
  });

  it('maps a non-JSON client error by status', async () => {
    stubFetch(async () => new Response('nope', { status: 404 }));
    await expect(fetchRows('mongo', request)).rejects.toMatchObject({ status: 404, kind: 'http-404' });
  });

  it('posts the set values request in the shape the API expects', async () => {
    const mock = stubFetch(async () => Response.json({ values: ['DE', null] }));
    await expect(fetchValues('mongo', 'country')).resolves.toEqual(['DE', null]);
    const [url, init] = mock.mock.calls[0] as unknown as [string, RequestInit];
    expect(url).toBe('/api/mongo/values');
    expect(JSON.parse(init.body as string)).toEqual({ setValuesRequest: { colId: 'country', filterModel: {} } });
  });
});

describe('provider list', () => {
  it('offers PostgreSQL, MongoDB, EF Core, SQLite, EF Core (SQLite), SQL Server, EF Core (SQL Server), MySQL and OpenSearch in that order', () => {
    expect(PROVIDERS).toEqual([
      { id: 'postgres', label: 'PostgreSQL' },
      { id: 'mongo', label: 'MongoDB' },
      { id: 'ef', label: 'EF Core' },
      { id: 'sqlite', label: 'SQLite' },
      { id: 'ef-sqlite', label: 'EF Core (SQLite)' },
      { id: 'sqlserver', label: 'SQL Server' },
      { id: 'ef-sqlserver', label: 'EF Core (SQL Server)' },
      { id: 'mysql', label: 'MySQL' },
      { id: 'opensearch', label: 'OpenSearch' },
    ]);
  });

  it('posts to the opensearch routes and reads a json query view', async () => {
    const mock = stubFetch(async () => Response.json({ kind: 'json', dialect: 'opensearch', text: '{}', parameters: [] }));
    await expect(fetchQuery('opensearch', request)).resolves.toMatchObject({ kind: 'json', dialect: 'opensearch' });
    expect((mock.mock.calls[0] as unknown as [string])[0]).toBe('/api/opensearch/query');
  });

  it('keeps the unsupported-shape kind and message of a 400 answer', async () => {
    stubFetch(async () => Response.json({ error: { kind: 'unsupported-shape', message: 'OpenSearch cannot order groups by an aggregate.' } }, { status: 400 }));
    await expect(fetchRows('opensearch', request)).rejects.toMatchObject({ status: 400, kind: 'unsupported-shape', message: 'OpenSearch cannot order groups by an aggregate.' });
  });

  it('posts to the mysql routes', async () => {
    const mock = stubFetch(async () => Response.json({ kind: 'sql', dialect: 'mysql', text: '', parameters: [] }));
    await fetchQuery('mysql', request);
    expect((mock.mock.calls[0] as unknown as [string])[0]).toBe('/api/mysql/query');
  });

  it('posts to the sqlserver routes', async () => {
    const mock = stubFetch(async () => Response.json({ kind: 'sql', dialect: 'mssql', text: '', parameters: [] }));
    await fetchQuery('sqlserver', request);
    expect((mock.mock.calls[0] as unknown as [string])[0]).toBe('/api/sqlserver/query');
  });

  it('posts to the sqlite routes', async () => {
    const mock = stubFetch(async () => Response.json({ kind: 'sql', dialect: 'sqlite', text: '', parameters: [] }));
    await fetchQuery('sqlite', request);
    expect((mock.mock.calls[0] as unknown as [string])[0]).toBe('/api/sqlite/query');
  });

  it('posts to the ef routes', async () => {
    const mock = stubFetch(async () => Response.json({ kind: 'sql', dialect: 'ef-npgsql', text: '', parameters: [] }));
    await fetchQuery('ef', request);
    expect((mock.mock.calls[0] as unknown as [string])[0]).toBe('/api/ef/query');
  });
});

describe('visible providers', () => {
  const ids = (list: readonly { id: string }[]) => list.map((item) => item.id);

  it('offers all three when the API reports all three', () => {
    expect(ids(visibleProviders({ status: 'ok', providers: ['ef', 'mongo', 'postgres'] }))).toEqual(['postgres', 'mongo', 'ef']);
  });

  it('offers only what the API reports', () => {
    expect(ids(visibleProviders({ status: 'ok', providers: ['mongo', 'postgres'] }))).toEqual(['postgres', 'mongo']);
  });

  it('ignores a provider the page does not know', () => {
    expect(ids(visibleProviders({ status: 'ok', providers: ['oracle', 'postgres'] }))).toEqual(['postgres']);
  });

  it('keeps the catalogue order whatever order the API reports', () => {
    expect(ids(visibleProviders({ status: 'ok', providers: ['ef', 'postgres', 'mongo'] }))).toEqual(['postgres', 'mongo', 'ef']);
  });

  it('offers the default three while health is unknown or the API is down', () => {
    expect(ids(visibleProviders(null))).toEqual(['postgres', 'mongo', 'ef']);
    expect(ids(visibleProviders(undefined))).toEqual(['postgres', 'mongo', 'ef']);
  });

  it('offers the default three when the answer is not ok or lists nothing the page knows', () => {
    expect(ids(visibleProviders({ status: 'degraded', providers: ['mongo'] }))).toEqual(['postgres', 'mongo', 'ef']);
    expect(ids(visibleProviders({ status: 'ok', providers: [] }))).toEqual(['postgres', 'mongo', 'ef']);
    expect(ids(visibleProviders({ status: 'ok', providers: ['oracle'] }))).toEqual(['postgres', 'mongo', 'ef']);
  });

  it('keeps the selected provider while it is offered', () => {
    expect(providerToSelect('mongo', visibleProviders({ status: 'ok', providers: ['mongo', 'postgres'] }))).toBe('mongo');
  });

  it('selects the first offered provider when the selected one is hidden', () => {
    expect(providerToSelect('postgres', visibleProviders({ status: 'ok', providers: ['ef', 'mongo'] }))).toBe('mongo');
  });
});
