import type { Locator, Page } from '@playwright/test';
import { PROVIDERS, type Provider } from '../../src/api';

/**
 * Every AG Grid selector the specs use, in one place. Page chrome (the demo's own markup) is found by role and accessible
 * name; the grid's DOM has no test ids, so its class names and attributes are listed here and nowhere else. If an AG Grid
 * upgrade renames one, this is the only file to change.
 */
export const GRID = {
  /** The grid's outermost element. */
  root: '.ag-root-wrapper',
  /** Body rows only (header rows have the class ag-header-row). Virtualised: only the rows in view exist. */
  row: '.ag-row',
  /** A row group; it carries `ag-row-group-contracted` or `ag-row-group-expanded`. */
  groupRow: '.ag-row-group',
  expander: '.ag-group-contracted, .ag-group-expanded',
  contractedExpander: '.ag-group-contracted',
  /** The grouping column AG Grid adds in front of the data columns. */
  autoGroupColId: 'ag-Grid-AutoColumn',
  /** The element that scrolls the body vertically. */
  verticalScroller: '.ag-body-vertical-scroll-viewport',
  headerLabel: '.ag-header-cell-label',
  filterButton: '.ag-header-cell-filter-button',
  menuButton: '.ag-header-cell-menu-button',
  /** The overlay shown in evaluation mode; it is in the DOM from the start and hides itself after a few seconds. */
  watermark: '.ag-watermark',
  /** Numbered pager (rendered but hidden while pagination is off). */
  pager: '.ag-paging-panel',
  pagerRows: '.ag-paging-row-summary-panel',
  pagerPage: '.ag-paging-page-summary-panel',
  popup: '.ag-popup',
  filterBody: '.ag-filter',
  filterFrom: 'input[aria-label="Filter Value"]',
  filterTo: 'input[aria-label="Filter to Value"]',
  operatorPicker: '[aria-label="Filtering operator"]',
  setFilterItem: '.ag-set-filter-item',
  menuOption: '.ag-menu-option',
  sideButton: '.ag-side-button',
  columnsPanel: '.ag-column-panel',
  /** The horizontal "row groups" bar above the grid (the vertical one lives in the Columns panel). */
  rowGroupBar: '.ag-column-drop-horizontal-rowgroup',
  rowGroupPill: '.ag-column-drop-cell',
  /** The pivot drop zone exists in the DOM but stays hidden (`ag-hidden`) because the page suppresses pivoting. */
  visiblePivotZone: '[class*="column-drop"][class*="pivot"]:not(.ag-hidden)',
} as const;

export const PAGE_SIZE = 100;

export function providerLabel(provider: Provider): string {
  const entry = PROVIDERS.find((item) => item.id === provider);
  if (!entry) {
    throw new Error(`Unknown provider id "${provider}"`);
  }
  return entry.label;
}

/** The Enterprise page (`/`) as the specs drive it. */
export class GridPage {
  constructor(readonly page: Page, readonly path = '/') {}

  /** Opens the page and waits until the API health check has answered (the switch may change provider after it). */
  async open(): Promise<void> {
    await this.page.goto(this.path);
    await this.page.getByText('API: up').waitFor();
  }

  // Page chrome: roles and accessible names.
  providerSwitch(): Locator {
    return this.page.getByRole('group', { name: 'Database provider' });
  }

  providerButton(provider: Provider): Locator {
    return this.providerSwitch().getByRole('button', { name: providerLabel(provider), exact: true });
  }

  async visibleProviderLabels(): Promise<string[]> {
    return this.providerSwitch().getByRole('button').allInnerTexts();
  }

  exportButton(): Locator {
    return this.page.getByRole('button', { name: /^Export CSV|^Exporting/ });
  }

  pagesButton(): Locator {
    return this.page.getByRole('button', { name: 'Pages', exact: true });
  }

  queryPanelButton(): Locator {
    return this.page.getByRole('button', { name: 'Query panel', exact: true });
  }

  queryPanel(): Locator {
    return this.page.getByRole('complementary', { name: 'Compiled query' });
  }

  errorBanner(): Locator {
    return this.page.getByRole('alert');
  }

  dismissError(): Locator {
    return this.page.getByRole('button', { name: 'Dismiss the error' });
  }

  // The grid.
  headerCell(colId: string): Locator {
    return this.page.locator(`.ag-header-cell[col-id="${colId}"]`);
  }

  /** Click the header text: sorts ascending, then descending, then off. Shift keeps the other sorts (multi-sort). */
  async clickHeader(colId: string, options: { shift?: boolean } = {}): Promise<void> {
    await this.headerCell(colId).locator(GRID.headerLabel).click({ modifiers: options.shift ? ['Shift'] : [] });
  }

  /** `ascending`, `descending` or `none`, from the header's aria-sort. */
  async sortOf(colId: string): Promise<string> {
    return (await this.headerCell(colId).getAttribute('aria-sort')) ?? 'none';
  }

  /** Opens the column's filter from the header button; the popup is `GRID.popup`. */
  async openFilter(colId: string): Promise<Locator> {
    await this.headerCell(colId).locator(GRID.filterButton).click();
    const popup = this.page.locator(GRID.popup).locator(GRID.filterBody).first();
    await popup.waitFor();
    return popup;
  }

  /** Opens the column menu (the header's three-dot button is only drawn on hover, hence the forced click). */
  async openColumnMenu(colId: string): Promise<void> {
    await this.headerCell(colId).locator(GRID.menuButton).click({ force: true });
  }

  menuOption(text: string | RegExp): Locator {
    return this.page.locator(GRID.menuOption, { hasText: text });
  }

  /** Body rows currently in the DOM (never the total: the grid draws only the rows in view). */
  rows(): Locator {
    return this.page.locator(GRID.row);
  }

  rowAt(index: number): Locator {
    return this.page.locator(`${GRID.row}[row-index="${index}"]`);
  }

  cell(rowIndex: number, colId: string): Locator {
    return this.rowAt(rowIndex).locator(`[col-id="${colId}"]`);
  }

  groupRows(): Locator {
    return this.page.locator(GRID.groupRow);
  }

  async scrollBodyTo(top: number): Promise<void> {
    await this.page.locator(GRID.verticalScroller).evaluate((element, y) => {
      element.scrollTop = y;
    }, top);
  }

  async scrollBodyToEnd(): Promise<void> {
    await this.page.locator(GRID.verticalScroller).evaluate((element) => {
      element.scrollTop = element.scrollHeight;
    });
  }

  watermark(): Locator {
    return this.page.locator(GRID.watermark);
  }

  pager(): Locator {
    return this.page.locator(GRID.pager);
  }

  pagerButton(name: 'First Page' | 'Previous Page' | 'Next Page' | 'Last Page'): Locator {
    return this.pager().getByRole('button', { name });
  }

  sideButton(text: 'Columns' | 'Filters'): Locator {
    return this.page.locator(GRID.sideButton, { hasText: text });
  }
}
