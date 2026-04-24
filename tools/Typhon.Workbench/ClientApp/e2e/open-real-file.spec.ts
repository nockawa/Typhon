import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

// Resolve the server's working-dir DemoData so we can type it into the in-app FileBrowser.
// ClientApp is at tools/Typhon.Workbench/ClientApp; server writes to ../bin/Debug/net10.0/DemoData.
const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

test.describe('Phase 4 — Connect Dialog', () => {
  test('Welcome shows all 4 entry buttons', async ({ page }) => {
    await page.goto('/');
    await expect(page.getByRole('button', { name: /^open \.typhon file$/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /^open \.typhon-trace$/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /^attach to engine$/i })).toBeVisible();
    await expect(page.getByRole('button', { name: /^recent files$/i })).toBeVisible();
  });

  test('Recent Files button opens dialog on Recent tab with empty state', async ({ page }) => {
    await page.goto('/');
    await page.getByRole('button', { name: /^recent files$/i }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(page.getByRole('tab', { name: /recent/i })).toHaveAttribute('data-state', 'active');
    await expect(page.getByText(/no recent files/i)).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.getByRole('dialog')).not.toBeVisible();
  });

  test('Attach button opens dialog on Attach tab with live endpoint form', async ({ page }) => {
    // Attach shipped in Phase 1b — the old "coming soon" stub is gone. This test now verifies
    // the real form chrome: endpoint input with default placeholder + an Attach submit button.
    // Real attach-to-mock end-to-end coverage lives in profiler-attach-connect.spec.ts.
    await page.goto('/');
    await page.getByRole('button', { name: /^attach to engine$/i }).click();
    await expect(page.getByRole('tab', { name: /attach/i })).toHaveAttribute('data-state', 'active');
    await expect(page.getByPlaceholder('localhost:9100')).toBeVisible();
    await expect(page.getByRole('button', { name: /^attach$/i })).toBeVisible();
  });

  test('Open File → browse to DemoData → pick demo.typhon → open → tree renders', async ({ page, request }) => {
    // Ensure DemoData dir exists and has a demo.typhon marker file (the engine writes its data as
    // demo.bin, but the user-facing UI picks .typhon — create the marker manually).
    fs.mkdirSync(DEMO_DIR, { recursive: true });
    fs.writeFileSync(path.join(DEMO_DIR, 'demo.typhon'), '');

    // Release any stale session holding a file lock on demo.bin (a previous test may have opened
    // and not closed it cleanly). We do this by a quick open+delete cycle.
    const seed = await request.post('http://localhost:5200/api/sessions/file', {
      data: { filePath: 'demo.typhon' },
    });
    const seedJson = await seed.json();
    await request.delete(`http://localhost:5200/api/sessions/${seedJson.sessionId}`, {
      headers: { 'X-Session-Token': seedJson.sessionId },
    });

    await page.goto('/');
    await page.getByRole('button', { name: /^open \.typhon file$/i }).click();
    await expect(page.getByRole('dialog')).toBeVisible();

    // Navigate the FileBrowser to the demo directory by typing into the breadcrumb input.
    const pathInput = page.getByPlaceholder(/path/i).first();
    await pathInput.fill(DEMO_DIR);

    // Wait for the listing to show the demo file; click it to select.
    const demoRow = page.getByText(/^demo\.typhon$/).first();
    await expect(demoRow).toBeVisible({ timeout: 10_000 });
    await demoRow.click();

    // Open button becomes enabled; click it.
    const openBtn = page.getByRole('button', { name: /^open$/i });
    await expect(openBtn).toBeEnabled();
    await openBtn.click();

    // Dialog closes; tree renders engine subsystems.
    await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
    await expect(page.locator('body')).toContainText(
      /Storage|DataEngine|Durability|Allocation|Synchronization/i,
      { timeout: 10_000 },
    );
  });
});
