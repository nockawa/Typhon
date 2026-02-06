#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Interactive launcher for Typhon observability stacks.

.DESCRIPTION
    Lets you pick between the PLJG (Prometheus + Jaeger + Grafana) and
    SigNoz (ClickHouse) observability stacks. Only one stack can run at
    a time since both bind to OTLP port 4317.

.EXAMPLE
    .\select-stack.ps1
    Launches the interactive stack selector.
#>

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host " Typhon Observability Stack Selector" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Check Podman is installed
if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
    Write-Host "Error: Podman not found." -ForegroundColor Red
    Write-Host "Please install Podman Desktop from: https://podman-desktop.io/" -ForegroundColor Yellow
    exit 1
}

# Detect running stacks by checking for known container name prefixes
$containers = podman ps --format "{{.Names}}" 2>$null
$pljgRunning = $false
$signozRunning = $false

if ($containers) {
    foreach ($c in $containers) {
        if ($c -match "^typhon-(otel-collector|jaeger|prometheus|grafana)$") {
            $pljgRunning = $true
        }
        if ($c -match "^typhon-signoz-") {
            $signozRunning = $true
        }
    }
}

if ($pljgRunning) {
    Write-Host "  [Running] PLJG stack detected" -ForegroundColor Green
}
if ($signozRunning) {
    Write-Host "  [Running] SigNoz stack detected" -ForegroundColor Green
}
if (-not $pljgRunning -and -not $signozRunning) {
    Write-Host "  No stack currently running" -ForegroundColor Gray
}

Write-Host ""
Write-Host "Choose a stack:" -ForegroundColor Yellow
Write-Host ""
Write-Host "  [1] PLJG     (Prometheus + Jaeger + Grafana)  ~1 GB RAM" -ForegroundColor White
Write-Host "                3 UIs: Grafana :3000, Jaeger :16686, Prometheus :9090" -ForegroundColor Gray
Write-Host ""
Write-Host "  [2] SigNoz   (ClickHouse + SigNoz UI)         ~4 GB RAM" -ForegroundColor White
Write-Host "                1 UI:  SigNoz :8080" -ForegroundColor Gray
Write-Host ""
Write-Host "  [Q] Quit" -ForegroundColor Gray
Write-Host ""

$choice = Read-Host "Selection"

switch ($choice.ToUpper()) {
    "1" {
        if ($signozRunning) {
            Write-Host ""
            Write-Host "Stopping SigNoz stack first..." -ForegroundColor Yellow
            & (Join-Path $ScriptDir "signoz\stop.ps1")
            Write-Host ""
        }
        & (Join-Path $ScriptDir "pljg\start.ps1")
    }
    "2" {
        if ($pljgRunning) {
            Write-Host ""
            Write-Host "Stopping PLJG stack first..." -ForegroundColor Yellow
            & (Join-Path $ScriptDir "pljg\stop.ps1")
            Write-Host ""
        }
        & (Join-Path $ScriptDir "signoz\start.ps1")
    }
    "Q" {
        Write-Host "Bye!" -ForegroundColor Gray
    }
    default {
        Write-Host "Invalid selection." -ForegroundColor Red
    }
}
