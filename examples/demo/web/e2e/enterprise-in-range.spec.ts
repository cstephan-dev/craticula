import { expect, test, type APIRequestContext } from '@playwright/test';
import { GRID, GridPage } from './pages/grid';
import { BASELINE, apiUrl, skipUnlessServed } from './support/providers';
import { captureRows } from './support/rows';
import { closePopup, expectMatchesBaseline, rangeFilter, sent, startOn } from './support/actions';

/**
 * A count the page's "In range" translation cannot influence: the same bounds written as two plain conditions joined by AND
 * (greater than the lower bound, less than the upper one), sent straight to the API.
 */
async function independentCount(request: APIRequestContext, provider: string, colId: string, filterType: 'number' | 'date', from: string, to: string): Promise<number | null> {
  const condition = (type: string, value: string) =>
    filterType === 'number' ? { filterType, type, filter: Number(value) } : { filterType, type, dateFrom: `${value} 00:00:00` };
  const body = {
    startRow: 0,
    endRow: 1,
    rowGroupCols: [],
    valueCols: [],
    pivotCols: [],
    pivotMode: false,
    groupKeys: [],
    sortModel: [],
    filterModel: { [colId]: { filterType, operator: 'AND', conditions: [condition('greaterThan', from), condition('lessThan', to)] } },
  };
  const response = await request.post(`${apiUrl()}/api/${provider}/rows`, { data: body });
  expect(response.status()).toBe(200);
  return ((await response.json()) as { lastRow: number | null }).lastRow;
}

test.describe('In range filter', () => {
  for (const provider of [BASELINE, 'mongo', 'sqlite'] as const) {
    test(`${provider}: Amount between 100 and 500 applies exclusive bounds`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);
      const ranged = await rangeFilter(grid, provider, 'amount', 'number', '100', '500');
      expect(ranged.status).toBe(200);
      expect(ranged.rows.length).toBeGreaterThan(0);
      for (const row of ranged.rows) {
        expect(row.amount as number).toBeGreaterThan(100);
        expect(row.amount as number).toBeLessThan(500);
      }
      const count = await independentCount(request, provider, 'amount', 'number', '100', '500');
      expect(ranged.lastRow).toBe(count);
      await expectMatchesBaseline(request, 'Amount in range 100 to 500', ranged);
      await expect(grid.errorBanner()).toHaveCount(0);
    });

    test(`${provider}: Ordered at between two dates gives the count of two plain conditions`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);
      const ranged = await rangeFilter(grid, provider, 'orderedAt', 'date', '2025-03-01', '2025-06-01');
      expect(ranged.status).toBe(200);
      expect(ranged.lastRow).toBeGreaterThan(0);
      const count = await independentCount(request, provider, 'orderedAt', 'date', '2025-03-01', '2025-06-01');
      expect(ranged.lastRow).toBe(count);
      await expectMatchesBaseline(request, 'Ordered at in range', ranged);
      await expect(grid.errorBanner()).toHaveCount(0);
    });
  }

  test('a range joined to another condition by OR shows the unsupported-filter message', async ({ page }) => {
    const grid = new GridPage(page);
    await startOn(grid, BASELINE);
    const rowsCalls: string[] = [];
    page.on('request', (call) => {
      if (call.method() === 'POST' && call.url().includes(`/api/${BASELINE}/rows`)) {
        rowsCalls.push(call.postData() ?? '');
      }
    });
    const popup = await grid.openFilter('amount');
    await popup.locator(GRID.operatorPicker).first().click();
    await page.getByRole('option', { name: 'Between', exact: true }).click();
    await popup.locator(GRID.filterRangeFrom).fill('100');
    await popup.locator(GRID.filterTo).first().fill('500');
    await popup.getByRole('radio', { name: 'OR' }).check();
    // The second condition: Equals 5 (Equals is the default).
    await popup.locator(GRID.filterFrom).nth(0).fill('5');
    await expect(grid.errorBanner()).toContainText('unsupported-filter');
    await expect(grid.errorBanner()).toContainText(/OR/);
    // The page refuses before it asks the API: no request, from the first fill on, carries an OR model.
    expect(rowsCalls.filter((body) => /"operator"\s*:\s*"OR"/.test(body))).toHaveLength(0);
    await closePopup(page);
  });

  test('narrowing the upper bound of a range keeps the grid answering', async ({ page, request }) => {
    const grid = new GridPage(page);
    await startOn(grid, BASELINE);
    const first = await rangeFilter(grid, BASELINE, 'amount', 'number', '100', '500');
    // Narrow the range: change the upper bound, the page sends a new translated request.
    const popup = await grid.openFilter('amount');
    const narrower = await captureRows(page, BASELINE, () => popup.locator(GRID.filterTo).first().fill('300'), {
      where: (r) => sent(r).customFilters !== undefined && JSON.stringify(sent(r).customFilters).includes('"300"'),
    });
    expect(narrower.lastRow).toBeLessThan(first.lastRow ?? 0);
    expect(narrower.lastRow).toBe(await independentCount(request, BASELINE, 'amount', 'number', '100', '300'));
    await closePopup(page);
  });
});
