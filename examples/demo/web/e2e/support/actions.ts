import { expect, type APIRequestContext, type Locator, type Page } from '@playwright/test';
import type { Provider } from '../../src/api';
import { GRID, type GridPage } from '../pages/grid';
import { expectSameRows } from './compare';
import { BASELINE } from './providers';
import { captureRows, fetchBaselineFor, type RowsCapture } from './rows';

/** The grid request a capture carried, read as the few members the specs look at. */
export interface SentRequest {
  startRow?: number | null;
  rowGroupCols?: Array<{ id: string }>;
  valueCols?: Array<{ id: string; aggFunc: string }>;
  groupKeys?: unknown[];
  filterModel?: Record<string, { values?: unknown[] } | undefined> | null;
  customFilters?: unknown;
  sortModel?: Array<{ colId: string; sort: string }>;
}

export const sent = (request: Record<string, unknown>): SentRequest => request as SentRequest;

/** Opens the page on `provider`: the first block of that provider is the answer returned. */
export async function startOn(grid: GridPage, provider: Provider): Promise<RowsCapture> {
  if (provider === BASELINE) {
    return captureRows(grid.page, BASELINE, () => grid.open());
  }
  await grid.open();
  return captureRows(grid.page, provider, () => grid.providerButton(provider).click());
}

/** Leaves an open filter popup. */
export async function closePopup(page: Page): Promise<void> {
  await page.keyboard.press('Escape');
  await expect(page.locator(GRID.popup).locator(GRID.filterBody)).toHaveCount(0);
}

async function chooseOperator(grid: GridPage, popup: Locator, label: string, index = 0): Promise<void> {
  await popup.locator(GRID.operatorPicker).nth(index).click();
  await grid.page.getByRole('option', { name: label, exact: true }).click();
}

const filteredOn = (colId: string) => (request: Record<string, unknown>) => {
  const model = sent(request).filterModel;
  return model != null && colId in model;
};

/** Types a text filter ("Contains" unless another operator is named) and returns the rows answer it caused. */
export async function textFilter(grid: GridPage, provider: Provider, colId: string, value: string, operator?: string): Promise<RowsCapture> {
  const popup = await grid.openFilter(colId);
  if (operator) {
    await chooseOperator(grid, popup, operator);
  }
  const capture = await captureRows(grid.page, provider, () => popup.locator(GRID.filterFrom).first().fill(value), { where: filteredOn(colId) });
  await closePopup(grid.page);
  return capture;
}

/** A number or date filter with one operator and one value (a date as yyyy-mm-dd). */
export async function valueFilter(grid: GridPage, provider: Provider, colId: string, operator: string, value: string): Promise<RowsCapture> {
  return textFilter(grid, provider, colId, value, operator);
}

/** "Between" on a number or date column; the page translates it into customFilters, so the request carries those. */
export async function rangeFilter(grid: GridPage, provider: Provider, colId: string, kind: 'number' | 'date', from: string, to: string): Promise<RowsCapture> {
  const popup = await grid.openFilter(colId);
  await chooseOperator(grid, popup, 'Between');
  const fillBoth = async () => {
    if (kind === 'number') {
      await popup.locator(GRID.filterRangeFrom).fill(from);
      await popup.locator(GRID.filterTo).fill(to);
    } else {
      await popup.locator(GRID.filterFrom).nth(0).fill(from);
      await popup.locator(GRID.filterFrom).nth(1).fill(to);
    }
  };
  const capture = await captureRows(grid.page, provider, fillBoth, { where: (request) => sent(request).customFilters !== undefined });
  await closePopup(grid.page);
  return capture;
}

function sameSet(left: unknown[] | undefined, right: readonly string[]): boolean {
  return left !== undefined && left.length === right.length && right.every((value) => left.includes(value));
}

/** Columns whose set filter has a fixed list (the page never asks the API for their values). */
const FIXED_LIST = new Set(['isPriority']);

