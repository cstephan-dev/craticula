import type { IDatasource, IGetRowsParams } from 'ag-grid-community';
import { fetchQuery, fetchRows, type Provider, type QueryView, type WireRequest } from '../api';
import { asApiErrorOf, type DatasourceListenerLike } from './errors';

/**
 * What AG Grid Community's Infinite Row Model sends: `startRow`, `endRow`, `sortModel`, `filterModel` and nothing else (no
 * group, value or pivot fields). The API parses it as it is (AG-6); nothing is added on the way out.
 */
export interface FlatRequest {
  startRow: number;
  endRow: number;
  sortModel: IGetRowsParams['sortModel'];
  filterModel: IGetRowsParams['filterModel'];
}

export function toFlatRequest(params: Pick<IGetRowsParams, 'startRow' | 'endRow' | 'sortModel' | 'filterModel'>): FlatRequest {
  return {
    startRow: params.startRow,
    endRow: params.endRow,
    sortModel: params.sortModel,
    filterModel: params.filterModel,
  };
}

/** The text the Copy request button puts on the clipboard: the exact body posted, ready to become a fixture's `agGridRequest.json`. */
export function requestToClipboardText(request: FlatRequest): string {
  return `${JSON.stringify(request, null, 2)}\n`;
}

export interface FlatListener extends DatasourceListenerLike {
  /** The compiled query of the latest request, or why it could not be compiled. */
  onQuery(request: FlatRequest, view: QueryView | Error): void;
}

/**
 * The Infinite Row Model datasource for one provider. The API's `lastRow` goes back as the grid's last row (unknown stays
 * unknown). `isCurrent` tells whether this provider is still the one on screen; a late answer from a provider the user has
 * switched away from must not touch the banner or the query panel.
 */
export function createFlatDatasource(
  provider: Provider,
  listener: FlatListener,
  isCurrent: () => boolean = () => true,
): IDatasource {
  return {
    getRows(params) {
      const request = toFlatRequest(params);
      // The API's request type has group fields the flat request does not carry; the wire is just JSON.
      const wire = request as unknown as WireRequest;

      fetchRows(provider, wire).then(
        (result) => params.successCallback(result.rows, result.lastRow ?? undefined),
        (error: unknown) => {
          params.failCallback();
          if (isCurrent()) {
            listener.onRowsError(asApiErrorOf(error));
          }
        },
      );

      fetchQuery(provider, wire).then(
        (view) => isCurrent() && listener.onQuery(request, view),
        (error: unknown) => isCurrent() && listener.onQuery(request, asApiErrorOf(error)),
      );
    },
  };
}
