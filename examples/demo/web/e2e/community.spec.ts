import { expect, test } from '@playwright/test';
import type { Provider } from '../src/api';
import { GRID, GridPage } from './pages/grid';
import { BASELINE, OTHER_PROVIDERS, skipUnlessServed } from './support/providers';
import { captureRows, type RowsCapture } from './support/rows';
import { expectMatchesBaseline, expectVisibleCells, rangeFilter, scrollPast, sent, textFilter, valueFilter } from './support/actions';

const COMMUNITY = '/community.html';
const EVERY_PROVIDER = [BASELINE, ...OTHER_PROVIDERS];

async function openCommunity(grid: GridPage, provider: Provider): Promise<RowsCapture> {
  if (provider === BASELINE) {
    return captureRows(grid.page, BASELINE, () => grid.open());
  }
  await grid.open();
  return captureRows(grid.page, provider, () => grid.providerButton(provider).click());
}

test.describe('AG Grid Community page', () => {
  test('the page loads without the Enterprise package, a watermark, a set filter or grouping', async ({ page }) => {
    const requested: string[] = [];
    page.on('request', (call) => requested.push(call.url()));
    const grid = new GridPage(page, COMMUNITY);
    const first = await openCommunity(grid, BASELINE);
    expect(first.lastRow).toBe(20_000);
    await expect(grid.cell(0, 'id')).toHaveText('1');
    await expect(grid.watermark()).toHaveCount(0);
    expect(requested.filter((url) => url.includes('ag-grid-enterprise'))).toEqual([]);

    // No side bar, no row group bar, and the Country filter is a text filter, not a set filter.
    await expect(page.locator(GRID.sideButton)).toHaveCount(0);
    await expect(page.locator(GRID.rowGroupBar)).toHaveCount(0);
    const popup = await grid.openFilter('country');
    await expect(popup.locator(GRID.filterFrom).first()).toBeVisible();
    await expect(popup.locator(GRID.setFilterItem)).toHaveCount(0);
    expect(requested.filter((url) => url.includes('ag-grid-enterprise') || url.includes('/values'))).toEqual([]);
  });

  test('sort, text, number and date filters and scrolling', async ({ page, request }) => {
    const grid = new GridPage(page, COMMUNITY);
    await openCommunity(grid, BASELINE);

    const sorted = await captureRows(page, BASELINE, () => grid.clickHeader('amount'), { where: (r) => (sent(r).sortModel?.length ?? 0) === 1 });
    await expectMatchesBaseline(request, 'Community sort by Amount', sorted);
    await expect.poll(() => grid.sortOf('amount')).toBe('ascending');

    const text = await textFilter(grid, BASELINE, 'customer', 'GmbH');
    expect(text.rows.length).toBeGreaterThan(0);
    await expectMatchesBaseline(request, 'Community text filter', text);
    await expectVisibleCells(grid, text);

    const number = await valueFilter(grid, BASELINE, 'quantity', 'Greater than', '8');
    expect(number.rows.every((row) => (row.quantity as number) > 8)).toBe(true);
    await expectMatchesBaseline(request, 'Community number filter', number);

    const date = await valueFilter(grid, BASELINE, 'shippedOn', 'After', '2025-03-01');
    await expectMatchesBaseline(request, 'Community date filter', date);
    expect(date.lastRow).toBeLessThanOrEqual(number.lastRow ?? Infinity);
    await expect(grid.errorBanner()).toHaveCount(0);
  });

  test('scrolling loads further blocks of the whole table', async ({ page, request }) => {
    const grid = new GridPage(page, COMMUNITY);
    await openCommunity(grid, BASELINE);
    const far = captureRows(page, BASELINE, () => scrollPast(grid, 5_000), { where: (r) => (sent(r).startRow ?? 0) > 5_000 });
    await expectMatchesBaseline(request, 'Community block far down the list', await far);
    await expect(grid.errorBanner()).toHaveCount(0);
  });

  for (const provider of OTHER_PROVIDERS) {
    test(`${provider}: the switch keeps the filter and the sort and answers like ${BASELINE}`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page, COMMUNITY);
      await openCommunity(grid, BASELINE);
      await textFilter(grid, BASELINE, 'customer', 'GmbH');
      const sorted = await captureRows(page, BASELINE, () => grid.clickHeader('amount'), { where: (r) => (sent(r).sortModel?.length ?? 0) === 1 });
      const switched = await captureRows(page, provider, () => grid.providerButton(provider).click());
      expect(sent(switched.request).sortModel).toEqual(sent(sorted.request).sortModel);
      await expectMatchesBaseline(request, 'Community provider switch', switched);
      await expectVisibleCells(grid, switched);
      await expect(grid.errorBanner()).toHaveCount(0);
    });
  }

  for (const provider of EVERY_PROVIDER) {
    test(`${provider}: In range on a number and a date column`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page, COMMUNITY);
      await openCommunity(grid, provider);
      const amount = await rangeFilter(grid, provider, 'amount', 'number', '100', '500');
      expect(amount.rows.length).toBeGreaterThan(0);
      for (const row of amount.rows) {
        expect(row.amount as number).toBeGreaterThan(100);
        expect(row.amount as number).toBeLessThan(500);
      }
      await expectMatchesBaseline(request, 'Community In range on Amount', amount);

      const date = await rangeFilter(grid, provider, 'shippedOn', 'date', '2025-03-01', '2025-06-01');
      expect(date.status).toBe(200);
      await expectMatchesBaseline(request, 'Community In range on Shipped on', date);
      await expect(grid.errorBanner()).toHaveCount(0);
    });
  }

  test('Copy request puts the flat request on the clipboard', async ({ page }) => {
    const grid = new GridPage(page, COMMUNITY);
    await openCommunity(grid, BASELINE);
    await textFilter(grid, BASELINE, 'customer', 'GmbH');
    const sorted = await captureRows(page, BASELINE, () => grid.clickHeader('amount'), { where: (r) => (sent(r).sortModel?.length ?? 0) === 1 });
    // The button copies the request of the latest /query answer, which can arrive after the rows: copy until it is the sorted one.
    await expect
      .poll(async () => {
        await page.getByRole('button', { name: /^Copy request|^Copied/ }).click();
        return page.evaluate(() => navigator.clipboard.readText());
      })
      .toContain('"sortModel": [\n    {');
    const copied = JSON.parse(await page.evaluate(() => navigator.clipboard.readText())) as Record<string, unknown>;
    expect(Object.keys(copied).sort()).toEqual(['endRow', 'filterModel', 'sortModel', 'startRow']);
    expect(copied.startRow).toBe(0);
    expect(copied.sortModel).toEqual(sent(sorted.request).sortModel);
    expect(copied.filterModel).toEqual({ customer: { filterType: 'text', type: 'contains', filter: 'GmbH' } });
  });
});
