import { defineConfig } from '@playwright/test';
import { fileURLToPath } from 'node:url';

// The admin SPA runs on the Vite dev server; every /api call is answered by the in-test fake backend.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  retries: process.env.CI ? 1 : 0,
  reporter: 'list',
  use: {
    baseURL: 'http://127.0.0.1:5174',
    browserName: 'chromium',
    headless: true
  },
  webServer: {
    command: 'npm run dev',
    cwd: fileURLToPath(new URL('../../src/admin', import.meta.url)),
    url: 'http://127.0.0.1:5174/',
    reuseExistingServer: !process.env.CI,
    timeout: 120000
  }
});
