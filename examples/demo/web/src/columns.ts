import type { ColDef, ColGroupDef, ValueFormatterParams } from 'ag-grid-enterprise';
import { fetchValues, type ApiError, type Provider } from './api';
import { asApiError } from './datasource';

/** The grid's `context` option: tells the column callbacks which provider is active. */
export interface GridContext {
  provider: Provider;
}

export interface ColumnContext {
  onError(error: ApiError): void;
}

/** The filter operators the library parses for numbers and dates (inRange is not on the wire). */
const NUMBER_OPTIONS = [
  'equals',
  'notEqual',
  'lessThan',
  'lessThanOrEqual',
  'greaterThan',
  'greaterThanOrEqual',
  'blank',
  'notBlank',
];
const DATE_OPTIONS = ['equals', 'notEqual', 'lessThan', 'greaterThan', 'blank', 'notBlank'];

/** The aggregations orders.schema.json allows on amount and quantity. */
const AGG_FUNCS = ['sum', 'avg', 'min', 'max', 'count'];

const twoDecimals = (params: ValueFormatterParams) =>
  typeof params.value === 'number' ? params.value.toFixed(2) : '';

/** Set filter whose values come from `/api/<provider>/values` (strings only; the wire format has no null entry). */
function serverSetFilter(colId: string, context: ColumnContext): Partial<ColDef> {
  return {
    filter: 'agSetColumnFilter',
    filterParams: {
      // Load the values again each time the filter opens: a failed load or a provider switch must not leave a stale list.
      refreshValuesOnOpen: true,
      values: (params: { context: GridContext; success: (values: string[]) => void }) => {
        fetchValues(params.context.provider, colId).then(
          (values) => params.success(values.filter((v): v is string => typeof v === 'string')),
          (error: unknown) => {
            context.onError(asApiError(error));
            params.success([]);
          },
        );
      },
    },
  };
}

export function buildColumnDefs(context: ColumnContext): (ColDef | ColGroupDef)[] {
  const group = (colId: string, headerName: string): ColDef => ({
    field: colId,
    headerName,
    enableRowGroup: true,
    ...serverSetFilter(colId, context),
  });

  return [
    { field: 'id', headerName: 'Id', filter: 'agNumberColumnFilter', filterParams: { filterOptions: NUMBER_OPTIONS }, width: 100 },
    { field: 'orderNo', headerName: 'Order no', filter: 'agTextColumnFilter' },
    { field: 'customer', headerName: 'Customer', filter: 'agTextColumnFilter', flex: 1, minWidth: 180 },
    group('country', 'Country'),
    group('category', 'Category'),
    group('status', 'Status'),
    {
      field: 'amount',
      headerName: 'Amount',
      type: 'rightAligned',
      filter: 'agNumberColumnFilter',
      filterParams: { filterOptions: NUMBER_OPTIONS },
      enableValue: true,
      allowedAggFuncs: AGG_FUNCS,
      valueFormatter: twoDecimals,
    },
    {
      field: 'quantity',
      headerName: 'Quantity',
      type: 'rightAligned',
      filter: 'agNumberColumnFilter',
      filterParams: { filterOptions: NUMBER_OPTIONS },
      enableValue: true,
      allowedAggFuncs: AGG_FUNCS,
    },
    { field: 'orderedAt', headerName: 'Ordered at', filter: 'agDateColumnFilter', filterParams: { filterOptions: DATE_OPTIONS }, minWidth: 200 },
    { field: 'shippedOn', headerName: 'Shipped on', filter: 'agDateColumnFilter', filterParams: { filterOptions: DATE_OPTIONS } },
    {
      field: 'isPriority',
      headerName: 'Priority',
      // A boolean column is filtered as a set of "true" / "false" (spec/fixtures/README.md).
      filter: 'agSetColumnFilter',
      filterParams: { values: ['true', 'false'] },
      valueFormatter: (params) => (params.value == null ? '' : String(params.value)),
    },
  ];
}
