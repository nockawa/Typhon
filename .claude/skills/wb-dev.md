---
name: wb-dev
description: Start the Typhon Workbench dev servers (ASP.NET Core :5200 + Vite :5173) as background processes and tail their logs
---

# wb-dev — Start Workbench Dev Servers

Starts both the ASP.NET Core backend and the Vite SPA dev server in the background and streams their logs to the conversation.

## What it does

1. Starts `dotnet watch --project tools/Typhon.Workbench` (Kestrel on :5200)
2. Starts `npm run dev` inside `tools/Typhon.Workbench/ClientApp/` (Vite on :5173)
3. Tails both logs inline so you can see startup errors immediately

## Usage

```
/wb-dev
```

No arguments. If either port is already in use, the process will fail with an address-in-use error — kill the existing process first.

## Implementation

Run both commands as background processes using the Bash tool with `run_in_background: true`:

```bash
# Terminal 1 — ASP.NET Core backend
cd /c/Dev/github/Typhon
dotnet watch --project tools/Typhon.Workbench
```

```bash
# Terminal 2 — Vite SPA
cd /c/Dev/github/Typhon/tools/Typhon.Workbench/ClientApp
npm run dev
```

After starting both, report:
- Backend: http://localhost:5200/health
- Frontend: http://localhost:5173
- API docs: http://localhost:5200/openapi.json

## Notes

- Vite proxies `/api`, `/openapi.json`, `/swagger`, `/health` to `:5200` — only open `:5173` in the browser
- `dotnet watch` hot-reloads C# changes; Vite HMR handles TypeScript/CSS changes
- Profiler.Server runs on `:5100` — kept free, no conflict
