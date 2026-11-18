import { defineConfig } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  retries: process.env.CI ? 1 : 0,
  reporter: 'list',
  use: {
    baseURL: 'http://127.0.0.1:5173',
    browserName: 'chromium',
    headless: true
  },
  webServer: {
    command: 'npm run dev',
    cwd: new URL('../../src/frontend', import.meta.url).pathname,
    url: 'http://127.0.0.1:5173/LllmFacade/',
    reuseExistingServer: !process.env.CI,
    timeout: 120000,
    env: { ...process.env, VITE_BASE_PATH: '/LllmFacade/' }
  }
});
