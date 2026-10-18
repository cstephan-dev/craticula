import type { IServerSideGetRowsRequest } from 'ag-grid-community';

export type Provider = 'postgres' | 'mongo' | 'ef' | 'sqlite';

export const PROVIDERS: readonly { id: Provider; label: string }[] = [
  { id: 'postgres', label: 'PostgreSQL' },
  { id: 'mongo', label: 'MongoDB' },
  { id: 'ef', label: 'EF Core' },
  { id: 'sqlite', label: 'SQLite' },
];

/** What the switch offers while the API's provider list is unknown (loading, down) and in a plain setup. */
export const DEFAULT_PROVIDER_IDS: readonly Provider[] = ['postgres', 'mongo', 'ef'];

/** The request the grid builds for a block. */
export type GridRequest = IServerSideGetRowsRequest;

/** A group key as the API parses it: a typed value, not the plain string the grid holds. */
export type WireGroupKey = { type: 'string'; value: string } | { type: 'null' };

/**
 * What the page posts: the grid request with one change, `groupKeys` as typed values. The API parses it with the
 * library. (The other adaptation is on the way back: aggregate result keys `<colId>_<aggFunc>` are renamed to the
 * column id; see datasource.ts.) Anyone copying this demo must reproduce both.
 */
export type WireRequest = Omit<GridRequest, 'groupKeys'> & { groupKeys: WireGroupKey[]; customFilters?: Record<string, unknown> };

export interface RowsResponse {
  rows: Record<string, unknown>[];
  lastRow?: number | null;
  groupLevelInfo?: unknown;
}

export interface QueryView {
  kind: string;
  dialect: string;
  text: string;
  parameters: unknown[];
}

export interface HealthResponse {
  status: string;
  providers: string[];
}

function catalogue(ids: readonly string[]): readonly { id: Provider; label: string }[] {
  return PROVIDERS.filter((item) => ids.includes(item.id));
}

/**
 * The providers the page offers. With a health answer (status ok and a list): the catalogue entries the API reports, in
 * catalogue order; an id the page does not know is ignored. Without one (not fetched yet, API down), or when the API
 * reports none the page knows: the default three, so the switch stays usable and "API: unreachable" still renders.
 */
export function visibleProviders(health: HealthResponse | null | undefined): readonly { id: Provider; label: string }[] {
  if (health?.status === 'ok' && Array.isArray(health.providers)) {
    const reported = catalogue(health.providers);
    if (reported.length > 0) {
      return reported;
    }
  }
  return catalogue(DEFAULT_PROVIDER_IDS);
}

/** The provider to be on: the current one while it is offered, else the first offered one. */
export function providerToSelect(current: Provider, visible: readonly { id: Provider }[]): Provider {
  return visible.some((item) => item.id === current) ? current : (visible[0]?.id ?? current);
}

/** Every failure the page can show: the API's `{ error: { kind, message } }`, or a transport failure (status 0). */
export class ApiError extends Error {
  readonly status: number;
  readonly kind: string;

  constructor(status: number, kind: string, message: string) {
    super(message);
    this.name = 'ApiError';
    this.status = status;
    this.kind = kind;
  }
}

async function send(path: string, init?: RequestInit): Promise<Response> {
  let response: Response;
  try {
    response = await fetch(path, init);
  } catch {
    throw new ApiError(0, 'backend-unavailable', 'The API did not answer. Is it running on port 5080?');
  }
  if (!response.ok) {
    throw await toApiError(response);
  }
  return response;
}

async function toApiError(response: Response): Promise<ApiError> {
  try {
    const body: unknown = await response.json();
    const error = (body as { error?: { kind?: unknown; message?: unknown } } | null)?.error;
    if (typeof error?.kind === 'string' && typeof error.message === 'string') {
      return new ApiError(response.status, error.kind, error.message);
    }
  } catch {
    // Not JSON (a proxy error page, for instance): fall through to the generic form.
  }
  // The dev proxy answers 500 when nothing listens on the API port; say that instead of a bare status.
  const kind = response.status >= 500 ? 'backend-unavailable' : `http-${response.status}`;
  return new ApiError(response.status, kind, `The API answered HTTP ${response.status} without an error body.`);
}

function post(provider: Provider, route: string, body: unknown): Promise<Response> {
  return send(`/api/${provider}/${route}`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

/** A request body as posted: the server-side page's `WireRequest` or the Community page's flat request (both are plain JSON objects). */
export type RequestBody = WireRequest | object;

/** Anything thrown while talking to the API, as an ApiError the pages can show. */
export function asApiError(error: unknown): ApiError {
  return error instanceof ApiError
    ? error
    : new ApiError(0, 'unexpected-error', error instanceof Error ? error.message : 'Unexpected error.');
}

export async function fetchRows(provider: Provider, request: RequestBody): Promise<RowsResponse> {
  return (await post(provider, 'rows', request)).json() as Promise<RowsResponse>;
}

export async function fetchQuery(provider: Provider, request: RequestBody): Promise<QueryView> {
  return (await post(provider, 'query', request)).json() as Promise<QueryView>;
}

/** Distinct values of one column for the set filter, ascending, nulls last. */
export async function fetchValues(provider: Provider, colId: string): Promise<unknown[]> {
  const response = await post(provider, 'values', { setValuesRequest: { colId, filterModel: {} } });
  const body = (await response.json()) as { values?: unknown[] };
  return body.values ?? [];
}

export async function fetchExport(provider: Provider, request: unknown): Promise<Blob> {
  return (await post(provider, 'export.csv', request)).blob();
}

export async function fetchHealth(): Promise<HealthResponse> {
  return (await send('/api/health')).json() as Promise<HealthResponse>;
}
