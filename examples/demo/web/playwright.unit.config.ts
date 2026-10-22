import { defineConfig } from '@playwright/test';

// The self-tests of the e2e helpers (e2e/support/*.spec.ts) need neither a browser nor a running demo, so this config has
// no global setup (the main config's one stops the run when the demo is down). Run them with `npm run e2e:unit`.
export default defineConfig({
  testDir: './e2e',
  outputDir: 'test-results',
  reporter: [['list']],
  projects: [{ name: 'unit', testMatch: /support\/.*\.spec\.ts/ }],
});
