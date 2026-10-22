import { expect, test } from '@playwright/test';
import { GridPage } from './pages/grid';
import { captureRows, fetchBaselineFor } from './support/rows';
import { expectSameRows } from './support/compare';
import { BASELINE, skipUnlessServed } from './support/providers';

// The smallest check that the harness drives the demo: the page loads, the API is up, the first block of rows arrives, and
// each served provider answers the same first block as the baseline.
test('the page loads against a running API', async ({ page }) => {
  const grid = new GridPage(page);
  const first = await captureRows(page, BASELINE, () => grid.open());
  expect(first.status).toBe(200);
  expect(first.lastRow).toBe(20_000);
  await expect(grid.providerButton(BASELINE)).toHaveAttribute('aria-pressed', 'true');
  await expect(grid.cell(0, 'id')).toHaveText('1');
  await expect(grid.watermark()).toHaveCount(1);
  await expect(page.getByText('API: up')).toBeVisible();
});

for (const provider of (['mongo', 'ef', 'sqlite', 'ef-sqlite', 'sqlserver', 'ef-sqlserver', 'mysql', 'opensearch', 'elasticsearch'] as const)) {
  test(`${provider} shows the same first block as ${BASELINE}`, async ({ page, request }) => {
    skipUnlessServed(provider);
    const grid = new GridPage(page);
    await grid.open();
    const actual = await captureRows(page, provider, () => grid.providerButton(provider).click());
    expect(actual.status).toBe(200);
    const baseline = await fetchBaselineFor(request, actual);
    expectSameRows({ scenario: 'first block after switching provider', provider, baseline, actual });
  });
}
