import { defineConfig, devices } from '@playwright/test';

// Solo-dev, local-only E2E. No CI integration (intentional).
// Prerequisite: run `dotnet run` and `npm run dev` in two terminals before `npm run test:e2e`.
export default defineConfig({
  testDir: './e2e',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  reporter: 'list',
  use: {
    baseURL: 'http://localhost:5173',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [
    { name: 'chromium', use: { ...devices['Desktop Chrome'] } },
  ],
});
