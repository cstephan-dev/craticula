import type { FullConfig } from '@playwright/test';
import { PROVIDERS_ENV } from './support/providers';

function stop(message: string): never {
  console.error(`\n${message}\n`);
  process.exit(2);
}

function steps(webUrl: string, apiUrl: string): string {
  return [
    'These tests drive a running demo. From examples/demo, in separate terminals (see the demo README, "Run it, step by step"):',
    '  1. docker compose up -d',
    '  2. dotnet run --project Api -- seed',
    '  3. dotnet run --project Api',
    '  4. cd web && npm run dev',
    `Expected: the API on ${apiUrl} (DEMO_API_URL) and the page on ${webUrl} (DEMO_WEB_URL).`,
  ].join('\n');
}

async function reachable(url: string): Promise<string | null> {
  try {
    const response = await fetch(url, { signal: AbortSignal.timeout(10_000) });
    return response.ok ? null : `HTTP ${response.status}`;
  } catch (error) {
    return error instanceof Error ? (error.cause instanceof Error ? error.cause.message : error.message) : 'no answer';
  }
}

/** Fails fast with the way to start the demo when it is not up, and passes the served providers on to the specs. */
export default async function globalSetup(config: FullConfig): Promise<void> {
  const webUrl = config.projects[0]?.use.baseURL ?? 'http://localhost:5173';
  const apiUrl = (process.env.DEMO_API_URL ?? 'http://127.0.0.1:5080').replace(/\/$/, '');

  let providers: string[];
  try {
    const response = await fetch(`${apiUrl}/api/health`, { signal: AbortSignal.timeout(10_000) });
    if (!response.ok) {
      throw new Error(`HTTP ${response.status}`);
    }
    providers = ((await response.json()) as { providers?: string[] }).providers ?? [];
  } catch (error) {
    const why = error instanceof Error ? (error.cause instanceof Error ? error.cause.message : error.message) : 'no answer';
    stop(`The demo API does not answer at ${apiUrl}/api/health (${why}).\n${steps(webUrl, apiUrl)}`);
  }

  const pageProblem = await reachable(webUrl);
  if (pageProblem) {
    stop(`The demo page does not answer at ${webUrl} (${pageProblem}).\n${steps(webUrl, apiUrl)}`);
  }

  // Set here, read by the workers (they are started after this function returns and inherit the environment).
  process.env[PROVIDERS_ENV] = JSON.stringify(providers);
  console.log(`Demo is up. Providers served: ${providers.join(', ') || '(none)'}`);
}
