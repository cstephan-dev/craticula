import { expect, test } from '@playwright/test';
import { GRID, PAGE_SIZE, GridPage } from './pages/grid';
import { BASELINE, OTHER_PROVIDERS, skipUnlessServed } from './support/providers';
import { captureRows } from './support/rows';
import {
  closePopup,
  expectMatchesBaseline,
  expectVisibleCells,
  groupByMenu,
  openColumnsPanel,
  sent,
  startOn,
  textFilter,
} from './support/actions';

const TOTAL = 20_000;
const PAGES = TOTAL / PAGE_SIZE;
const number = (value: number) => value.toLocaleString('en-US');

async function pagesOn(grid: GridPage): Promise<void> {
  await grid.pagesButton().click();
  await expect(grid.pagesButton()).toHaveAttribute('aria-pressed', 'true');
  await expect(grid.pager()).toBeVisible();
}

test.describe('numbered pages', () => {
  test('the Pages button switches between numbered pages and scrolling', async ({ page }) => {
    const grid = new GridPage(page);
    await startOn(grid, BASELINE);
    await expect(grid.pagesButton()).toHaveAttribute('aria-pressed', 'false');
    await expect(grid.pager()).toBeHidden();

    await pagesOn(grid);
    await expect(grid.page.locator(GRID.pagerRows)).toHaveText(`1 to ${PAGE_SIZE} of ${number(TOTAL)}`);

    await grid.pagesButton().click();
    await expect(grid.pagesButton()).toHaveAttribute('aria-pressed', 'false');
    await expect(grid.pager()).toBeHidden();
    // Scrolling is back: a row far down the list is drawn, and the first one no longer is.
    await grid.scrollBodyTo(100_000);
    await expect(grid.rowAt(0)).toHaveCount(0);
    await expect(grid.rows().first()).toBeVisible();
  });

  for (const provider of [BASELINE, ...OTHER_PROVIDERS]) {
    test(`${provider}: ${PAGES} pages of ${PAGE_SIZE}, and a page holds the rows ${BASELINE} holds`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      const first = await startOn(grid, provider);
      await pagesOn(grid);
      // A provider that cannot count above 10,000 rows has no last page to show.
      const counted = first.lastRow !== null;
      if (counted) {
        expect(first.lastRow).toBe(TOTAL);
        await expect(grid.page.locator(GRID.pagerRows)).toHaveText(`1 to ${PAGE_SIZE} of ${number(TOTAL)}`);
        await expect(grid.page.locator(GRID.pagerPage)).toContainText(`of ${PAGES}`);
      }

      const second = await captureRows(page, provider, () => grid.pagerButton('Next Page').click(), { where: (r) => sent(r).startRow === PAGE_SIZE });
      await expectMatchesBaseline(request, 'second page', second);
      expect(second.rows).toHaveLength(PAGE_SIZE);
      await expect(grid.page.locator(GRID.pagerRows)).toContainText(`${PAGE_SIZE + 1} to ${2 * PAGE_SIZE}`);
      await expectVisibleCells(grid, second, { offset: PAGE_SIZE });

      if (counted) {
        const last = await captureRows(page, provider, () => grid.pagerButton('Last Page').click(), { where: (r) => sent(r).startRow === TOTAL - PAGE_SIZE });
        await expectMatchesBaseline(request, 'last page', last);
        await expect(grid.page.locator(GRID.pagerRows)).toHaveText(`${number(TOTAL - PAGE_SIZE + 1)} to ${number(TOTAL)} of ${number(TOTAL)}`);
      }
    });
  }

  test('a filter and then a sort bring a later page back to page 1', async ({ page, request }) => {
    const grid = new GridPage(page);
    await startOn(grid, BASELINE);
    await pagesOn(grid);
    await captureRows(page, BASELINE, () => grid.pagerButton('Next Page').click(), { where: (r) => sent(r).startRow === PAGE_SIZE });
    const third = await captureRows(page, BASELINE, () => grid.pagerButton('Next Page').click(), { where: (r) => sent(r).startRow === 2 * PAGE_SIZE });
    await expect(grid.page.locator(GRID.pagerRows)).toContainText(`${2 * PAGE_SIZE + 1} to ${3 * PAGE_SIZE}`);
    expect(third.rows).toHaveLength(PAGE_SIZE);

    const filtered = await textFilter(grid, BASELINE, 'customer', 'GmbH');
    expect(filtered.lastRow).toBeGreaterThan(PAGE_SIZE);
    expect(sent(filtered.request).startRow).toBe(0);
    await expect(grid.page.locator(GRID.pagerRows)).toHaveText(`1 to ${PAGE_SIZE} of ${number(filtered.lastRow ?? 0)}`);
    await expectVisibleCells(grid, filtered, { offset: 0 });

    // The sort then applies to page 1 of the filtered list.
    const sorted = await captureRows(page, BASELINE, () => grid.clickHeader('amount'), {
      where: (r) => (sent(r).sortModel?.length ?? 0) === 1 && sent(r).startRow === 0,
    });
    await expect(grid.page.locator(GRID.pagerRows)).toHaveText(`1 to ${PAGE_SIZE} of ${number(filtered.lastRow ?? 0)}`);
    await expectMatchesBaseline(request, 'sorted page 1 of the filtered list', sorted);
    expect(sorted.rows.map((row) => row.amount)).toEqual([...sorted.rows.map((row) => row.amount as number)].sort((a, b) => a - b));
  });

  test('grouping and then Pages keeps the groups, the filter and the sort', async ({ page }) => {
    const grid = new GridPage(page);
    await startOn(grid, BASELINE);
    await textFilter(grid, BASELINE, 'customer', 'GmbH');
    // A grouped list can be ordered by its group column or an aggregate, so the sort goes on Country before it becomes the group.
    await captureRows(page, BASELINE, () => grid.clickHeader('country'), { where: (r) => (sent(r).sortModel?.length ?? 0) === 1 });
    const grouped = await groupByMenu(grid, BASELINE, 'country', 'Country', 1);
    expect(grouped.status, JSON.stringify(grouped.error)).toBe(200);
    expect(sent(grouped.request).sortModel).toEqual([{ colId: 'country', sort: 'asc', type: 'default' }]);
    expect(grouped.rows.length).toBeGreaterThan(0);
    await expect(grid.groupRows().first()).toBeVisible();
    await expect.poll(() => grid.sortOf(GRID.autoGroupColId)).toBe('ascending');

    await pagesOn(grid);

    await expect(grid.page.locator(GRID.rowGroupBar).locator(GRID.rowGroupPill)).toHaveText('Country');
    await expect(grid.groupRows().first()).toBeVisible();
    await expect.poll(() => grid.sortOf(GRID.autoGroupColId)).toBe('ascending');
    const popup = await grid.openFilter('customer');
    await expect(popup.locator(GRID.filterFrom).first()).toHaveValue('GmbH');
    await closePopup(page);
  });

  test('the Columns panel offers no Pivot Mode', async ({ page }) => {
    const grid = new GridPage(page);
    await startOn(grid, BASELINE);
    const panel = await openColumnsPanel(grid);
    await expect(panel.getByText('Row Groups', { exact: true })).toBeVisible();
    await expect(panel.getByText('Values', { exact: true })).toBeVisible();
    await expect(page.getByText('Pivot Mode')).toHaveCount(0);
    for (const zone of await page.locator(GRID.pivotZone).all()) {
      await expect(zone).toBeHidden();
    }
  });
});
