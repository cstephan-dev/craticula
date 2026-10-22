import { execFile } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { promisify } from 'node:util';
import { expect, test } from '@playwright/test';
import type { Provider } from '../src/api';
import { GridPage } from './pages/grid';
import { BASELINE, apiUrl, isServed, served } from './support/providers';
import { captureRows, fetchRowsFor } from './support/rows';
import { startOn } from './support/actions';

const run = promisify(execFile);

/** The demo folder, where docker-compose.yml lives. */
const DEMO_DIR = fileURLToPath(new URL('../..', import.meta.url));

interface Engine {
  /** The compose service to stop. */
  service: string;
  /** The compose profile that service belongs to (none for the default two). */
  profile?: string;
}

/** Which container backs which provider. The file-based providers (sqlite, ef-sqlite) have none, so they have no scenario here. */
const BACKING: Partial<Record<Provider, Engine>> = {
  postgres: { service: 'postgres' },
  ef: { service: 'postgres' },
  mongo: { service: 'mongo' },
  sqlserver: { service: 'sqlserver', profile: 'sqlserver' },
  'ef-sqlserver': { service: 'sqlserver', profile: 'sqlserver' },
  mysql: { service: 'mysql', profile: 'mysql' },
  opensearch: { service: 'opensearch', profile: 'opensearch' },
  elasticsearch: { service: 'elasticsearch', profile: 'elasticsearch' },
};

/** Every provider a service backs: they all go down together. */
function providersOf(engine: Engine): Provider[] {
  return (Object.entries(BACKING) as Array<[Provider, Engine]>).filter(([, other]) => other.service === engine.service).map(([id]) => id);
}

async function compose(engine: Engine, ...args: string[]): Promise<void> {
  const profile = engine.profile ? ['--profile', engine.profile] : [];
  await run('docker', ['compose', ...profile, ...args], { cwd: DEMO_DIR, timeout: 300_000 });
}

const command = (engine: Engine, verb: string) => `docker compose ${engine.profile ? `--profile ${engine.profile} ` : ''}${verb} ${engine.service}`;

// Stopping a database breaks every test that reads it, so these run alone (the `destructive` project) and in order.
test.describe.configure({ mode: 'serial' });

test.describe('a database that is down', () => {
  test.skip(process.env.E2E_DESTRUCTIVE !== '1', 'stops database containers with docker compose; set E2E_DESTRUCTIVE=1 to run it');

  let stopped: Engine | null = null;

  /** Starts the container again and waits until it is healthy; the caller's failure message names it when that fails. */
  async function restore(): Promise<void> {
    const engine = stopped;
    if (!engine) {
      return;
    }
    try {
      await compose(engine, 'up', '-d', '--wait', engine.service);
    } catch (error) {
      throw new Error(
        `The container "${engine.service}" was stopped by this test and could not be started again (${error instanceof Error ? error.message : String(error)}). Start it yourself: ${command(engine, 'up -d --wait')}`,
        { cause: error },
      );
    }
    stopped = null;
  }

  test.afterEach(async () => {
    await restore();
  });

  for (const [provider, engine] of Object.entries(BACKING) as Array<[Provider, Engine]>) {
    test(`${provider}: with ${engine.service} stopped the page shows backend-unavailable and the other providers keep working`, async ({ page, request }) => {
      test.skip(!isServed(provider), `${provider} not served by this API`);
      test.setTimeout(420_000);
      const grid = new GridPage(page);
      // The page is loaded and the provider answers while its database is up; then the database is stopped.
      await startOn(grid, provider === BASELINE ? BASELINE : provider);
      await expect(grid.errorBanner()).toHaveCount(0);

      stopped = engine;
      await compose(engine, 'stop', engine.service);
      const what = `with the "${engine.service}" container stopped (${command(engine, 'stop')})`;

      // A new request on the stopped provider: sorting asks the API again. A pooled connection that the server closed during
      // the shutdown can surface as one HTTP 500 (PostgreSQL's "terminating connection" error) before the API answers 503, so
      // the request is repeated; the report notes it when it happened.
      const statuses: number[] = [];
      let failed = await captureRows(page, provider, () => grid.clickHeader('amount'), { timeout: 90_000 });
      statuses.push(failed.status);
      for (let attempt = 0; attempt < 2 && failed.status !== 503; attempt += 1) {
        failed = await captureRows(page, provider, () => grid.clickHeader('amount'), { timeout: 90_000 });
        statuses.push(failed.status);
      }
      if (statuses.length > 1) {
        test.info().annotations.push({
          type: `divergence: ${provider}`,
          description: `the first request after the container stopped answered HTTP ${statuses.slice(0, -1).join(', ')} before 503 (a pooled connection closed by the shutdown)`,
        });
      }
      expect(failed.status, `${provider} ${what}: expected HTTP 503 (answers so far: ${statuses.join(', ')})`).toBe(503);
      expect(failed.error?.kind, `${provider} ${what}`).toBe('backend-unavailable');
      await expect(grid.errorBanner(), `${provider} ${what}: the banner`).toContainText('backend-unavailable');
      // The grid is still there, not a blank page.
      await expect(page.locator('.ag-root-wrapper')).toBeVisible();
      await expect(grid.headerCell('amount')).toBeVisible();
      await expect(page.getByText('API: up')).toBeVisible();

      // Another provider, whose database is up, keeps working from the same page.
      const down = providersOf(engine);
      const other = served(['sqlite', 'ef-sqlite', 'mongo', 'postgres']).find((id) => !down.includes(id));
      if (other) {
        const answer = await captureRows(page, other, () => grid.providerButton(other).click(), { timeout: 60_000 });
        expect(answer.status, `${other} ${what}: the page must keep working`).toBe(200);
        expect(answer.rows.length, `${other} ${what}`).toBeGreaterThan(0);
        await expect(grid.errorBanner(), `${other} ${what}: no banner after switching`).toHaveCount(0);
      }

      await restore();
      // The provider answers again (the API reconnects on its own).
      await expect
        .poll(async () => (await fetchRowsFor(request, provider, { startRow: 0, endRow: 1, rowGroupCols: [], valueCols: [], pivotCols: [], pivotMode: false, groupKeys: [], filterModel: {}, sortModel: [] })).status, {
          message: `${provider} after ${engine.service} was started again (API ${apiUrl()})`,
          timeout: 120_000,
          intervals: [1000, 2000, 5000],
        })
        .toBe(200);
    });
  }
});
