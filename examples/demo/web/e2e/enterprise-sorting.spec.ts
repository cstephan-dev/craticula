import { expect, test } from '@playwright/test';
import { GridPage } from './pages/grid';
import { BASELINE, OTHER_PROVIDERS, skipUnlessServed } from './support/providers';
import { captureRows } from './support/rows';
import { expectMatchesBaseline, expectVisibleCells, lastDrawnRowIndex, scrollPast, sent, startOn } from './support/actions';

const EVERY_PROVIDER = [BASELINE, ...OTHER_PROVIDERS];

test.describe('sorting', () => {
  for (const provider of EVERY_PROVIDER) {
    test(`${provider}: scrolling to the end of the table sorted by Id loads past 10,000 rows`, async ({ page }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);
      const sorted = await captureRows(page, provider, () => grid.clickHeader('id'), { where: (r) => (sent(r).sortModel?.length ?? 0) === 1 });
      expect(sorted.rows[0]?.id).toBe(1);
      await expect.poll(() => grid.sortOf('id')).toBe('ascending');

      // A provider that counts jumps to the last row; one that cannot count above 10,000 grows block by block until it is reached.
      await scrollPast(grid, 10_000);
      await expect(grid.errorBanner()).toHaveCount(0);
      expect(await lastDrawnRowIndex(grid)).toBeGreaterThan(10_000);
      // The rows drawn at the bottom are the rows the baseline holds for that block.
      const bottom = await grid.rows().last().getAttribute('row-index');
      await expect(grid.cell(Number(bottom), 'id')).toHaveText(String(Number(bottom) + 1));
    });

    test(`${provider}: shift-click multi-sort by Id then Amount, and by Amount, Id, Country`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);

      await captureRows(page, provider, () => grid.clickHeader('id'), { where: (r) => (sent(r).sortModel?.length ?? 0) === 1 });
      const two = await captureRows(page, provider, () => grid.clickHeader('amount', { shift: true }), { where: (r) => (sent(r).sortModel?.length ?? 0) === 2 });
      expect(sent(two.request).sortModel?.map((item) => item.colId)).toEqual(['id', 'amount']);
      await expectMatchesBaseline(request, 'multi-sort Id then Amount', two);
      await expectVisibleCells(grid, two);
      await expect(grid.errorBanner()).toHaveCount(0);

      // A fresh page, then sort by Amount, Id, Country in that order.
      await startOn(grid, provider);
      await captureRows(page, provider, () => grid.clickHeader('amount'), { where: (r) => (sent(r).sortModel?.length ?? 0) === 1 });
      await captureRows(page, provider, () => grid.clickHeader('id', { shift: true }), { where: (r) => (sent(r).sortModel?.length ?? 0) === 2 });
      const three = await captureRows(page, provider, () => grid.clickHeader('country', { shift: true }), { where: (r) => (sent(r).sortModel?.length ?? 0) === 3 });
      expect(sent(three.request).sortModel?.map((item) => item.colId)).toEqual(['amount', 'id', 'country']);
      await expectMatchesBaseline(request, 'multi-sort Amount, Id, Country', three);
      await expectVisibleCells(grid, three);
      await expect(grid.errorBanner()).toHaveCount(0);
    });
  }
});
