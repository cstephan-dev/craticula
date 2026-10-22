import { readFile } from 'node:fs/promises';
import { expect, test, type Download, type Page } from '@playwright/test';
import type { Provider } from '../src/api';
import { GridPage } from './pages/grid';
import { BASELINE, OTHER_PROVIDERS, apiUrl, skipUnlessServed } from './support/providers';
import { captureRows } from './support/rows';
import { firstCsvDifference, parseCsv, type CsvTable } from './support/csv';
import { sent, startOn, textFilter } from './support/actions';

const EVERY_PROVIDER = [BASELINE, ...OTHER_PROVIDERS];
const TOTAL = 20_000;

interface Exported {
  download: Download;
  table: CsvTable;
  /** The body the page posted to the export route. */
  body: Record<string, unknown>;
}

/** Clicks Export CSV, waits for the download and the request that caused it, and reads the file. */
async function exportCsv(grid: GridPage, provider: Provider): Promise<Exported> {
  const posted = grid.page.waitForRequest((call) => call.method() === 'POST' && new URL(call.url()).pathname === `/api/${provider}/export.csv`);
  const downloading = grid.page.waitForEvent('download');
  await grid.exportButton().click();
  const [download, call] = await Promise.all([downloading, posted]);
  expect(download.suggestedFilename()).toBe(`orders-${provider}.csv`);
  const path = await download.path();
  const text = await readFile(path, 'utf8');
  return { download, table: parseCsv(text), body: (call.postDataJSON() ?? {}) as Record<string, unknown> };
}

/** What the baseline exports for the request the page posted. */
async function baselineExport(page: Page, body: Record<string, unknown>): Promise<CsvTable> {
  const response = await page.request.post(`${apiUrl()}/api/${BASELINE}/export.csv`, { data: body });
  expect(response.status()).toBe(200);
  return parseCsv(await response.text());
}

async function expectSameExport(page: Page, scenario: string, provider: Provider, actual: Exported, rows?: number): Promise<void> {
  const baseline = await baselineExport(page, actual.body);
  expect(actual.table[0], `${scenario}: header of ${provider}`).toEqual(baseline[0]);
  if (rows !== undefined) {
    expect(actual.table.length - 1, `${scenario}: data rows of ${provider}`).toBe(rows);
  }
  const difference = firstCsvDifference(baseline, actual.table);
  expect(difference, `Scenario "${scenario}": the CSV of ${provider} differs from ${BASELINE}: ${difference}`).toBeNull();
}

test.describe('CSV export', () => {
  for (const provider of EVERY_PROVIDER) {
    test(`${provider}: export with a filter and a sort equals ${BASELINE}'s file`, async ({ page }) => {
      skipUnlessServed(provider);
      test.setTimeout(120_000);
      const grid = new GridPage(page);
      await startOn(grid, provider);
      const filtered = await textFilter(grid, provider, 'customer', 'GmbH');
      const sorted = await captureRows(page, provider, () => grid.clickHeader('amount'), { where: (r) => (sent(r).sortModel?.length ?? 0) === 1 });

      const exported = await exportCsv(grid, provider);
      expect(exported.body.sortModel).toEqual([{ colId: 'amount', sort: 'asc' }]);
      expect(filtered.lastRow).toBeGreaterThan(0);
      await expectSameExport(page, 'export with a filter and a sort', provider, exported, sorted.lastRow ?? filtered.lastRow ?? undefined);
      // Every exported row matches the filter, and the order is the sort's.
      const customers = exported.table.slice(1).map((row) => row[exported.table[0]?.indexOf('Customer') ?? 0]);
      expect(customers.every((name) => name?.toLowerCase().includes('gmbh'))).toBe(true);
      // Ascending, with the rows that have no amount (an empty cell) last.
      const cells = exported.table.slice(1).map((row) => row[exported.table[0]?.indexOf('Amount') ?? 0] ?? '');
      const firstEmpty = cells.indexOf('');
      const filled = (firstEmpty === -1 ? cells : cells.slice(0, firstEmpty)).map(Number);
      expect(filled).toEqual([...filled].sort((a, b) => a - b));
      if (firstEmpty !== -1) {
        expect(cells.slice(firstEmpty).every((cell) => cell === '')).toBe(true);
      }
      await expect(grid.errorBanner()).toHaveCount(0);
      await expect(grid.exportButton()).toHaveText('Export CSV');
    });

    test(`${provider}: the whole table exports as ${TOTAL.toLocaleString('en-US')} rows and neutralises a formula cell`, async ({ page }) => {
      skipUnlessServed(provider);
      test.setTimeout(180_000);
      const grid = new GridPage(page);
      await startOn(grid, provider);
      const exported = await exportCsv(grid, provider);
      await expectSameExport(page, 'unfiltered export', provider, exported, TOTAL);
      const cells = exported.table.flat();
      // A cell that starts with = is written with a leading apostrophe so a spreadsheet does not run it.
      expect(cells).toContain("'=SUM(A1)");
      expect(cells).not.toContain('=SUM(A1)');
      // A name with a line break inside quotes stays one cell.
      expect(cells.some((cell) => cell.includes('\n') && cell.startsWith('Line'))).toBe(true);
    });

    test(`${provider}: export after a multi-sort keeps both sorts`, async ({ page }) => {
      skipUnlessServed(provider);
      test.setTimeout(180_000);
      const grid = new GridPage(page);
      await startOn(grid, provider);
      await captureRows(page, provider, () => grid.clickHeader('country'), { where: (r) => (sent(r).sortModel?.length ?? 0) === 1 });
      await captureRows(page, provider, () => grid.clickHeader('amount', { shift: true }), { where: (r) => (sent(r).sortModel?.length ?? 0) === 2 });
      const exported = await exportCsv(grid, provider);
      expect((exported.body.sortModel as Array<{ colId: string }>).map((item) => item.colId)).toEqual(['country', 'amount']);
      await expectSameExport(page, 'export after a multi-sort', provider, exported, TOTAL);
    });
  }
});
