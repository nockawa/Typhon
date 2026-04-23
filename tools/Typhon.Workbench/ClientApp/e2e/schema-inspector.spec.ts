import { test, expect } from '@playwright/test';
import fs from 'node:fs';
import path from 'node:path';

const DEMO_DIR = path.resolve('../bin/Debug/net10.0/DemoData');

/**
 * Prepares a clean demo session — same ritual as resource-tree-polish.spec.ts so the Workbench boots
 * against a real, freshly-opened engine. Returns with the dockview shell mounted and the resource
 * tree populated, ready for module-specific interactions.
 */
async function openDemo(
  page: import('@playwright/test').Page,
  request: import('@playwright/test').APIRequestContext,
) {
  fs.mkdirSync(DEMO_DIR, { recursive: true });
  fs.writeFileSync(path.join(DEMO_DIR, 'demo.typhon'), '');

  const list = await request.get('http://localhost:5200/api/sessions');
  if (list.ok()) {
    const { sessions = [] } = await list.json();
    for (const s of sessions as Array<{ sessionId: string }>) {
      await request.delete(`http://localhost:5200/api/sessions/${s.sessionId}`, {
        headers: { 'X-Session-Token': s.sessionId },
      });
    }
  }

  const seed = await request.post('http://localhost:5200/api/sessions/file', {
    data: { filePath: 'demo.typhon' },
  });
  const seedJson = await seed.json();
  await request.delete(`http://localhost:5200/api/sessions/${seedJson.sessionId}`, {
    headers: { 'X-Session-Token': seedJson.sessionId },
  });

  await page.addInitScript(() => {
    try { localStorage.clear(); } catch { /* ignore */ }
  });
  await page.goto('/');
  await page.getByRole('button', { name: /^open \.typhon file$/i }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
  await page.getByPlaceholder(/path/i).first().fill(DEMO_DIR);
  const demoRow = page.getByText(/^demo\.typhon$/).first();
  await expect(demoRow).toBeVisible({ timeout: 10_000 });
  await demoRow.click();
  await page.getByRole('button', { name: /^open$/i }).click();
  await expect(page.getByRole('dialog')).not.toBeVisible({ timeout: 10_000 });
  await expect(page.locator('body')).toContainText(/Storage|DataEngine/i, { timeout: 10_000 });
}

test.describe('Schema Inspector — Phase 1', () => {
  test('View → Component Browser opens the panel with a working search', async ({ page, request }) => {
    await openDemo(page, request);

    // Radix Menubar: clicking the trigger toggles the popup. Wait for the Component Browser item
    // to be visible before clicking to avoid racing the popup animation.
    await page.getByRole('menuitem', { name: /^view$/i }).click();
    const schemaBrowserItem = page.getByRole('menuitem', { name: /component browser/i });
    await expect(schemaBrowserItem).toBeVisible({ timeout: 5_000 });
    await schemaBrowserItem.click();

    // The panel mounting is confirmed by the search input — role-based tab assertions don't work
    // because dockview-react doesn't tag tabs with an ARIA role.
    const search = page.getByPlaceholder(/search components/i);
    await expect(search).toBeVisible({ timeout: 5_000 });

    // Surface renders cleanly under filter churn.
    await search.fill('noMatchExpectedHere');
    await search.fill('');
  });

  test('Palette command "Open Component Browser" opens the panel', async ({ page, request }) => {
    await openDemo(page, request);

    // Click the palette trigger (Ctrl+K may be intercepted by vite's HMR overlay on some setups).
    await page.getByRole('button', { name: /open command palette/i }).click();
    const paletteInput = page.getByPlaceholder(/search commands/i);
    await expect(paletteInput).toBeVisible();

    await paletteInput.fill('schema');
    const command = page.getByRole('option', { name: /open component browser/i });
    await expect(command).toBeVisible();
    await command.click();

    // Panel-mount canary (see note in the View menu test).
    await expect(page.getByPlaceholder(/search components/i)).toBeVisible({ timeout: 5_000 });
  });

  test('Resource-tree right-click → "Show Component Layout" is disabled for non-ComponentTable nodes', async ({ page, request }) => {
    await openDemo(page, request);

    // First row in the tree is the engine root or a subsystem — definitely not a ComponentTable —
    // so the action should remain disabled. This is the design contract: the item only enables on
    // component-table rows, which the demo session doesn't expose without schema DLLs.
    const firstRowInner = page.locator('[role="treeitem"] > div').first();
    await firstRowInner.click({ button: 'right' });
    await expect(
      page.getByRole('menuitem', { name: /show component layout/i }),
    ).toBeDisabled();
    await page.keyboard.press('Escape');
  });

  test('View → Component Archetypes is disabled without a selection', async ({ page, request }) => {
    await openDemo(page, request);

    await page.getByRole('menuitem', { name: /^view$/i }).click();
    const item = page.getByRole('menuitem', { name: /component archetypes/i });
    await expect(item).toBeVisible({ timeout: 5_000 });
    // Design contract: per-component panels are inert without a selection (tooltip: "Select a
    // component first"). Disabled items cannot be clicked.
    await expect(item).toBeDisabled();
    await page.keyboard.press('Escape');
  });

  test('View → Component Indexes is disabled without a selection', async ({ page, request }) => {
    await openDemo(page, request);

    await page.getByRole('menuitem', { name: /^view$/i }).click();
    const item = page.getByRole('menuitem', { name: /component indexes/i });
    await expect(item).toBeVisible({ timeout: 5_000 });
    await expect(item).toBeDisabled();
    await page.keyboard.press('Escape');
  });

  test('View → Component Relationships — disabled without selection; opens with runtime-not-hosted banner when selected', async ({ page, request }) => {
    await openDemo(page, request);

    // Open Component Browser so we can select a row if the demo session has any components.
    await page.getByRole('menuitem', { name: /^view$/i }).click();
    await page.getByRole('menuitem', { name: /component browser/i }).click();
    const search = page.getByPlaceholder(/search components/i);
    await expect(search).toBeVisible({ timeout: 5_000 });

    // Demo session may or may not expose user component rows. Branch accordingly.
    const firstRow = page.getByTestId('schema-row').first();
    const hasComponentRow = await firstRow.isVisible().catch(() => false);

    if (hasComponentRow) {
      await firstRow.dblclick();
      await page.getByRole('menuitem', { name: /^view$/i }).click();
      const item = page.getByRole('menuitem', { name: /component relationships/i });
      await expect(item).toBeEnabled();
      await item.click();
      await expect(page.getByText(/runtime not hosted/i)).toBeVisible({ timeout: 5_000 });
    } else {
      await page.getByRole('menuitem', { name: /^view$/i }).click();
      const item = page.getByRole('menuitem', { name: /component relationships/i });
      await expect(item).toBeDisabled();
      await page.keyboard.press('Escape');
    }
  });

  test('View → Archetype Browser opens a panel with search + filter chips', async ({ page, request }) => {
    await openDemo(page, request);

    await page.getByRole('menuitem', { name: /^view$/i }).click();
    const archBrowserItem = page.getByRole('menuitem', { name: /archetype browser/i });
    await expect(archBrowserItem).toBeVisible({ timeout: 5_000 });
    await archBrowserItem.click();

    // Panel-mount canary: the Archetype Browser has its own search placeholder distinct from
    // Component Browser's. Filter chip row confirms the quick-filter stripe is wired.
    const search = page.getByPlaceholder(/search archetypes/i);
    await expect(search).toBeVisible({ timeout: 5_000 });
    await expect(page.getByText(/quick filters:/i)).toBeVisible();

    // Verify the two quick-filter chips exist. They're rendered as Badges — no native role, so
    // target by visible text.
    await expect(page.getByText(/^no entities$/i)).toBeVisible();
    await expect(page.getByText(/^legacy storage$/i)).toBeVisible();

    // Sanity: typing in the search doesn't crash the table under churn.
    await search.fill('nomatchxyz');
    await search.fill('');
  });

  test('Palette command "Open Archetype Browser" opens the panel', async ({ page, request }) => {
    await openDemo(page, request);

    await page.getByRole('button', { name: /open command palette/i }).click();
    const paletteInput = page.getByPlaceholder(/search commands/i);
    await expect(paletteInput).toBeVisible();

    await paletteInput.fill('archetype');
    const command = page.getByRole('option', { name: /open archetype browser/i });
    await expect(command).toBeVisible();
    await command.click();

    await expect(page.getByPlaceholder(/search archetypes/i)).toBeVisible({ timeout: 5_000 });
  });

  test('Connect dialog exposes a Dev Fixture tab when the server is built with DEBUG', async ({ page, request }) => {
    // Close any active session first so the Connect dialog picker is reachable.
    const list = await request.get('http://localhost:5200/api/sessions');
    if (list.ok()) {
      const { sessions = [] } = await list.json();
      for (const s of sessions as Array<{ sessionId: string }>) {
        await request.delete(`http://localhost:5200/api/sessions/${s.sessionId}`, {
          headers: { 'X-Session-Token': s.sessionId },
        });
      }
    }
    await page.addInitScript(() => {
      try { localStorage.clear(); } catch { /* ignore */ }
    });
    await page.goto('/');

    await page.getByRole('button', { name: /^open \.typhon file$/i }).click();
    await expect(page.getByRole('dialog')).toBeVisible();

    // The Dev Fixture trigger is rendered only when GET /api/fixtures/capability succeeds — which
    // happens only in DEBUG builds. The e2e harness always runs against a DEBUG build, so this tab
    // must be present. A Release-build server would 404 the capability probe and the tab would be
    // hidden; that scenario isn't covered here because we don't ship Release e2e yet.
    const devFixtureTab = page.getByRole('tab', { name: /dev fixture/i });
    await expect(devFixtureTab).toBeVisible({ timeout: 5_000 });
    await devFixtureTab.click();

    // The tab's body must render the force-recreation checkbox + the Create & Open button.
    await expect(page.getByText(/force recreation/i)).toBeVisible();
    await expect(page.getByRole('button', { name: /(create|recreate) & open/i })).toBeVisible();

    // Dismiss — we don't actually generate the fixture here; that would conflict with any running
    // Workbench session holding file handles. The Create flow is exercised end-to-end by the
    // NUnit WorkbenchFixtureGenerator test (manually un-ignored), which calls the same internal
    // FixtureDatabase.CreateOrReuse under the hood.
    await page.keyboard.press('Escape');
  });
});
