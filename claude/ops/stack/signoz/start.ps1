#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Starts the Typhon SigNoz Observability Stack (ClickHouse + SigNoz UI)

.DESCRIPTION
    This script starts the SigNoz-based observability stack for Typhon development.
    SigNoz provides unified logs, metrics, and traces in a single UI backed by ClickHouse.
    It validates Podman is available and the machine is running before starting.

.EXAMPLE
    .\start.ps1
    Starts the SigNoz stack
#>

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$StackName = "SigNoz (ClickHouse)"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Typhon Observability Stack" -ForegroundColor Cyan
Write-Host " [$StackName]" -ForegroundColor Yellow
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

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
Write-Host "  (SigNoz needs ~4 GB RAM — ensure your Podman machine has enough)" -ForegroundColor Gray
Push-Location $ScriptDir
try {
    podman compose -f compose.yaml up -d

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "Error: Failed to start containers." -ForegroundColor Red
        exit 1
    }

    # SigNoz takes longer to start (ClickHouse + schema migration)
    Write-Host "Waiting for services to become healthy (this may take 30-60s)..." -ForegroundColor Gray
    Start-Sleep -Seconds 15

    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host " Stack Started Successfully!" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "Access Points:" -ForegroundColor Cyan
    Write-Host "  SigNoz UI:   http://localhost:8080" -ForegroundColor White
    Write-Host ""
    Write-Host "OTLP Endpoints (for your app):" -ForegroundColor Cyan
    Write-Host "  gRPC:        localhost:4317" -ForegroundColor White
    Write-Host "  HTTP:        localhost:4318" -ForegroundColor White
    Write-Host ""
    Write-Host "Commands:" -ForegroundColor Gray
    Write-Host "  Stop stack:   .\stop.ps1" -ForegroundColor Gray
    Write-Host "  View logs:    podman compose logs -f" -ForegroundColor Gray
    Write-Host "  Status:       podman compose ps" -ForegroundColor Gray
    Write-Host ""
    Write-Host "Note: Schema migration runs on first start. If SigNoz UI is" -ForegroundColor Gray
    Write-Host "not yet ready, wait 30-60s and try again." -ForegroundColor Gray
    Write-Host ""
}
finally {
    Pop-Location
}
