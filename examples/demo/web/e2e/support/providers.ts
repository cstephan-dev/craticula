import { test } from '@playwright/test';
import { PROVIDERS, type Provider } from '../../src/api';

/** Environment variable carrying the provider ids `/api/health` listed, as a JSON array: set by global-setup.ts, read by the specs. */
export const PROVIDERS_ENV = 'DEMO_E2E_PROVIDERS';

/** The provider every other one is compared with. */
export const BASELINE: Provider = 'postgres';

/** Where the API answers: the same address `global-setup.ts` probed. */
export function apiUrl(): string {
  return (process.env.DEMO_API_URL ?? 'http://127.0.0.1:5080').replace(/\/$/, '');
}

/** The providers `/api/health` listed when the run started (the page offers exactly these). */
export function servedProviders(): Provider[] {
  const raw = process.env[PROVIDERS_ENV];
  if (raw === undefined) {
    throw new Error(`${PROVIDERS_ENV} is not set: the specs must run through "npm run e2e" so that global-setup.ts can read /api/health.`);
  }
  const listed = JSON.parse(raw) as string[];
  // Catalogue order, ignoring ids the page does not know (the same reading as the page's own provider switch).
  return PROVIDERS.map((item) => item.id).filter((id) => listed.includes(id));
}

export function isServed(provider: Provider): boolean {
  return servedProviders().includes(provider);
}

/** The providers of `wanted` that are served, in the order given. */
export function served(wanted: readonly Provider[]): Provider[] {
  const available = servedProviders();
  return wanted.filter((id) => available.includes(id));
}

/**
 * Skips the running test (or, in a `describe` or `beforeEach`, the whole group) when one of the providers is not served,
 * with the reason in the report. Call it first.
 */
export function skipUnlessServed(...required: Provider[]): void {
  const missing = required.filter((id) => !isServed(id));
  test.skip(missing.length > 0, `${missing.join(', ')} not served by this API (the page only shows what /api/health lists)`);
}
