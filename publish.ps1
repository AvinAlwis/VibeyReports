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
    [string]$OutputDir = "$PSScriptRoot\dist"
)

$ErrorActionPreference = 'Stop'

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
