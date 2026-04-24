import path from 'node:path';
import { defineConfig } from 'vitest/config';

// Vitest config is kept separate from vite.config.ts so the test runner doesn't load the full Vite plugin stack (React, Tailwind,
// vite-plugin-checker) on every `npm test`. Phase 0 tests are pure TypeScript with no DOM — `environment: 'node'` avoids the
// jsdom/happy-dom startup cost. When we add component tests, we'll flip this to 'jsdom' via a per-file directive or a second config.
//
// The `@/` alias mirrors vite.config.ts. Type-only `import type { ... } from '@/...'` resolves
// without the alias (TS erases them), but runtime `@/...` imports transitively pulled in by a
// test file fail without it — vitest doesn't inherit vite.config's resolve rules.
export default defineConfig({
  resolve: {
    alias: {
      '@': path.resolve(__dirname, './src'),
    },
  },
  test: {
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
    environment: 'node',
  },
});
