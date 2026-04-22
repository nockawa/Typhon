#!/usr/bin/env bash
# Typhon Workbench — fresh-clone bootstrap.
#
# Runs the full Phase 0 toolchain end-to-end: installs JS deps, restores NuGet packages, builds the client bundle and the .NET host.
# After this exits 0 you can `dotnet run --project tools/Typhon.Workbench` (port :5200) and `npm run dev` inside
# tools/Typhon.Workbench/ClientApp (port :5173) and hit "Hello Workbench" in the browser.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"
WORKBENCH_DIR="$REPO_ROOT/tools/Typhon.Workbench"
CLIENT_DIR="$WORKBENCH_DIR/ClientApp"

echo "==> Typhon Workbench bootstrap"
echo "    Repo root: $REPO_ROOT"
echo "    Workbench: $WORKBENCH_DIR"

if [[ ! -d "$CLIENT_DIR" ]]; then
  echo "    ERROR: ClientApp directory not found at $CLIENT_DIR" >&2
  exit 1
fi

echo "==> [1/3] Installing JS dependencies (npm install)"
(cd "$CLIENT_DIR" && npm install --no-audit --no-fund)

echo "==> [2/3] Restoring NuGet packages (dotnet restore)"
(cd "$REPO_ROOT" && dotnet restore Typhon.slnx)

echo "==> [3/3] Building the solution (dotnet build)"
(cd "$REPO_ROOT" && dotnet build Typhon.slnx -c Debug --no-restore)

echo ""
echo "==> Bootstrap complete."
echo "    Next steps:"
echo "      Back end : dotnet run --project tools/Typhon.Workbench"
echo "      Front end: (cd tools/Typhon.Workbench/ClientApp && npm run dev)"
echo "      Browser  : http://localhost:5173"
