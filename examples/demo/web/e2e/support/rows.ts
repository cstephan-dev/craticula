import type { APIRequestContext, Page, Response } from '@playwright/test';
import type { Provider } from '../../src/api';
import { apiUrl, BASELINE } from './providers';
import type { Row } from './divergences';

/** One answer of `POST /api/<provider>/rows` as the page received it. */
export interface RowsCapture {
  provider: Provider;
  status: number;
  /** The request body the page posted (the grid request, with typed group keys). */
  request: Record<string, unknown>;
  rows: Row[];
  /** The API's lastRow: the total row count, or null when it is unknown (more rows may follow). */
  lastRow: number | null;
  /** Column ids of the first row (a grouped level has its group column and the aggregates). */
  columns: string[];
  /** The error body of a failed answer; undefined on 200. */
  error?: { kind?: string; message?: string };
}

export interface CaptureOptions {
  /** Narrows the match to one request, for example the second block (`(request) => request.startRow === 100`). */
  where?: (request: Record<string, unknown>) => boolean;
  timeout?: number;
}

function isRowsCall(response: Response, provider: Provider): boolean {
  const request = response.request();
  return request.method() === 'POST' && new URL(response.url()).pathname === `/api/${provider}/rows`;
}

async function toCapture(provider: Provider, response: Response): Promise<RowsCapture> {
  const request = (response.request().postDataJSON() ?? {}) as Record<string, unknown>;
  const status = response.status();
  const body = (await response.json().catch(() => ({}))) as { rows?: Row[]; lastRow?: number | null; error?: RowsCapture['error'] };
  const rows = body.rows ?? [];
  return {
    provider,
    status,
    request,
    rows,
    lastRow: body.lastRow ?? null,
    columns: rows.length > 0 ? Object.keys(rows[0]) : [],
    ...(status === 200 ? {} : { error: body.error }),
  };
}

/**
 * Runs `action` (a click, a typed filter, a scroll) and returns the rows answer it caused for `provider`. The wait is
 * registered before the action, so a fast answer is not missed; it matches the provider's own URL only, so a late answer
 * of the provider the page has just left cannot satisfy it.
 */
export async function captureRows(
  page: Page,
  provider: Provider,
  action: () => Promise<unknown>,
  options: CaptureOptions = {},
): Promise<RowsCapture> {
  const { where, timeout } = options;
  const pending = page.waitForResponse(
    (response) => {
      if (!isRowsCall(response, provider)) {
        return false;
      }
      return where ? where((response.request().postDataJSON() ?? {}) as Record<string, unknown>) : true;
    },
    timeout === undefined ? undefined : { timeout },
  );
  await action();
  return toCapture(provider, await pending);
}

/** The same request sent straight to the API for another provider, without the page: what that provider answers. */
export async function fetchRowsFor(
  request: APIRequestContext,
  provider: Provider,
  body: Record<string, unknown>,
): Promise<RowsCapture> {
  const response = await request.post(`${apiUrl()}/api/${provider}/rows`, { data: body });
  const parsed = (await response.json().catch(() => ({}))) as { rows?: Row[]; lastRow?: number | null; error?: RowsCapture['error'] };
  const rows = parsed.rows ?? [];
  return {
    provider,
    status: response.status(),
    request: body,
    rows,
    lastRow: parsed.lastRow ?? null,
    columns: rows.length > 0 ? Object.keys(rows[0]) : [],
    ...(response.status() === 200 ? {} : { error: parsed.error }),
  };
}

/** What the baseline provider answers to the request a capture carried: the rows to compare the capture with. */
export function fetchBaselineFor(request: APIRequestContext, capture: RowsCapture): Promise<RowsCapture> {
  return fetchRowsFor(request, BASELINE, capture.request);
}
