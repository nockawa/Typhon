import { test, expect } from '@playwright/test';

/**
 * Tier-1 regression guard: opening a second trace must not leak the first trace's tick count /
 * span data into the panel header. The ProfilerPanel's useEffect(() => return () => reset(),
 * [sessionId, ...]) is the sole cleanup point; a regression there would show the first trace's
 * numbers alongside the second trace's file name.
 *
 * Both traces are generated on the server via the DEBUG-only `/api/fixtures/trace` endpoint with
 * distinct tick counts so the assertion can tell them apart purely from the UI text.
 */

interface SessionSummary { sessionId: string }

async function closeAllSessions(request: import('@playwright/test').APIRequestContext): Promise<void> {
  const list = await request.get('http://localhost:5173/api/sessions');
  if (!list.ok()) return;
  const { sessions = [] } = await list.json();
  for (const s of sessions as SessionSummary[]) {
    await request.delete(`http://localhost:5173/api/sessions/${s.sessionId}`, {
      headers: { 'X-Session-Token': s.sessionId },
    });
  }
}

async function openTraceViaDialog(
  page: import('@playwright/test').Page,
  absolutePath: string,
): Promise<void> {
  await page.getByRole('button', { name: /^open \.typhon-trace$/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByRole('tab', { name: /^open trace$/i }).click();
  await page.getByPlaceholder(/\.typhon-trace$/i).fill(absolutePath);
  await page.getByRole('button', { name: /^open$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
}

test.describe('Profiler — session cleanup between traces (Tier-1 regression)', () => {
  test('second trace header reflects the new tick count, not the first trace\'s', async ({ page, request }) => {
    await closeAllSessions(request);

    // Generate two distinct traces so the UI text for tick count will differ (3 vs 11 ticks).
    const fxA = await request.post('http://localhost:5173/api/fixtures/trace', {
      data: { tickCount: 3, instantsPerTick: 2 },
    });
    const fxB = await request.post('http://localhost:5173/api/fixtures/trace', {
      data: { tickCount: 11, instantsPerTick: 2 },
    });
    expect(fxA.ok()).toBeTruthy();
    expect(fxB.ok()).toBeTruthy();
    const { traceFilePath: pathA } = await fxA.json();
    const { traceFilePath: pathB } = await fxB.json();

    await page.addInitScript(() => {
      try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.goto('/');

    // Open trace A → header shows "3 ticks".
    await openTraceViaDialog(page, pathA);
    await expect(page.getByText(/\b3 ticks\b/)).toBeVisible({ timeout: 15_000 });

    // Open trace B (File → Connect… → trace tab). Use the menu bar entry so we don't race the
    // Welcome screen; MenuBar is stable once a session is active.
    await page.getByRole('menuitem', { name: /^file$/i }).click();
    await page.getByRole('menuitem', { name: /open \.typhon-trace/i }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.getByRole('tab', { name: /^open trace$/i }).click();
    await page.getByPlaceholder(/\.typhon-trace$/i).fill(pathB);
    await page.getByRole('button', { name: /^open$/i }).click();
    await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });

    // Header must show B's tick count (11) AND must NOT show A's (3). The explicit negative
    // assertion catches the regression even if the UI happens to render "3 11 ticks" or similar.
    await expect(page.getByText(/\b11 ticks\b/)).toBeVisible({ timeout: 15_000 });
    await expect(page.getByText(/\b3 ticks\b/)).not.toBeVisible();

    await closeAllSessions(request);
  });
});
