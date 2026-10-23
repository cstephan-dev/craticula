import { expect, test } from '@playwright/test';
import type { Provider } from '../src/api';
import { GridPage } from './pages/grid';
import { skipUnlessServed } from './support/providers';
import { startOn, textFilter } from './support/actions';

const NAME = 'Müller, Söhne & Co';

type Search = 'söhne' | 'SÖHNE' | 'sohne';

/**
 * Whether a "contains" filter finds the customer `Müller, Söhne & Co`, per engine and typed text. Measured on all eleven
 * providers, in two batches (SQL Server 2022, MySQL 8.4, OpenSearch 2.19, Elasticsearch 8.17 included); the results agree with the demo README.
 */
const FINDS: Partial<Record<Provider, Record<Search, boolean>>> = {
  postgres: { 'söhne': true, 'SÖHNE': true, sohne: false },
  ef: { 'söhne': true, 'SÖHNE': true, sohne: false },
  mongo: { 'söhne': true, 'SÖHNE': true, sohne: false },
  // SQLite folds ASCII letters only.
  sqlite: { 'söhne': true, 'SÖHNE': false, sohne: false },
  'ef-sqlite': { 'söhne': true, 'SÖHNE': false, sohne: false },
  // A case-insensitive collation that folds beyond ASCII but is accent sensitive.
  sqlserver: { 'söhne': true, 'SÖHNE': true, sohne: false },
  'ef-sqlserver': { 'söhne': true, 'SÖHNE': true, sohne: false },
  // The stock MySQL collation ignores accents.
  mysql: { 'söhne': true, 'SÖHNE': true, sohne: true },
  'ef-mysql': { 'söhne': true, 'SÖHNE': true, sohne: true },
  // Search engines fold ASCII letters only and are accent sensitive.
  opensearch: { 'söhne': true, 'SÖHNE': false, sohne: false },
  elasticsearch: { 'söhne': true, 'SÖHNE': false, sohne: false },
};

test.describe('text matching and collation', () => {
  for (const [provider, expected] of Object.entries(FINDS) as Array<[Provider, Record<Search, boolean>]>) {
    test(`${provider}: Customer contains söhne, SÖHNE and sohne`, async ({ page }) => {
      skipUnlessServed(provider);
      const grid = new GridPage(page);
      await startOn(grid, provider);
      for (const search of Object.keys(expected) as Search[]) {
        const capture = await textFilter(grid, provider, 'customer', search);
        expect(capture.status, `${provider}, "${search}"`).toBe(200);
        const found = capture.rows.some((row) => row.customer === NAME);
        expect(found, `${provider} contains "${search}" ${expected[search] ? 'finds' : 'misses'} ${NAME}`).toBe(expected[search]);
        // The grid swaps the old rows for the new ones, so wait for exactly the rows of this answer before reading a cell.
        await expect(grid.rows()).toHaveCount(capture.rows.length);
        if (found) {
          await expect(grid.page.getByRole('gridcell', { name: NAME })).toBeVisible();
        }
        await expect(grid.errorBanner()).toHaveCount(0);
      }
    });
  }
});
