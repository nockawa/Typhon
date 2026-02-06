#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Stops the Typhon SigNoz Observability Stack

.DESCRIPTION
    This script stops and removes all containers from the SigNoz stack.
    Use -RemoveVolumes to also delete ClickHouse data and SigNoz state.

.PARAMETER RemoveVolumes
    Use -RemoveVolumes to also remove named volumes (ClickHouse data, SQLite, ZooKeeper).

.EXAMPLE
    .\stop.ps1
    Stops the SigNoz stack

.EXAMPLE
    .\stop.ps1 -RemoveVolumes
    Stops the stack and removes all data volumes
#>

param(
    [switch]$RemoveVolumes
)

$ErrorActionPreference = "Stop"
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path

$StackName = "SigNoz (ClickHouse)"

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
    $downArgs = @("-f", "compose.yaml", "down")
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
