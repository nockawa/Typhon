#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Stops the Typhon Observability Stack

.DESCRIPTION
    This script stops and removes all containers from the observability stack.
    Data is not persisted between runs (by design for development).

.PARAMETER Full
    Use -Full to stop the full stack (if it was started with -Full).

.PARAMETER RemoveVolumes
    Use -RemoveVolumes to also remove any named volumes.

.EXAMPLE
    .\stop.ps1
    Stops the minimal stack

.EXAMPLE
    .\stop.ps1 -RemoveVolumes
    Stops the stack and removes volumes
#>

param(
    [switch]$Full,
    [switch]$RemoveVolumes
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Stack configuration
$ComposeFile = if ($Full) { "compose.full.yaml" } else { "compose.yaml" }
$StackName = if ($Full) { "Full (PLJG)" } else { "Minimal (Jaeger + Grafana)" }

Write-Host ""
Write-Host "Stopping Typhon Observability Stack [$StackName]..." -ForegroundColor Cyan
Write-Host ""

# Check Podman is installed
if (-not (Get-Command podman -ErrorAction SilentlyContinue)) {
    Write-Host "Error: Podman not found." -ForegroundColor Red
    exit 1
}

# Stop the stack
Push-Location $ScriptDir
try {
    $downArgs = @("-f", $ComposeFile, "down")
    if ($RemoveVolumes) {
        $downArgs += "-v"
    }

    podman compose @downArgs

    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "Warning: Some containers may not have stopped cleanly." -ForegroundColor Yellow
    }
    else {
        Write-Host ""
        Write-Host "Stack stopped successfully." -ForegroundColor Green
    }

    Write-Host ""
    Write-Host "To restart: .\start.ps1" -ForegroundColor Gray
    Write-Host ""
}
finally {
    Pop-Location
}
