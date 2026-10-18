import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, PROVIDERS, fetchHealth, fetchQuery, fetchRows, fetchValues, type WireRequest } from './api';

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
  it('offers PostgreSQL, MongoDB and EF Core in that order', () => {
    expect(PROVIDERS).toEqual([
      { id: 'postgres', label: 'PostgreSQL' },
      { id: 'mongo', label: 'MongoDB' },
      { id: 'ef', label: 'EF Core' },
    ]);
  });

  it('posts to the ef routes', async () => {
    const mock = stubFetch(async () => Response.json({ kind: 'sql', dialect: 'ef-npgsql', text: '', parameters: [] }));
    await fetchQuery('ef', request);
    expect((mock.mock.calls[0] as unknown as [string])[0]).toBe('/api/ef/query');
  });
});
