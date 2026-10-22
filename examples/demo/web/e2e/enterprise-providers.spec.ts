import { expect, test } from '@playwright/test';
import { GRID, GridPage } from './pages/grid';
import { BASELINE, OTHER_PROVIDERS, apiUrl, skipUnlessServed } from './support/providers';
import { captureRows } from './support/rows';
import {
  expectMatchesBaseline,
  expectVisibleCells,
  openSetFilter,
  sent,
  setFilterTo,
  startOn,
  textFilter,
  valueFilter,
} from './support/actions';

const EVERY_PROVIDER = [BASELINE, ...OTHER_PROVIDERS];

test.describe('provider switch', () => {
  for (const provider of OTHER_PROVIDERS) {
    test(`${provider} answers like ${BASELINE} to a Customer filter and an Amount sort`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, BASELINE);
      const filtered = await textFilter(grid, BASELINE, 'customer', 'GmbH');
      expect(filtered.rows.length).toBeGreaterThan(0);
      const sorted = await captureRows(page, BASELINE, () => grid.clickHeader('amount'), { where: (r) => (sent(r).sortModel?.length ?? 0) === 1 });
      expect(sorted.rows.length).toBeGreaterThan(0);

      // The filter and the sort stay on screen across the switch: the new provider is asked the same question.
      const switched = await captureRows(page, provider, () => grid.providerButton(provider).click());
      expect(sent(switched.request).sortModel).toEqual(sent(sorted.request).sortModel);
      expect(sent(switched.request).filterModel).toEqual(sent(sorted.request).filterModel);
      await expectMatchesBaseline(request, 'provider switch with a filter and a sort', switched);
      await expect(grid.providerButton(provider)).toHaveAttribute('aria-pressed', 'true');
      await expectVisibleCells(grid, switched, { columns: ['id', 'customer', 'country'] });
      await expect(grid.errorBanner()).toHaveCount(0);
    });
  }
});

test.describe('set filter', () => {
  for (const provider of EVERY_PROVIDER) {
    test(`${provider}: the Country list comes from the API and narrows the rows`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);

      const popup = await openSetFilter(grid, provider, 'country');
      // The filter lists the values the API sent for the column.
      const shown = (await popup.locator(GRID.setFilterItem).allInnerTexts()).map((text) => text.trim()).filter((text) => text !== '(Select All)');
      const answer = await request.post(`${apiUrl()}/api/${BASELINE}/values`, { data: { setValuesRequest: { colId: 'country', filterModel: {} } } });
      const wanted = ((await answer.json()) as { values: unknown[] }).values.filter((value): value is string => typeof value === 'string');
      // The list is drawn in part (it scrolls), so the items on screen must be among the values the baseline holds.
      expect(shown.length).toBeGreaterThan(2);
      for (const value of shown) {
        expect(wanted).toContain(value);
      }

      const chosen = [...shown].sort().slice(0, 2);
      const narrowed = await setFilterTo(grid, provider, 'country', chosen, popup);
      expect(narrowed.rows.length).toBeGreaterThan(0);
      for (const row of narrowed.rows) {
        expect(chosen).toContain(row.country);
      }
      await expectMatchesBaseline(request, `set filter on Country: ${chosen.join(', ')}`, narrowed);
      await expectVisibleCells(grid, narrowed, { columns: ['id', 'country'] });
      await expect(grid.errorBanner()).toHaveCount(0);
    });

    test(`${provider}: clearing every Country value shows an empty grid and no error`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);
      const cleared = await setFilterTo(grid, provider, 'country', []);
      expect(cleared.status).toBe(200);
      expect(cleared.rows).toHaveLength(0);
      expect(cleared.lastRow).toBe(0);
      await expectMatchesBaseline(request, 'set filter with nothing selected', cleared);
      await expect(grid.rows()).toHaveCount(0);
      await expect(grid.errorBanner()).toHaveCount(0);
    });
  }
});

test.describe('filters by column type', () => {
  for (const provider of EVERY_PROVIDER) {
    test(`${provider}: number, date and boolean filters give the counts ${BASELINE} gives`, async ({ page, request }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);

      const quantity = await valueFilter(grid, provider, 'quantity', 'Greater than', '8');
      expect(quantity.rows.length).toBeGreaterThan(0);
      expect(quantity.rows.every((row) => (row.quantity as number) > 8)).toBe(true);
      await expectMatchesBaseline(request, 'Quantity greater than 8', quantity);
      await expectVisibleCells(grid, quantity, { columns: ['id', 'quantity'] });

      const shipped = await valueFilter(grid, provider, 'shippedOn', 'After', '2025-03-01');
      expect(shipped.rows.length).toBeGreaterThan(0);
      await expectMatchesBaseline(request, 'Shipped on after 2025-03-01 with Quantity greater than 8', shipped);

      const ordered = await valueFilter(grid, provider, 'orderedAt', 'Before', '2025-06-01');
      expect(ordered.rows.length).toBeGreaterThan(0);
      await expectMatchesBaseline(request, 'Ordered at before 2025-06-01 with the earlier filters', ordered);
      expect(ordered.lastRow === null || ordered.lastRow <= (shipped.lastRow ?? Infinity)).toBe(true);

      const priority = await setFilterTo(grid, provider, 'isPriority', ['true']);
      expect(priority.rows.length).toBeGreaterThan(0);
      expect(priority.rows.every((row) => row.isPriority === true)).toBe(true);
      await expectMatchesBaseline(request, 'Priority is true with the earlier filters', priority);
      await expect(grid.errorBanner()).toHaveCount(0);
    });
  }
});

test('the Enterprise page shows the evaluation watermark', async ({ page }) => {
  const grid = new GridPage(page);
  await startOn(grid, BASELINE);
  await expect(grid.watermark()).toBeAttached();
  await expect(page.getByText('evaluation mode')).toBeVisible();
});
