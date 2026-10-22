import { expect, test } from '@playwright/test';
import type { Provider } from '../src/api';
import { GridPage } from './pages/grid';
import { BASELINE, OTHER_PROVIDERS, skipUnlessServed } from './support/providers';
import { startOn, textFilter } from './support/actions';

interface Expectation {
  kind: string;
  dialect: string | RegExp;
  /** Text the compiled query must contain (placeholders, the operators of the engine). */
  contains: Array<string | RegExp>;
  /** Text it must not contain. */
  lacks?: Array<string | RegExp>;
  /** A sentence of the note the panel shows above the text for this dialect. */
  note?: string | RegExp;
}

/**
 * What the panel shows after a Customer "contains" filter, per provider. The SQL Server and MySQL rows come from the demo
 * README's description of their compiled text (`@p1` with COLLATE; `?` without), as do the search engines' (JSON).
 */
const EXPECTED: Record<Provider, Expectation> = {
  postgres: { kind: 'sql', dialect: 'postgres', contains: ['$1', 'ILIKE'], lacks: ['COLLATE'] },
  mongo: { kind: 'mongo', dialect: 'mongodb', contains: ['$match'] },
  ef: { kind: 'sql', dialect: 'ef-npgsql', contains: ['SELECT', 'lower('], note: /EF Core inlines parameter values/ },
  sqlite: { kind: 'sql', dialect: 'sqlite', contains: ['?', 'LIKE'], lacks: ['COLLATE'] },
  'ef-sqlite': { kind: 'sql', dialect: 'ef-sqlite', contains: ['SELECT'], note: /EF Core inlines parameter values/ },
  sqlserver: { kind: 'sql', dialect: /sqlserver/i, contains: ['@p1', 'COLLATE'] },
  'ef-sqlserver': { kind: 'sql', dialect: 'ef-sqlserver', contains: ['SELECT'], note: /EF Core inlines parameter values/ },
  mysql: { kind: 'sql', dialect: /mysql/i, contains: ['?'], lacks: ['COLLATE'] },
  opensearch: { kind: 'json', dialect: 'opensearch', contains: ['"query"'], note: /compiled search request/ },
  elasticsearch: { kind: 'json', dialect: 'elasticsearch', contains: ['"query"'], note: /compiled search request/ },
};

test.describe('query panel', () => {
  for (const provider of [BASELINE, ...OTHER_PROVIDERS]) {
    test(`${provider}: the panel shows the compiled query with its parameters and copies it`, async ({ page }) => {
      skipUnlessServed(provider);
      const expected = EXPECTED[provider];
      const grid = new GridPage(page);
      await startOn(grid, provider);
      await textFilter(grid, provider, 'customer', 'GmbH');

      // Open by default: closing removes it, opening again brings it back.
      await expect(grid.queryPanelButton()).toHaveAttribute('aria-pressed', 'true');
      await expect(grid.queryPanel()).toBeVisible();
      await grid.queryPanelButton().click();
      await expect(grid.queryPanelButton()).toHaveAttribute('aria-pressed', 'false');
      await expect(grid.queryPanel()).toHaveCount(0);
      await grid.queryPanelButton().click();
      const panel = grid.queryPanel();
      await expect(panel).toBeVisible();

      await expect(panel.locator('.tag')).toHaveText(expected.kind);
      await expect(panel.getByText(expected.dialect, { exact: typeof expected.dialect === 'string' }).first()).toBeVisible();
      const text = panel.locator('pre').first();
      for (const part of expected.contains) {
        await expect(text).toContainText(part);
      }
      for (const part of expected.lacks ?? []) {
        await expect(text).not.toContainText(part);
      }
      if (expected.note) {
        await expect(panel.locator('.query-note')).toContainText(expected.note);
      } else {
        await expect(panel.locator('.query-note')).toHaveCount(0);
      }
      await expect(panel.getByRole('heading', { name: /^Parameters \(\d+\)$/ })).toBeVisible();

      const shown = (await text.textContent()) ?? '';
      if (expected.kind === 'json') {
        expect(() => JSON.parse(shown)).not.toThrow();
      }
      await panel.getByRole('button', { name: 'Copy query' }).click();
      await expect(panel.getByRole('button', { name: 'Copied' })).toBeVisible();
      expect(await page.evaluate(() => navigator.clipboard.readText())).toBe(shown);
    });
  }
});