/** Opens a set filter and waits for its list: `/values` answered (unless the list is fixed) and the items are drawn. */
export async function openSetFilter(grid: GridPage, provider: Provider, colId: string): Promise<Locator> {
  const values = FIXED_LIST.has(colId)
    ? null
    : grid.page.waitForResponse((response) => new URL(response.url()).pathname === `/api/${provider}/values`);
  const popup = await grid.openFilter(colId);
  if (values) {
    expect((await values).status()).toBe(200);
  }
  await expect(popup.locator(GRID.setFilterItem).nth(1)).toBeVisible();
  return popup;
}

/** Sets the filter to exactly `values` (an empty list clears every value) and returns the rows answer for that selection. */
export async function setFilterTo(grid: GridPage, provider: Provider, colId: string, values: readonly string[], open?: Locator): Promise<RowsCapture> {
  const popup = open ?? (await openSetFilter(grid, provider, colId));
  const item = (text: string) => popup.locator(GRID.setFilterItem).getByText(text, { exact: true });
  const matches = (request: Record<string, unknown>) => {
    const model = sent(request).filterModel;
    return model != null && colId in model && sameSet(model[colId]?.values, values);
  };
  // "(Select All)" starts ticked, so one click clears the whole list; then each wanted value is ticked.
  const steps = async () => {
    await item('(Select All)').click();
    for (const value of values) {
      await item(value).click();
    }
  };
  const capture = await captureRows(grid.page, provider, steps, { where: matches });
  await closePopup(grid.page);
  return capture;
}

/** Group by `label` through the column menu; returns the rows answer of the new grouping. */
export async function groupByMenu(grid: GridPage, provider: Provider, colId: string, label: string, levels: number): Promise<RowsCapture> {
  await grid.openColumnMenu(colId);
  const capture = await captureRows(grid.page, provider, () => grid.menuOption(`Group by ${label}`).click(), {
    where: (request) => (sent(request).rowGroupCols?.length ?? 0) === levels && (sent(request).groupKeys?.length ?? 0) === 0,
  });
  return capture;
}

export async function openColumnsPanel(grid: GridPage): Promise<Locator> {
  const panel = grid.page.locator(GRID.columnsPanel);
  if (!(await panel.isVisible())) {
    await grid.sideButton('Columns').click();
  }
  await expect(panel).toBeVisible();
  return panel;
}

/** AG Grid's own drag and drop follows the mouse, so it needs real moves between the press and the release. */
export async function dragTo(page: Page, source: Locator, target: Locator): Promise<void> {
  const from = await source.boundingBox();
  const to = await target.boundingBox();
  if (!from || !to) {
    throw new Error('Cannot drag: the source or the drop zone is not on screen.');
  }
  await page.mouse.move(from.x + from.width / 2, from.y + from.height / 2);
  await page.mouse.down();
  await page.mouse.move(from.x + from.width / 2 + 10, from.y + from.height / 2 + 10, { steps: 5 });
  await page.mouse.move(to.x + to.width / 2, to.y + to.height / 2, { steps: 15 });
  await page.mouse.up();
}

/** Drags a column from the Columns panel's list into the Values zone (it starts with `sum`). */
export async function addValueColumn(grid: GridPage, provider: Provider, colId: string, label: string): Promise<RowsCapture> {
  await openColumnsPanel(grid);
  const handle = grid.page.locator(`.ag-column-select-virtual-list-item[aria-label="${label} Column"] .ag-drag-handle`);
  const zone = grid.page.locator(GRID.valueZone);
  return captureRows(grid.page, provider, () => dragTo(grid.page, handle, zone), {
    where: (request) => sent(request).valueCols?.some((value) => value.id === colId) === true,
  });
}

