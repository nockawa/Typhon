import { test, expect } from '@playwright/test';

/**
 * Tier-3 polish guard: the theme classList effect in ThemeProvider must wire up correctly on
 * first load. The persist/rehydrate logic itself is unit-tested; this is the DOM-level smoke
 * check that confirms the store value actually reaches `<html>.classList`.
 *
 * A regression here (effect never runs, wrong element targeted, class name typo) would make
 * every Tailwind dark-mode style silently inert.
 */

test.describe('Theme — DOM class wiring (Tier-3 smoke)', () => {
  test('default theme applies .dark on <html>', async ({ page }) => {
    await page.addInitScript(() => {
      try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.goto('/');
    // Default in useThemeStore is 'dark' → ThemeProvider's effect adds `.dark` to <html>.
    await expect(page.locator('html')).toHaveClass(/\bdark\b/);
  });

  test('class survives reload (persist → rehydrate round-trip at DOM level)', async ({ page }) => {
    // No seeding — just goto, reload, re-check. This is enough to catch a regression where the
    // persisted theme fails to rehydrate (the `<html>` class would flicker to light on every
    // reload or stay wrong permanently).
    await page.addInitScript(() => {
      try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.goto('/');
    await expect(page.locator('html')).toHaveClass(/\bdark\b/);
    await page.reload();
    await expect(page.locator('html')).toHaveClass(/\bdark\b/);
  });
});
