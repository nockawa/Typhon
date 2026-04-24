import { test, expect } from '@playwright/test';

/**
 * Tier-1 regression guard: switching from a Trace session to an Attach session (or vice versa)
 * must replace the header state wholesale. The two kinds render different chrome — Trace shows a
 * totals pill (`N ticks · Xms · N systems`); Attach shows a connection pill (`Connected · N
 * ticks received · Follow`). A regression in the Profiler store reset would overlay attach-mode
 * pills on top of trace-mode metadata or vice versa.
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

async function stopMockProfiler(
  request: import('@playwright/test').APIRequestContext,
  port: number | null,
): Promise<void> {
  if (port == null) return;
  await request.delete(`http://localhost:5173/api/fixtures/mock-profiler/${port}`);
}

test.describe('Profiler — session-kind switch (Tier-1 regression)', () => {
  test('trace → attach: trace header cleared, attach chrome visible', async ({ page, request }) => {
    await closeAllSessions(request);

    // Trace fixture.
    const fxTrace = await request.post('http://localhost:5173/api/fixtures/trace', {
      data: { tickCount: 7, instantsPerTick: 2 },
    });
    const { traceFilePath } = await fxTrace.json();

    // Mock profiler endpoint for the attach half.
    const fxMock = await request.post('http://localhost:5173/api/fixtures/mock-profiler', {
      data: { blockIntervalMs: 40, maxBlocks: 100 },
    });
    const { port } = await fxMock.json() as { port: number };

    try {
      await page.addInitScript(() => {
        try { localStorage.clear(); } catch { /* ignore */ }
      });
      await page.goto('/');

      // Open the trace first.
      await page.getByRole('button', { name: /^open \.typhon-trace$/i }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await page.getByRole('tab', { name: /^open trace$/i }).click();
      await page.getByPlaceholder(/\.typhon-trace$/i).fill(traceFilePath);
      await page.getByRole('button', { name: /^open$/i }).click();
      await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
      await expect(page.getByText(/\b7 ticks\b/)).toBeVisible({ timeout: 15_000 });

      // Now switch to Attach via File menu.
      await page.getByRole('menuitem', { name: /^file$/i }).click();
      await page.getByRole('menuitem', { name: /attach to engine/i }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await page.getByPlaceholder('localhost:9100').fill(`127.0.0.1:${port}`);
      await page.getByRole('button', { name: /^attach$/i }).click();
      await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });

      // Attach-mode chrome visible.
      await expect(page.getByText(/^connected$/i)).toBeVisible({ timeout: 10_000 });
      await expect(page.getByText(/ticks received/i)).toBeVisible();

      // Trace-mode totals pill must be gone — the "7 ticks" from the previous trace can't linger.
      await expect(page.getByText(/\b7 ticks\b/)).not.toBeVisible();
    } finally {
      await closeAllSessions(request);
      await stopMockProfiler(request, port);
    }
  });

  test('attach → trace: attach chrome cleared, trace totals visible', async ({ page, request }) => {
    await closeAllSessions(request);

    const fxMock = await request.post('http://localhost:5173/api/fixtures/mock-profiler', {
      data: { blockIntervalMs: 40, maxBlocks: 100 },
    });
    const { port } = await fxMock.json() as { port: number };

    const fxTrace = await request.post('http://localhost:5173/api/fixtures/trace', {
      data: { tickCount: 4, instantsPerTick: 2 },
    });
    const { traceFilePath } = await fxTrace.json();

    try {
      await page.addInitScript(() => {
        try { localStorage.clear(); } catch { /* ignore */ }
      });
      await page.goto('/');

      // Attach first.
      await page.getByRole('button', { name: /^attach to engine$/i }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await page.getByPlaceholder('localhost:9100').fill(`127.0.0.1:${port}`);
      await page.getByRole('button', { name: /^attach$/i }).click();
      await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
      await expect(page.getByText(/^connected$/i)).toBeVisible({ timeout: 10_000 });

      // Now open the trace.
      await page.getByRole('menuitem', { name: /^file$/i }).click();
      await page.getByRole('menuitem', { name: /open \.typhon-trace/i }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await page.getByRole('tab', { name: /^open trace$/i }).click();
      await page.getByPlaceholder(/\.typhon-trace$/i).fill(traceFilePath);
      await page.getByRole('button', { name: /^open$/i }).click();
      await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });

      // Trace totals pill visible.
      await expect(page.getByText(/\b4 ticks\b/)).toBeVisible({ timeout: 15_000 });

      // Attach chrome gone — "Connected" pill and "ticks received" counter were attach-specific.
      await expect(page.getByText(/^connected$/i)).not.toBeVisible();
      await expect(page.getByText(/ticks received/i)).not.toBeVisible();
    } finally {
      await closeAllSessions(request);
      await stopMockProfiler(request, port);
    }
  });
});
