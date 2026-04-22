# hooks/ — conventions

- Window/document listeners must be cleaned up in `useEffect` return
- Hooks that need DOM use `@vitest-environment jsdom` directive — don't change global vitest config
- Prefer pure function factories (e.g., `createShiftShiftHandler`) for testability without `renderHook`
- `useKeyboardShortcuts` is called once at Shell level — don't call it in panels or sub-components
