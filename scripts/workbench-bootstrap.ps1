# Typhon Workbench — fresh-clone bootstrap (PowerShell edition).
#
# Runs the full Phase 0 toolchain end-to-end. See workbench-bootstrap.sh for the bash equivalent — both scripts must stay in
# sync on the ordered steps (install JS deps → restore NuGet → build solution) because either is a valid entry point depending
# on the operator's shell.

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$RepoRoot = Resolve-Path (Join-Path $ScriptDir '..')
$WorkbenchDir = Join-Path $RepoRoot 'tools/Typhon.Workbench'
$ClientDir = Join-Path $WorkbenchDir 'ClientApp'

Write-Host "==> Typhon Workbench bootstrap"
Write-Host "    Repo root: $RepoRoot"
Write-Host "    Workbench: $WorkbenchDir"

if (-not (Test-Path $ClientDir)) {
    Write-Error "ClientApp directory not found at $ClientDir"
    exit 1
}

Write-Host "==> [1/3] Installing JS dependencies (npm install)"
Push-Location $ClientDir
try {
    npm install --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw "npm install failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Write-Host "==> [2/3] Restoring NuGet packages (dotnet restore)"
Push-Location $RepoRoot
try {
    dotnet restore Typhon.slnx
    if ($LASTEXITCODE -ne 0) { throw "dotnet restore failed with exit code $LASTEXITCODE" }

    Write-Host "==> [3/3] Building the solution (dotnet build)"
    dotnet build Typhon.slnx -c Debug --no-restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Write-Host ""
Write-Host "==> Bootstrap complete."
Write-Host "    Next steps:"
Write-Host "      Back end : dotnet run --project tools/Typhon.Workbench"
Write-Host "      Front end: cd tools/Typhon.Workbench/ClientApp; npm run dev"
Write-Host "      Browser  : http://localhost:5173"
