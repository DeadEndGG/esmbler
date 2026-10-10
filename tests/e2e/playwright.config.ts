import { defineConfig, devices } from '@playwright/test';

// Base URL of the running system (docker compose web container by default).
const baseURL = process.env.ESMBLER_WEB_URL ?? 'http://localhost:8080';
// Use a preinstalled Chromium when the bundled browser version differs (e.g. sandboxed CI images).
const executablePath = process.env.CHROMIUM_PATH || undefined;

export default defineConfig({
  testDir: './specs',
  timeout: 90_000,
  expect: { timeout: 15_000 },
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: [['list'], ['html', { open: 'never', outputFolder: 'report' }]],
  use: {
    baseURL,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    launchOptions: executablePath ? { executablePath } : {},
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'], viewport: { width: 1440, height: 900 } } }],
});
