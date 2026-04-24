import { test, expect } from '@playwright/test';

/**
 * Tier-0 canary for the **Attach** flow (umbrella #267 / sub-issue #262). Guards the core UX
 * contract that shipped without an end-to-end test: TCP connect → Init frame → live tick counter
 * starts climbing in the panel header.
 *
 * Backing server is the in-process MockTcpProfilerServer spun up by the DEBUG-only
 * `POST /api/fixtures/mock-profiler` endpoint — that gives us a real TCP profiler on loopback
 * without needing a second process. The paired DELETE endpoint tears it down so a long-running
 * dev server doesn't accumulate zombies across test runs.
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

test.describe('Profiler — attach connect (Tier-0 canary)', () => {
  test('attach to mock profiler → Connected → tick counter climbs', async ({ page, request }) => {
    await closeAllSessions(request);

    // Start the mock profiler. 50 ms between blocks × 200 max blocks = ~10 s of ticks — plenty
    // of headroom to observe the counter climb before the test ends.
    const start = await request.post('http://localhost:5173/api/fixtures/mock-profiler', {
      data: { blockIntervalMs: 50, maxBlocks: 200 },
    });
    expect(start.ok(), 'fixture endpoint should respond 200').toBeTruthy();
    const { port } = await start.json() as { port: number };
    expect(port).toBeGreaterThan(0);

    try {
      await page.addInitScript(() => {
        try { localStorage.clear(); } catch { /* ignore */ }
      });
      await page.goto('/');

      await page.getByRole('button', { name: /^attach to engine$/i }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await expect(page.getByRole('tab', { name: /attach/i })).toHaveAttribute('data-state', 'active');

      const endpointInput = page.getByPlaceholder('localhost:9100');
      await endpointInput.fill(`127.0.0.1:${port}`);
      await page.getByRole('button', { name: /^attach$/i }).click();

      // Dialog closes once POST /api/sessions/attach returns (after the 3-retry TCP connect).
      await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });

      // Panel header shows the Connected pill.
      await expect(page.getByText(/^connected$/i)).toBeVisible({ timeout: 10_000 });

      // The live tick counter ("N ticks received") should climb past 0 as Block frames arrive.
      // The mock emits a block every 50 ms with one TickStart+TickEnd pair → one tick per block.
      // Wait up to 5 s for at least one tick to land in the UI.
      await expect
        .poll(
          async () => {
            const txt = await page.getByText(/\d+ ticks received/).textContent();
            if (!txt) return 0;
            const m = txt.match(/(\d[\d,]*)\s+ticks received/);
            return m ? Number(m[1].replace(/,/g, '')) : 0;
          },
          { timeout: 5_000, intervals: [100, 250, 500] },
        )
        .toBeGreaterThan(0);
    } finally {
      await closeAllSessions(request);
      await stopMockProfiler(request, port);
    }
  });

  test('attach to dead port → 503 → dialog stays open with error pill', async ({ page, request }) => {
    await closeAllSessions(request);

    // Port 1 is reserved / not listening — the retry loop will exhaust and return 503.
    await page.addInitScript(() => {
      try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.goto('/');

    await page.getByRole('button', { name: /^attach to engine$/i }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.getByPlaceholder('localhost:9100').fill('127.0.0.1:1');
    await page.getByRole('button', { name: /^attach$/i }).click();

    // AttachSessionRuntime retries 3 × 2 s before 503 — ~6-8 s worst case. Give 15 s headroom.
    await expect(page.getByText(/failed to attach/i)).toBeVisible({ timeout: 15_000 });
    await expect(page.getByRole('dialog')).toBeVisible();
    await page.keyboard.press('Escape');
  });
});
