import { ApiError } from '../api';

/** What the Community page wants to hear about while the grid loads rows. */
export interface DatasourceListenerLike {
  /** A rows request failed (the grid also gets `failCallback()`). */
  onRowsError(error: ApiError): void;
}

/**
 * The same conversion datasource.ts has, kept here so this entry imports nothing from the Enterprise page's modules
 * (datasource.ts and columns.ts import types from ag-grid-enterprise).
 */
export function asApiErrorOf(error: unknown): ApiError {
  return error instanceof ApiError
    ? error
    : new ApiError(0, 'unexpected-error', error instanceof Error ? error.message : 'Unexpected error.');
}
