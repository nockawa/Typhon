---
name: new-panel
description: Scaffold a new Workbench panel — full stack (server DTOs/service/controller/tests + client hooks/store/panel/context-menu/tests + wiring + Playwright canary) that compiles on first invocation
argument-hint: <ModuleName> <PanelName>
---

# new-panel — Scaffold a Workbench Panel

Generates every file needed for a new dockable panel in the Typhon Workbench. Output compiles on first invocation: `dotnet build` green, `tsc --noEmit` green, `npm run lint` green, vitest green, one Playwright canary green. The service body returns an empty list / default DTO so you can wire a UI immediately and fill in the engine-read code as a second pass.

## Input

`$ARGUMENTS` should contain two PascalCase identifiers separated by whitespace:

- `ModuleName` — the module this panel belongs to (e.g., `QueryConsole`, `SchemaInspector`, `DataBrowser`). Case-sensitive, no spaces, no dots.
- `PanelName` — the specific panel within the module (e.g., `QueryEditor`, `ArchetypeBrowser`). Case-sensitive, no spaces.

If either is missing, use `AskUserQuestion` to collect them.

If `$ARGUMENTS` contains `--help` or `-h`, display the block below and **stop** — do not execute the workflow.

```
/new-panel <ModuleName> <PanelName>

  Scaffold a full-stack Workbench panel that compiles on first invocation.

Arguments:
  ModuleName      Module this panel belongs to (PascalCase, e.g. QueryConsole)
  PanelName       Panel name within the module (PascalCase, e.g. QueryEditor)
  --help, -h      Show this help

What it does:
  - Creates/extends the module's service + controller + DTOs on the server
  - Creates the panel component + context menu + hook on the client
  - Wires dockview registration + menu + palette + open-helper
  - Generates a vitest smoke test + a Playwright canary
  - Verifies the whole stack compiles

Examples:
  /new-panel QueryConsole QueryEditor
  /new-panel SchemaInspector DefaultsDiff
```

## Preflight

Before generating any file:

1. **Parse args** — validate both identifiers are PascalCase, no punctuation. Derive:
   - `moduleSlug` = lowerCamel of ModuleName (e.g. `queryConsole`)
   - `moduleKebab` = kebab of ModuleName (e.g. `query-console`)
   - `panelSlug` = lowerCamel of PanelName (e.g. `queryEditor`)
   - `panelKebab` = kebab of PanelName (e.g. `query-editor`)

2. **Detect module state** — check if a server-side `<ModuleName>Service.cs` exists under `tools/Typhon.Workbench/<ModuleName>/Service/` (or wherever the first module was placed). If **missing**, this is a **new-module** scaffold — add the full server stack. If **present**, this is a **new-panel-in-existing-module** scaffold — only add the panel-level files and skip the server stack.

3. **Confirm target with the user** via `AskUserQuestion`:
   - "New module `<ModuleName>` (generate server + client stack)" OR "Existing module, add panel `<PanelName>` only"
   - "Continue" / "Abort"

## Reference patterns

**Do not embed long templates in this file.** Read the existing implementations and adapt. The authoritative references:

| What | Reference |
|---|---|
| Server service method + error types | `tools/Typhon.Workbench/Schema/SchemaService.cs` |
| Server controller + auth attributes + `Invoke<T>` error mapping | `tools/Typhon.Workbench/Controllers/SchemaController.cs` |
| DTO records (camelCase JSON) | `tools/Typhon.Workbench/Dtos/Schema/ComponentSummaryDto.cs` |
| Server tests + `WorkbenchFactory.CreateAuthenticatedClient` | `test/Typhon.Workbench.Tests/Schema/SchemaControllerTests.cs` |
| Module-scoped zustand store + selection + touchedAt pattern | `tools/Typhon.Workbench/ClientApp/src/stores/useSchemaInspectorStore.ts` |
| Normalized types + Orval-style `normalizeX` functions | `tools/Typhon.Workbench/ClientApp/src/hooks/schema/types.ts` |
| Query hook via Orval generated function + normalizer | `tools/Typhon.Workbench/ClientApp/src/hooks/schema/useComponentList.ts` |
| Query hook via raw `customFetch` (use this path for the scaffold) | `tools/Typhon.Workbench/ClientApp/src/hooks/schema/useArchetypeList.ts` |
| Panel component + empty/loading/error states | `tools/Typhon.Workbench/ClientApp/src/panels/SchemaInspector/SchemaIndexPanel.tsx` |
| Panel with search + filter chips + sortable Table | `tools/Typhon.Workbench/ClientApp/src/panels/SchemaBrowser/SchemaBrowserPanel.tsx` |
| Co-located context menu | `tools/Typhon.Workbench/ClientApp/src/panels/SchemaBrowser/SchemaBrowserContextMenu.tsx` |
| Module-level dock api registry + `openX()` helpers | `tools/Typhon.Workbench/ClientApp/src/shell/commands/openSchemaBrowser.ts` |
| Dockview components-map registration | `tools/Typhon.Workbench/ClientApp/src/shell/DockHost.tsx` |
| Menu bar entry (possibly gated on selection) | `tools/Typhon.Workbench/ClientApp/src/shell/MenuBar.tsx` |
| Palette command registration | `tools/Typhon.Workbench/ClientApp/src/shell/commands/baseCommands.ts` |
| Vitest for a zustand store | `tools/Typhon.Workbench/ClientApp/src/stores/__tests__/useSchemaInspectorStore.test.ts` |
| Playwright canary — panel opens via menu | `tools/Typhon.Workbench/ClientApp/e2e/schema-inspector.spec.ts` |

