import { defineConfig } from 'vitest/config';

// Vitest config is kept separate from vite.config.ts so the test runner doesn't load the full Vite plugin stack (React, Tailwind,
// vite-plugin-checker) on every `npm test`. Phase 0 tests are pure TypeScript with no DOM — `environment: 'node'` avoids the
// jsdom/happy-dom startup cost. When we add component tests, we'll flip this to 'jsdom' via a per-file directive or a second config.
export default defineConfig({
  test: {
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
    environment: 'node',
  },
});
