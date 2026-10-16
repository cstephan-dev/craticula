import type { IDatasource, IGetRowsParams } from 'ag-grid-community';
import { withTranslatedRanges } from '../inRange';
import { ApiError, asApiError, fetchQuery, fetchRows, type Provider, type QueryView } from '../api';

/**
 * What AG Grid Community's Infinite Row Model sends: `startRow`, `endRow`, `sortModel`, `filterModel` and nothing else (no
 * group, value or pivot fields). The API parses it as it is (AG-7); nothing is added on the way out.
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

export interface FlatListener {
  /** A rows request failed (the grid also gets `failCallback()`). */
  onRowsError(error: ApiError): void;
  /** The compiled query of the latest request, or why it could not be compiled. */
  onQuery(request: FlatRequest, view: QueryView | ApiError): void;
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
      // What the grid sent stays the request shown and copied; what is posted has any native in-range filter translated (inRange.ts).
      let wire: object;
      try {
        wire = withTranslatedRanges(request);
      } catch (error: unknown) {
        params.failCallback();
        if (isCurrent()) {
          const failure = asApiError(error);
          listener.onRowsError(failure);
          listener.onQuery(request, failure); // the panel and Copy request must not keep the previous request
        }
        return;
      }
      fetchRows(provider, wire).then(
        (result) => params.successCallback(result.rows, result.lastRow ?? undefined),
        (error: unknown) => {
          params.failCallback();
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