When generating each file, **Read the reference, adapt the patterns** — don't blindly copy. The Workbench conventions shift and the scaffolder stays current by deferring to the live code.

## Generation plan

Execute in this order so intermediate states are buildable:

### New-module path (full stack)

1. **Server DTOs** (`tools/Typhon.Workbench/Dtos/<ModuleName>/`)
   - One placeholder DTO record. Name it `<PanelName>ItemDto` — the shape the list-endpoint returns an array of. Example fields: `string Name, int Count` — just enough to demonstrate camelCase JSON without pretending to model the future data shape.

2. **Server service** (`tools/Typhon.Workbench/<ModuleName>/<ModuleName>Service.cs`)
   - Single public method `List<PanelName>s(Guid sessionId)` returning `<PanelName>ItemDto[]`.
   - Body: `return [];` plus a `// TODO: read real data from the engine` comment.
   - Use the same `_sessions` + `RequireOpenEngine` pattern as `SchemaService`.

3. **Server controller** (`tools/Typhon.Workbench/Controllers/<ModuleName>Controller.cs`)
   - Route `api/sessions/{sessionId:guid}/<moduleKebab>`.
   - One `GET <panelKebab>s` endpoint calling the service method, wrapped in the standard `Invoke<T>` (copy the wrapper from `SchemaController`).
   - `[RequireBootstrapToken][RequireSession][Tags("<ModuleName>")]`.

4. **Server DI registration** (`tools/Typhon.Workbench/Hosting/ServiceExtensions.cs`)
   - Register `<ModuleName>Service` as scoped alongside existing services.

5. **Server tests** (`test/Typhon.Workbench.Tests/<ModuleName>/<ModuleName>ControllerTests.cs`)
   - Two tests: `List_Returns200_WithEmptyArray` (happy path against a session) and `List_WithoutBootstrapToken_Returns401`.
   - Use `WorkbenchFactory.CreateAuthenticatedClient` and the fixture-session helper the existing tests use.

