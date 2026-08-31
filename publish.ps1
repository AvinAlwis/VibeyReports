#Requires -Version 5.1
<#
    Publishes both executables to SEPARATE directories (Task 11, supplement S2):

        dist\VibeyReports.Mcp.exe
        dist\worker\VibeyReports.CrystalWorker.exe

    Both projects reference VibeyReports.Contracts, so publishing them into the same
    directory lets whichever one runs second overwrite files the other needs (most
    notably VibeyReports.Contracts.dll itself, since the net48 worker's dependency
    closure differs from the net10 host's). WorkerLocator already probes
    AppContext.BaseDirectory/worker/ for exactly this layout, so no environment
    variable is required at run time (see WorkerLocator.Find).
#>
[CmdletBinding()]
param(
    [string]$OutputDir
)

$ErrorActionPreference = 'Stop'

# Nesting `powershell.exe -File .\publish.ps1` inside an already-running PowerShell session
# can leave $PSScriptRoot empty (observed during Task 11 verification) — the script would
# then silently resolve its default $OutputDir to the current drive root (e.g. D:\dist)
# instead of this repo's dist\ folder, and every Test-Path check below would still pass
# because the wrong location is a real one. Guard against that rather than trusting
# $PSScriptRoot blindly, and fail loudly if it truly can't be determined.
if (-not $PSScriptRoot) {
    $PSScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if (-not $PSScriptRoot) {
    throw "Could not determine this script's own directory (`$PSScriptRoot and `$MyInvocation.MyCommand.Path were both empty). Invoke it directly, e.g. '& .\publish.ps1', rather than nested inside another powershell.exe process."
}
if (-not $OutputDir) {
    $OutputDir = Join-Path $PSScriptRoot 'dist'
}

$workerOutputDir = Join-Path $OutputDir 'worker'

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir | Out-Null
New-Item -ItemType Directory -Path $workerOutputDir | Out-Null

Write-Host "Publishing MCP server (net10.0) to $OutputDir ..."
dotnet publish "$PSScriptRoot\src\VibeyReports.Mcp" -c Release -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "MCP publish failed." }

Write-Host "Publishing Crystal worker (net48, x86) to $workerOutputDir ..."
# dotnet publish -r win-x86 targets SDK-style RID-aware publish, which .NET Framework
# projects do not support the same way as .NET/.NET Core projects (Task 11 supplement S3).
# The worker's csproj already pins <PlatformTarget>x86</PlatformTarget>, so a plain publish
# produces a genuinely 32-bit binary regardless of RID; verify with `dumpbin` or `corflags`
# after publishing (see README "Verifying the worker is x86").
dotnet publish "$PSScriptRoot\src\VibeyReports.CrystalWorker" -c Release -o $workerOutputDir
if ($LASTEXITCODE -ne 0) { throw "Worker publish failed." }

$mcp    = Join-Path $OutputDir 'VibeyReports.Mcp.exe'
$worker = Join-Path $workerOutputDir 'VibeyReports.CrystalWorker.exe'

if (-not (Test-Path $mcp))    { throw "Missing $mcp" }
if (-not (Test-Path $worker)) { throw "Missing $worker" }

Write-Host ""
Write-Host "Published:"
Write-Host "  MCP server: $mcp"
Write-Host "  Worker:     $worker"
