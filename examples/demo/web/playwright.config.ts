import { defineConfig, devices } from '@playwright/test';

// The tests drive a demo that is already running (see the demo README): the page on DEMO_WEB_URL, the API on DEMO_API_URL.
const webUrl = process.env.DEMO_WEB_URL ?? 'http://localhost:5173';

export default defineConfig({
  testDir: './e2e',
  outputDir: 'test-results',
  globalSetup: './e2e/global-setup.ts',
  reporter: [['list'], ['html', { open: 'never' }]],
  // Every spec reads the same database and writes nothing, so workers may run side by side.
  fullyParallel: true,
  // The first request to a cold engine can take a while.
  timeout: 60_000,
  expect: { timeout: 15_000 },
  use: {
    baseURL: webUrl,
    // The date filters read dates as UTC midnight; a fixed zone and locale keep the date pickers the same on every machine.
    timezoneId: 'UTC',
    locale: 'en-US',
    permissions: ['clipboard-read', 'clipboard-write'],
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    {
      name: 'chromium',
      use: {
        ...devices['Desktop Chrome'],
        // Wide enough that every column is drawn: the grid only renders the columns in view.
        viewport: { width: 1920, height: 1080 },
      },
    },
  ],
});