6. **Client normalized types** (`tools/Typhon.Workbench/ClientApp/src/hooks/<moduleSlug>/types.ts`)
   - Interface `<PanelName>Item` mirroring the DTO, a `normalize<PanelName>Item(raw)` function.
   - Since the DTO is locally declared in the hook file (we're using `customFetch`), import the shape from a local interface, not from `@/api/generated`.

7. **Client query hook** (`tools/Typhon.Workbench/ClientApp/src/hooks/<moduleSlug>/use<PanelName>List.ts`)
   - Use `customFetch` + TanStack Query directly (see `useArchetypeList.ts`). Declare the DTO shape locally as a TS interface so compile works before Orval regen.
   - Add a `// TODO: swap to generated hook once npm run generate-types runs` note.

8. **Client store** (`tools/Typhon.Workbench/ClientApp/src/stores/use<ModuleName>Store.ts`)
   - Zustand store with a single `selected<PanelName>Id: string | null`, a `select(id)` action, a `reset()` action, and a `touchedAt: number` that bumps on select (for DetailPanel recency — follow the pattern in `useSchemaInspectorStore`).

### Shared path (both new-module and panel-in-existing-module)

9. **Panel component** (`tools/Typhon.Workbench/ClientApp/src/panels/<ModuleName>/<PanelName>Panel.tsx`)
   - IDockviewPanelProps signature.
   - Uses the query hook + the module store.
   - Renders: header strip (title + row count + refresh button), empty/loading/error state stack, a `<Table>` of rows with a single "Name" column.
   - Row click → `store.select(row.id)`.
   - Keep the h-full w-full overflow-hidden wrapper per `panels/CLAUDE.md`.

10. **Context menu** (`tools/Typhon.Workbench/ClientApp/src/panels/<ModuleName>/<PanelName>ContextMenu.tsx`)
    - Three disabled stubs: "Copy Name", "Open in Other Panel", "Open in Query Console" — flesh out post-scaffold.

11. **Dockview registration** — edit `shell/DockHost.tsx`:
    - Import the new panel component.
    - Append `<PanelName>: <PanelName>Panel` to the `components` map.

12. **Open helper** (`tools/Typhon.Workbench/ClientApp/src/shell/commands/open<ModuleName>.ts`)
    - If new module: create the file following `openSchemaBrowser.ts`.
    - If existing module: append a new `open<PanelName>()` function that calls the same `openDockPanel` helper.

13. **Menu wiring** — edit `shell/MenuBar.tsx`:
    - Add a `MenubarItem` under the View menu pointing at `open<PanelName>()`.
    - Title attr `"Select an item first"` if the panel requires a selection state.

14. **Palette command** — edit `shell/commands/baseCommands.ts`:
    - Add `{ id: '<moduleKebab>-<panelKebab>', label: 'Open <PanelName>', keywords: '<module lowercase> <panel lowercase>', action: open<PanelName> }`.

15. **Vitest smoke** (`tools/Typhon.Workbench/ClientApp/src/stores/__tests__/use<ModuleName>Store.test.ts`)
    - `select` persists id, `select` bumps `touchedAt`, `reset` clears.

16. **Playwright canary** (`tools/Typhon.Workbench/ClientApp/e2e/<moduleKebab>.spec.ts`)
    - One test: open demo session, View → `<PanelName>`, assert a unique placeholder or title substring is visible.
    - Copy the `openDemo(page, request)` helper from `e2e/schema-inspector.spec.ts` — Playwright specs don't share modules yet.

## Verification (gate before reporting success)

Run these sequentially; fail early and report which step broke.

```bash
# 1. Server build
dotnet build tools/Typhon.Workbench/Typhon.Workbench.csproj -c Debug --nologo 2>&1 | tail -5

# 2. Server tests (if new-module path)
dotnet test test/Typhon.Workbench.Tests/Typhon.Workbench.Tests.csproj --filter "FullyQualifiedName~<ModuleName>" --nologo --no-build 2>&1 | tail -3

# 3. Client typecheck
cd tools/Typhon.Workbench/ClientApp && npx tsc --noEmit 2>&1 | tail -5

# 4. Client lint
npm run lint 2>&1 | tail -3

# 5. Client vitest (store test)
npm test -- --run src/stores/__tests__/use<ModuleName>Store.test.ts 2>&1 | tail -5
```

Do **not** run Playwright in verification — that requires Kestrel + Vite running, which may not be the case. Instead, print a reminder: "run `npx playwright test e2e/<moduleKebab>.spec.ts` once dev servers are up".

## Output

Report to the user:
- Files created (absolute paths)
- Files edited (DockHost.tsx, MenuBar.tsx, baseCommands.ts, ServiceExtensions.cs if new-module)
- Verification results (pass / fail per step)
- **Next steps markdown** inline in the chat, listing:
  - Where to fill in the real service body (file + method)
  - `npm run generate-types` as the swap point from `customFetch` → Orval generated hook
  - `npx playwright test e2e/<moduleKebab>.spec.ts` to confirm the canary
  - Suggested follow-ups: enrich DTO, flesh out context menu actions, add sibling panels, add e2e coverage for the real features

## Notes

- Prefer `select-none` (default for the app) — the panel shell inherits. If any part of the new panel should be copyable, add `select-text` on that specific region.
- Use `StatusBadge` (not raw Badge + ad-hoc colors) for any status chip — see `components/ui/status-badge.tsx`.
- If the panel includes a canvas, follow `SchemaLayoutRenderer` theme-token pattern — read CSS variables, listen for `.dark` class changes via MutationObserver.
- If the panel has a table, row-height convention is 22 px (see `panels/CLAUDE.md`).
- Do not create barrel `index.ts` files — import directly (see `src/CLAUDE.md`).
