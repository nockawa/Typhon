# stores/ — conventions

- One concern per store file — keep slices thin (state + actions, no derived async)
- Server/async state belongs in TanStack Query, not Zustand
- Persisted stores use `safeStorage` wrapper (see `useThemeStore.ts`) — handles node/test environments
- Actions are plain `set()` calls — no middleware side effects except in `ThemeProvider`
- Store tests live in `__tests__/` alongside the stores and use Vitest (no DOM needed)
