import { defineConfig } from 'vitest/config';

/**
 * Vitest configuration for the Typhon profiler client. Intentionally minimal — the test suite is pure TypeScript with no
 * browser-DOM dependencies (we test decoders and data-transform helpers, not UI components), so Node's native environment
 * is sufficient and avoids the happy-dom/jsdom startup overhead.
 */
export default defineConfig({
  test: {
    include: ['src/**/*.test.ts'],
    environment: 'node',
  },
});