/** Drags a column from the Columns panel's list into the Row Groups zone. */
export async function addGroupColumn(grid: GridPage, provider: Provider, colId: string, label: string, levels: number): Promise<RowsCapture> {
  await openColumnsPanel(grid);
  const handle = grid.page.locator(`.ag-column-select-virtual-list-item[aria-label="${label} Column"] .ag-drag-handle`);
  const zone = grid.page.locator(GRID.rowGroupZone);
  return captureRows(grid.page, provider, () => dragTo(grid.page, handle, zone), {
    where: (request) =>
      (sent(request).rowGroupCols?.length ?? 0) === levels &&
      sent(request).rowGroupCols?.[levels - 1]?.id === colId &&
      (sent(request).groupKeys?.length ?? 0) === 0,
  });
}

/** Changes the aggregation of a value column from its pill's menu. */
export async function setAggregation(grid: GridPage, provider: Provider, colId: string, label: string, func: string): Promise<RowsCapture> {
  await openColumnsPanel(grid);
  const pill = grid.page.locator(`${GRID.valueZone} ${GRID.dropPillText}`, { hasText: `(${label})` });
  await pill.click();
  return captureRows(grid.page, provider, () => grid.page.getByRole('option', { name: func, exact: true }).click(), {
    where: (request) => sent(request).valueCols?.some((value) => value.id === colId && value.aggFunc === func) === true && (sent(request).groupKeys?.length ?? 0) === 0,
  });
}

/** Opens the group row at `rowIndex`; returns the answer for its children (`depth` is the number of group keys that request carries). */
export async function openGroupAt(grid: GridPage, provider: Provider, rowIndex: number, depth: number): Promise<RowsCapture> {
  const expander = grid.rowAt(rowIndex).locator(GRID.contractedGroup);
  return captureRows(grid.page, provider, () => expander.click(), {
    where: (request) => (sent(request).groupKeys?.length ?? 0) === depth,
  });
}

/** The assertion every scenario ends on: the rows the grid received equal the baseline's answer to the same request. */
export async function expectMatchesBaseline(request: APIRequestContext, scenario: string, capture: RowsCapture): Promise<RowsCapture> {
  expect(capture.status, `${scenario}: ${capture.provider} answered HTTP ${capture.status} ${JSON.stringify(capture.error ?? '')}`).toBe(200);
  const baseline = await fetchBaselineFor(request, capture);
  expect(baseline.status, `${scenario}: ${BASELINE} answered HTTP ${baseline.status}`).toBe(200);
  expectSameRows({ scenario, provider: capture.provider, baseline, actual: capture });
  return baseline;
}

/** A few cells the grid shows must be the values the API sent for those rows (the grid draws only the rows in view). */
export async function expectVisibleCells(
  grid: GridPage,
  capture: RowsCapture,
  options: { rows?: number; offset?: number; columns?: string[] } = {},
): Promise<void> {
  const { rows = 3, offset = 0, columns = ['id', 'customer'] } = options;
  for (let index = 0; index < Math.min(rows, capture.rows.length); index += 1) {
    // While the grid swaps blocks, the old and the new row can both carry the same index for a moment; wait for one.
    await expect(grid.rowAt(offset + index), `${capture.provider}, row ${offset + index} is drawn once`).toHaveCount(1);
    for (const column of columns) {
      const value = capture.rows[index][column];
      await expect(grid.cell(offset + index, column), `${capture.provider}, row ${offset + index}, column ${column}`).toHaveText(
        value === null || value === undefined ? '' : String(value),
      );
    }
  }
}

/** The highest row index the grid has drawn (it draws only the rows in view, so this is the bottom of the viewport). */
export async function lastDrawnRowIndex(grid: GridPage): Promise<number> {
  const indexes = await grid.rows().evaluateAll((rows) => rows.map((row) => Number(row.getAttribute('row-index'))));
  return indexes.length === 0 ? -1 : Math.max(...indexes);
}

/** Scrolls to the end repeatedly until the grid has drawn a row at `index` or later (a provider without a total grows the list block by block). */
export async function scrollPast(grid: GridPage, index: number, timeout = 90_000): Promise<void> {
  await expect
    .poll(
      async () => {
        await grid.scrollBodyToEnd();
        return lastDrawnRowIndex(grid);
      },
      { timeout, intervals: [250, 500, 1000] },
    )
    .toBeGreaterThan(index);
}
