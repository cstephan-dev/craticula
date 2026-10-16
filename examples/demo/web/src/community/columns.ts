import type { ColDef, ValueFormatterParams } from 'ag-grid-community';

/**
 * The operators the library parses for numbers and dates. Native in-range is left out on purpose: it carries no
 * inclusivity, so the library rejects it (SEM-4); a host that wants ranges sends them through customFilters. Community has
 * no set filter, so every filterable column here uses the text, number or date filter.
 */
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

const twoDecimals = (params: ValueFormatterParams) =>
  typeof params.value === 'number' ? params.value.toFixed(2) : '';

export const COMMUNITY_COLUMNS: ColDef[] = [
  { field: 'id', headerName: 'Id', filter: 'agNumberColumnFilter', filterParams: { filterOptions: NUMBER_OPTIONS }, width: 100 },
  { field: 'orderNo', headerName: 'Order no', filter: 'agTextColumnFilter' },
  { field: 'customer', headerName: 'Customer', filter: 'agTextColumnFilter', flex: 1, minWidth: 180 },
  { field: 'country', headerName: 'Country', filter: 'agTextColumnFilter' },
  { field: 'category', headerName: 'Category', filter: 'agTextColumnFilter' },
  { field: 'status', headerName: 'Status', filter: 'agTextColumnFilter' },
  {
    field: 'amount',
    headerName: 'Amount',
    type: 'rightAligned',
    filter: 'agNumberColumnFilter',
    filterParams: { filterOptions: NUMBER_OPTIONS },
    valueFormatter: twoDecimals,
  },
  {
    field: 'quantity',
    headerName: 'Quantity',
    type: 'rightAligned',
    filter: 'agNumberColumnFilter',
    filterParams: { filterOptions: NUMBER_OPTIONS },
  },
  { field: 'orderedAt', headerName: 'Ordered at', filter: 'agDateColumnFilter', filterParams: { filterOptions: DATE_OPTIONS }, minWidth: 200 },
  { field: 'shippedOn', headerName: 'Shipped on', filter: 'agDateColumnFilter', filterParams: { filterOptions: DATE_OPTIONS } },
  {
    field: 'isPriority',
    headerName: 'Priority',
    // A boolean column is filtered as a set of "true" / "false" on the Enterprise page; Community has no set filter.
    filter: false,
    valueFormatter: (params) => (params.value == null ? '' : String(params.value)),
  },
];
