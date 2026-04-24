---
name: wb-dev
description: Start / stop / status of the Typhon Workbench dev servers (Kestrel :5200 + Vite :5173) with file-based PID tracking so state survives across Claude Code sessions
argument-hint: start | stop | status | restart
---

# wb-dev — Workbench Dev Server Controller

Launches and manages the two Workbench dev servers with a persistent PID file at `.claude/state/wb-dev.json`, so a new Claude Code session (or a later slash-command invocation) can still stop servers it didn't start itself.

## Input

`$ARGUMENTS` first token determines the action:

- `start` (default when no args) — launches both servers, waits for binding, writes PID file, tails startup output
- `stop` — reads the PID file, kills each process tree, deletes the file
- `status` — reads the PID file, checks each PID for liveness, reports
- `restart` — `stop` then `start`
- `--help` / `-h` — show the help block below and stop

```
/wb-dev [start | stop | status | restart]

  Manage the Typhon Workbench dev servers with file-based PID tracking.

Actions:
  start       Launch Kestrel (:5200) + Vite (:5173) in background, save PIDs
  stop        Kill the process trees recorded in the PID file, clear the file
  status      Report each process as alive | dead | no-state
  restart     Stop then start
  --help, -h  Show this help

State file:
  .claude/state/wb-dev.json — { kestrelRootPid, kestrelListenerPid,
                                vitePid, viteListenerPid, startedAt }

Examples:
  /wb-dev             (shorthand for /wb-dev start)
  /wb-dev start
  /wb-dev stop
  /wb-dev status
  /wb-dev restart
```

## State file shape

`/c/Dev/github/Typhon/.claude/state/wb-dev.json`:

```json
{
  "kestrelRootPid": 111,
  "kestrelListenerPid": 222,
  "vitePid": 333,
  "viteListenerPid": 444,
  "startedAt": "2026-04-23T10:42:15+02:00"
}
```

**Root vs listener**: `dotnet watch` spawns `Typhon.Workbench.exe` as a child (the listener on :5200). Killing only the listener would cause watch to respawn it — so we also track the `dotnet watch` process itself (`kestrelRootPid`) and `taskkill /T` it at stop time. `npm run dev` similarly spawns vite.

The `.claude/state/` directory is gitignored.

## Discovery strategy

Identifying PIDs by capturing `$!` in a backgrounded bash shell is racy (the bash shell's own PID vs. the command's PID, shell exiting before the echo lands) and fragile across OS / shell variants. Instead:

1. Launch the two servers in the background with `run_in_background: true` — fire-and-forget, no PID capture.
2. Poll `netstat -ano` until both :5200 and :5173 are `LISTENING` (timeout 30 s).
3. For each listener PID, use **PowerShell `Get-CimInstance Win32_Process`** filtered by command-line to find the root `dotnet watch` / `npm run dev` process. This is more robust than parent-walking because the relevant processes are uniquely identifiable by their command line:
   - Kestrel root: `dotnet.exe` whose command-line contains `watch` and `Typhon.Workbench`
   - Vite root: `node.exe` whose command-line contains `vite` (spawned by `npm run dev`)
4. Write JSON with both root PIDs and listener PIDs.

## Workflows

### `start`

