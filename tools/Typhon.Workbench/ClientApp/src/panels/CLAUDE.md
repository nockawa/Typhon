# panels/ — conventions

- Each panel is a self-contained React component registered in `DockHost.tsx`'s `components` map
- Panel props come from `IDockviewPanelProps` (dockview-react) — access panel params via `props.params`
- Row height is **22px** (`rowHeight={22}`) for all list/tree panels — matches `--density-row-height`
- Use `useResourceGraphStore` for resource tree selection; create per-panel stores for panel-local state
- Panels must fill their container: `<div className="h-full w-full overflow-hidden">`
