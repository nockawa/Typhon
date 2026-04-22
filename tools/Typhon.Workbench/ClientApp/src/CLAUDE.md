# ClientApp/src — conventions

- **Tailwind only** for styling — no inline `style` props except for dynamic values (e.g., computed widths)
- **Path alias `@/`** maps to `src/` — always use it, never relative `../../` imports
- **shadcn components** live in `src/components/ui/` — use them for all chrome
- **Zustand stores** for all cross-component state — no prop drilling beyond one level
- **No barrel `index.ts`** files — import directly from the module file
- **Strict TypeScript** — no `any`, no `@ts-ignore`
