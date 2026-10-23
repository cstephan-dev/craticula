import { expect, test } from '@playwright/test';
import { GRID, GridPage } from './pages/grid';
import { BASELINE, OTHER_PROVIDERS, skipUnlessServed } from './support/providers';
import { SEARCH_ENGINES } from './support/divergences';
import { captureRows } from './support/rows';
import {
  addGroupColumn,
  addValueColumn,
  expectMatchesBaseline,
  openGroupAt,
  sent,
  setAggregation,
  startOn,
} from './support/actions';

const EVERY_PROVIDER = [BASELINE, ...OTHER_PROVIDERS];

test.describe('grouping and aggregation', () => {
  for (const provider of EVERY_PROVIDER) {
    test(`${provider}: group by Country then Category with sum, avg and count, and open a group`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);

      const byCountry = await addGroupColumn(grid, provider, 'country', 'Country', 1);
      await expectMatchesBaseline(request, 'group by Country', byCountry);
      await expect(grid.groupRows().first()).toBeVisible();

      const byCategory = await addGroupColumn(grid, provider, 'category', 'Category', 2);
      await expectMatchesBaseline(request, 'group by Country then Category', byCategory);
      expect(sent(byCategory.request).rowGroupCols?.map((column) => column.id)).toEqual(['country', 'category']);

      const sum = await addValueColumn(grid, provider, 'amount', 'Amount');
      await expectMatchesBaseline(request, 'Amount sum by Country', sum);
      // The API names an aggregate <column>_<function>; the page shows it in the column itself.
      expect(sum.columns).toContain('amount_sum');

      const quantity = await addValueColumn(grid, provider, 'quantity', 'Quantity');
      await expectMatchesBaseline(request, 'Amount sum and Quantity sum by Country', quantity);
      const quantityAvg = await setAggregation(grid, provider, 'quantity', 'Quantity', 'avg');
      await expectMatchesBaseline(request, 'Quantity average by Country', quantityAvg);
      const amountAvg = await setAggregation(grid, provider, 'amount', 'Amount', 'avg');
      await expectMatchesBaseline(request, 'Amount average by Country', amountAvg);
      const amountCount = await setAggregation(grid, provider, 'amount', 'Amount', 'count');
      await expectMatchesBaseline(request, 'Amount count by Country', amountCount);

      // The group rows on screen carry the group keys the API sent.
      const keyOf = (row: Record<string, unknown>): unknown => Object.values(row)[0];
      const firstIndex = amountCount.rows.findIndex((row) => keyOf(row) !== null);
      expect(firstIndex, `${provider}: a Country group with a key`).toBeGreaterThanOrEqual(0);
      const firstRow = amountCount.rows[firstIndex];
      if (firstRow) {
        await expect(grid.page.getByRole('gridcell', { name: String(keyOf(firstRow)), exact: true }).first()).toBeVisible();
      }

      // Open a group: its children are the next group level, and then the orders themselves.
      const children = await openGroupAt(grid, provider, firstIndex, 1);
      await expectMatchesBaseline(request, 'children of the first Country group', children);
      expect(sent(children.request).groupKeys).toHaveLength(1);
      expect(children.rows.length).toBeGreaterThan(0);
      const childIndex = children.rows.findIndex((row) => keyOf(row) !== null);
      expect(childIndex, `${provider}: a Category group with a key`).toBeGreaterThanOrEqual(0);
      const leaves = await openGroupAt(grid, provider, firstIndex + 1 + childIndex, 2);
      await expectMatchesBaseline(request, 'orders of the first Country and Category group', leaves);
      expect(leaves.rows.length).toBeGreaterThan(0);
      expect(leaves.columns).toContain('customer');
      await expect(grid.errorBanner()).toHaveCount(0);
    });
  }

  for (const provider of EVERY_PROVIDER) {
    test(`${provider}: the group with no Country opens like any other`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);

      const byCountry = await addGroupColumn(grid, provider, 'country', 'Country', 1);
      // Wherever the provider lists it (a search engine lists it first, PostgreSQL last).
      const blankIndex = byCountry.rows.findIndex((row) => Object.values(row)[0] === null);
      expect(blankIndex, `${provider}: a Country group with no key`).toBeGreaterThanOrEqual(0);
      await expect(grid.rowAt(blankIndex).getByText('(blank)', { exact: true })).toBeVisible();

      const children = await openGroupAt(grid, provider, blankIndex, 1);
      expect(sent(children.request).groupKeys).toEqual([{ type: 'null' }]);
      await expectMatchesBaseline(request, 'children of the Country group with no key', children);
      expect(children.rows.length).toBeGreaterThan(0);
      await expect(grid.errorBanner()).toHaveCount(0);
    });
  }

  for (const provider of SEARCH_ENGINES) {
    test(`${provider}: ordering the groups by an aggregate is reported as not supported`, async ({ page }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);
      await addGroupColumn(grid, provider, 'country', 'Country', 1);
      await addValueColumn(grid, provider, 'amount', 'Amount');
      const refused = await captureRows(page, provider, () => grid.clickHeader('amount'), {
        where: (request) => (sent(request).sortModel?.length ?? 0) === 1 && (sent(request).groupKeys?.length ?? 0) === 0,
      });
      expect(refused.status).toBe(400);
      expect(refused.error?.kind).toBe('unsupported-shape');
      await expect(grid.errorBanner()).toContainText('Not supported on this provider');
      await expect(grid.page.locator(GRID.root)).toBeVisible();
    });
  }
});