**Pre-flight.** Check the state file.
- If present and all recorded PIDs alive → print "already running" + endpoints + **stop** (don't relaunch).
- If present but any recorded PID is dead → print "stale state", delete the file, continue.
- If absent → continue.

**Port pre-flight.**
```bash
netstat -ano | grep -E ':(5200|5173)\s' | grep LISTENING || true
```
If either port is LISTENING → warn the user, list the owning PIDs, **stop** (ask them to `/wb-dev stop` or kill manually first).

**Launch.** Two separate `run_in_background: true` Bash calls. Neither captures `$!`; both just run the server command in the foreground of their background shell:

Shell 1 (Kestrel):
```bash
cd /c/Dev/github/Typhon
mkdir -p .claude/state
dotnet watch --project tools/Typhon.Workbench > .claude/state/wb-dev.kestrel.log 2>&1
```

Shell 2 (Vite):
```bash
cd /c/Dev/github/Typhon/tools/Typhon.Workbench/ClientApp
npm run dev > /c/Dev/github/Typhon/.claude/state/wb-dev.vite.log 2>&1
```

**Wait for binding.** Poll every 2 s up to 30 s total:
```bash
K=""; V=""
for i in $(seq 1 15); do
  K=$(netstat -ano | grep -E ':5200\s' | grep LISTENING | awk '{print $NF}' | head -1)
  V=$(netstat -ano | grep -E ':5173\s' | grep LISTENING | awk '{print $NF}' | head -1)
  if [ -n "$K" ] && [ -n "$V" ]; then break; fi
  sleep 2
done
echo "KESTREL_LISTENER=$K VITE_LISTENER=$V"
```
If the loop exits without both PIDs → tail the two log files (`.claude/state/wb-dev.*.log`), report "failed to bind within 30 s", stop.

**Resolve root PIDs via PowerShell.**
```powershell
$k = Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
     Where-Object { $_.CommandLine -match 'watch' -and $_.CommandLine -match 'Typhon\.Workbench' } |
     Select-Object -First 1 -ExpandProperty ProcessId
$v = Get-CimInstance Win32_Process -Filter "Name='node.exe'" |
     Where-Object { $_.CommandLine -match 'npm.*run.*dev' -or $_.CommandLine -match 'vite' } |
     Select-Object -First 1 -ExpandProperty ProcessId
"$k $v"
```
On POSIX, fall back to `pgrep -f 'dotnet watch.*Typhon.Workbench'` and `pgrep -f 'vite'`.

**Write the JSON.**
```bash
python3 -c "
import json, datetime
s = {
    'kestrelRootPid':     $K_ROOT,
    'kestrelListenerPid': $K_LISTENER,
    'vitePid':            $V_ROOT,
    'viteListenerPid':    $V_LISTENER,
    'startedAt':          datetime.datetime.now().astimezone().isoformat(),
}
open('.claude/state/wb-dev.json', 'w').write(json.dumps(s, indent=2) + '\n')
print(json.dumps(s))
"
```

**Tail ~15 lines of each log** so the user sees the binding message / any compile errors.

**Report:**
- Backend: http://localhost:5200/health
- Frontend: http://localhost:5173
- API docs: http://localhost:5200/openapi.json
- Logs: `.claude/state/wb-dev.kestrel.log`, `.claude/state/wb-dev.vite.log`
- PIDs: from the JSON (root + listener)

### `stop`

1. Read `.claude/state/wb-dev.json`. If missing → "no wb-dev state file; nothing to stop". Also run `netstat` to note anything actually bound to :5200 / :5173 that a prior session may have orphaned.

2. Tree-kill each **root** PID — `/T` cascades to children (the listener is always a child):
   ```bash
   taskkill //F //T //PID <kestrelRootPid> 2>/dev/null || kill -TERM <kestrelRootPid> 2>/dev/null || true
   taskkill //F //T //PID <vitePid>        2>/dev/null || kill -TERM <vitePid>        2>/dev/null || true
   ```

3. Delete `.claude/state/wb-dev.json`. Leave `.log` files for post-mortem.

4. Verify with `netstat` — if either port is still bound, report the surviving PID and suggest a manual `taskkill //F //PID <pid>`. In that case *do not* recreate the state file — the user will want to triage first.

5. Report: "stopped Kestrel (root pid N, listener M) + Vite (root pid X, listener Y)".

### `status`

1. Read `.claude/state/wb-dev.json`. If missing → "no state file; servers not tracked".
2. For each PID (root + listener) use `tasklist //FI "PID eq <pid>" 2>NUL | grep -q "<pid>"` to test liveness. On POSIX fall back to `kill -0 <pid>`.
3. Report a table:
   ```
   Kestrel  root      (pid 111)  alive   since 2026-04-23T10:42:15+02:00
   Kestrel  listener  (pid 222)  alive   bound :5200
   Vite     root      (pid 333)  alive
   Vite     listener  (pid 444)  alive   bound :5173
   ```
4. Cross-check with `netstat` — if :5200 or :5173 is bound to a **different** PID than the recorded listener, flag state-file staleness (probably a reboot or manual kill).

### `restart`

`stop` → `sleep 1` (let ports release) → `start`.

## Notes

- `dotnet watch` hot-reloads C# changes; Vite HMR handles TypeScript/CSS changes.
- Vite proxies `/api`, `/openapi.json`, `/swagger`, `/health` to `:5200` — only open `:5173` in the browser.
- JSON state survives Claude Code session switches.
- **Stale-PID trap after reboot**: PIDs can be reused. `status` cross-checks the listener PID against netstat; `stop` will blindly kill the recorded PIDs, so run `status` first after a reboot, or delete the state file if suspicious.
- `.claude/state/` is in `.gitignore`.
