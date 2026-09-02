<#
    One JSON request on stdin, one JSON response on stdout.

    Relaunches itself under 32-bit PowerShell when started 64-bit: the Crystal assemblies are
    x86 and will not load in a 64-bit host. The request is handed to the child through a temp
    FILE rather than a pipe, because stdin plumbing across a relaunch is fragile and silently
    truncates on some hosts.
#>
[CmdletBinding()]
param([string]$RequestFile)

$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[Console]::OutputEncoding = $utf8NoBom
[Console]::InputEncoding  = $utf8NoBom

function Write-VibeyResponse($Object) {
    [Console]::Out.Write(($Object | ConvertTo-Json -Depth 24 -Compress))
}

$raw = if ($RequestFile) { [IO.File]::ReadAllText($RequestFile) } else { [Console]::In.ReadToEnd() }

if ([Environment]::Is64BitProcess) {
    $x86 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $x86)) {
        Write-VibeyResponse @{ ok = $false; error = "32-bit PowerShell not found at $x86. The Crystal assemblies are x86 and cannot load in a 64-bit host." }
        exit 1
    }
    $tmp = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($tmp, $raw, $utf8NoBom)
        & $x86 -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -RequestFile $tmp
        exit $LASTEXITCODE
    } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

try {
    try { $request = $raw | ConvertFrom-Json }
    catch { Write-VibeyResponse @{ ok = $false; error = "Request is not valid JSON: $($_.Exception.Message)" }; exit 1 }

    Import-Module (Join-Path $PSScriptRoot 'VibeyValidate.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'VibeyCrystal.psm1')  -Force

    switch ($request.command) {
        'read'  { Write-VibeyResponse (Invoke-VibeyRead  -Request $request) }
        'apply' { Write-VibeyResponse (Invoke-VibeyApply -Request $request) }
        default {
            Write-VibeyResponse @{ ok = $false
                error = "`"$($request.command)`" is not a supported command. Supported: read, apply." }
            exit 1
        }
    }
} catch {
    Write-VibeyResponse @{ ok = $false; error = $_.Exception.Message }
    exit 1
}
