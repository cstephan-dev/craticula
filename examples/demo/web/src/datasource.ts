import type { IServerSideDatasource } from 'ag-grid-enterprise';
import { createProviderGate } from './providerGate';
import { ApiError, fetchQuery, fetchRows, type GridRequest, type Provider, type QueryView, type WireGroupKey, type WireRequest } from './api';

/** What the page wants to hear about while the grid loads blocks. */
export interface DatasourceListener {
  /** A rows request failed (the grid also gets `fail()`). */
  onRowsError(error: ApiError): void;
  /** The compiled query of the latest request, or why it could not be compiled. */
  onQuery(request: GridRequest, view: QueryView | ApiError): void;
}

export function asApiError(error: unknown): ApiError {
  return error instanceof ApiError
    ? error
    : new ApiError(0, 'unexpected-error', error instanceof Error ? error.message : 'Unexpected error.');
}

/**
 * One difference between what the grid sends and what the library parses (found against the running API):
 * `groupKeys` are plain strings in the grid but typed values (`{type, value}`) on the wire. Every groupable column of the
 * schema is a string, so a key is a string value (a null key a null value).
 */
export function toWireRequest(request: GridRequest): WireRequest {
  const groupKeys = request.groupKeys.map(
    (key): WireGroupKey => (key == null ? { type: 'null' } : { type: 'string', value: key }),
  );
  return { ...request, groupKeys };
}

/**
 * The API names an aggregate column `<id>_<aggFunc>` (amount_sum); the grid reads it from the column's field (amount).
 * Group rows carry only the group column and the aggregates, so renaming on the keys of `valueCols` is enough.
 */
export function toGridRows(request: GridRequest, rows: Record<string, unknown>[]): Record<string, unknown>[] {
  if (request.valueCols.length === 0) {
    return rows;
  }
  return rows.map((row) => {
    const mapped = { ...row };
    for (const column of request.valueCols) {
      const wireKey = `${column.id}_${column.aggFunc}`;
      if (wireKey in mapped) {
        mapped[column.id] = mapped[wireKey];
        delete mapped[wireKey];
      }
    }
    return mapped;
  });
}

/**
 * The server-side datasource for one provider: the grid's request goes out, the API's answer goes back.
 * `isCurrent` tells whether this datasource's provider is still the one on screen; a late answer from a provider the
 * user has switched away from must not touch the banner or the query panel (the grid drops its own stale rows).
 */
export function createDatasource(
  provider: Provider,
  listener: DatasourceListener,
  isCurrent: () => boolean = () => true,
): IServerSideDatasource {
  return {
    getRows(params) {
      const request = params.request;
      const wire = toWireRequest(request);

      fetchRows(provider, wire).then(
        (result) => {
          params.success({
            rowData: toGridRows(request, result.rows),
            rowCount: result.lastRow ?? undefined,
            ...(result.groupLevelInfo != null ? { groupLevelInfo: result.groupLevelInfo } : {}),
          });
        },
        (error: unknown) => {
          params.fail();
          if (isCurrent()) {
            listener.onRowsError(asApiError(error));
          }
        },
      );

      fetchQuery(provider, wire).then(
        (view) => isCurrent() && listener.onQuery(request, view),
        (error: unknown) => isCurrent() && listener.onQuery(request, asApiError(error)),
      );
    },
  };
}

export { createProviderGate };
