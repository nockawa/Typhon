import { describe, it, expect } from 'vitest';

// Trivial smoke test — exists so `npm test` exits 0 in Phase 0 CI. Real tests land with real code starting at Phase 1.
describe('Phase 0 smoke', () => {
  it('runs Vitest', () => {
    expect(1 + 1).toBe(2);
  });
});
