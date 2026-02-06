#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Starts the Typhon Observability Stack (Jaeger + Grafana)

.DESCRIPTION
    This script starts the minimal observability stack for Typhon development.
    It validates Podman is available and the machine is running before starting.

.PARAMETER Full
    Use -Full to start the full stack (Prometheus + Loki + Alertmanager).
    Note: Full stack (compose.full.yaml) is not yet implemented.

.EXAMPLE
    .\start.ps1
    Starts the minimal stack (Jaeger + Grafana)

.EXAMPLE
    .\start.ps1 -Full
    Starts the full stack (when implemented)
#>

param(
    [switch]$Full
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Stack configuration
$ComposeFile = if ($Full) { "compose.full.yaml" } else { "compose.yaml" }
$StackName = if ($Full) { "Full (PLJG)" } else { "Minimal (Jaeger + Grafana)" }

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Typhon Observability Stack" -ForegroundColor Cyan
Write-Host " [$StackName]" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check if full stack file exists
if ($Full -and -not (Test-Path (Join-Path $ScriptDir $ComposeFile))) {
    Write-Host "Error: Full stack (compose.full.yaml) is not yet implemented." -ForegroundColor Red
    Write-Host "Use the minimal stack (without -Full) for now." -ForegroundColor Yellow
    exit 1
}

# Check Podman is installed
Write-Host "Checking Podman installation..." -ForegroundColor Gray
if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
    Write-Host ""
    Write-Host "Error: Podman not found." -ForegroundColor Red
    Write-Host "Please install Podman Desktop from: https://podman-desktop.io/" -ForegroundColor Yellow
    exit 1
}
Write-Host "  Podman found" -ForegroundColor Green

# Check Podman machine is running
Write-Host "Checking Podman machine status..." -ForegroundColor Gray
$machineList = podman machine list --format "{{.Name}},{{.Running}}" 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host ""
    Write-Host "Error: Could not get Podman machine status." -ForegroundColor Red
    Write-Host "Try running: podman machine init" -ForegroundColor Yellow
    exit 1
}

$machineRunning = $false
foreach ($line in $machineList) {
    if ($line -match ",true$") {
        $machineRunning = $true
        break
    }
}

if (-not $machineRunning) {
    Write-Host "  No running machine found. Starting default machine..." -ForegroundColor Yellow
    podman machine start
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "Error: Failed to start Podman machine." -ForegroundColor Red
        Write-Host "Try running: podman machine init --cpus 4 --memory 4096" -ForegroundColor Yellow
        exit 1
    }
}
Write-Host "  Podman machine running" -ForegroundColor Green

# Start the stack
Write-Host ""
Write-Host "Starting containers..." -ForegroundColor Gray
Push-Location $ScriptDir
try {
    podman compose -f $ComposeFile up -d

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "Error: Failed to start containers." -ForegroundColor Red
        exit 1
    }

    # Wait a moment for health checks
    Write-Host "Waiting for services to become healthy..." -ForegroundColor Gray
    Start-Sleep -Seconds 5

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host " Stack Started Successfully!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Access Points:" -ForegroundColor Cyan
    Write-Host "  Grafana:     http://localhost:3000  (admin/typhon)" -ForegroundColor White
    Write-Host "  Jaeger UI:   http://localhost:16686" -ForegroundColor White
    Write-Host ""
    Write-Host "OTLP Endpoints (for your app):" -ForegroundColor Cyan
    Write-Host "  gRPC:        localhost:4317" -ForegroundColor White
    Write-Host "  HTTP:        localhost:4318" -ForegroundColor White

    if ($Full) {
        Write-Host ""
        Write-Host "Additional Services:" -ForegroundColor Cyan
        Write-Host "  Prometheus:  http://localhost:9090" -ForegroundColor White
        Write-Host "  Loki:        http://localhost:3100" -ForegroundColor White
    }

    Write-Host ""
    Write-Host "Commands:" -ForegroundColor Gray
    Write-Host "  Stop stack:   .\stop.ps1" -ForegroundColor Gray
    Write-Host "  View logs:    podman compose logs -f" -ForegroundColor Gray
    Write-Host "  Status:       podman compose ps" -ForegroundColor Gray
    Write-Host ""
}
finally {
    Pop-Location
}
