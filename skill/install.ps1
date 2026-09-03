<#
    Copies the skill to ~/.claude/skills/vibey-reports. No build, no registration.

    Everything the skill needs is in the folder being copied: SKILL.md, the entry point and the
    two modules. The only external requirement is Crystal Reports XI R2, which cannot be copied
    and is checked for below.
#>
[CmdletBinding()]
param([string]$Destination = (Join-Path $HOME '.claude\skills'))

$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'vibey-reports'
if (-not (Test-Path -LiteralPath $source)) { throw "Skill folder not found: $source" }
foreach ($required in 'SKILL.md', 'scripts\vibey.ps1', 'scripts\VibeyCrystal.psm1', 'scripts\VibeyValidate.psm1') {
    if (-not (Test-Path -LiteralPath (Join-Path $source $required))) {
        throw "Skill folder is incomplete - missing $required in $source"
    }
}

# A warning, not an error: installing on a machine that has not had Crystal installed yet is a
# reasonable thing to do, and the failure it causes later is unmistakable.
$crystal = 'C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2'
if (-not (Test-Path -LiteralPath $crystal)) {
    Write-Warning "Crystal Reports XI R2 was not found at $crystal. The skill installs, but every command will fail until Crystal is installed."
}

$target = Join-Path $Destination 'vibey-reports'
if (-not (Test-Path -LiteralPath $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }

# Replace rather than merge: a stale script left behind by an older version would still be found
# by the module import and is harder to diagnose than a missing one.
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
Copy-Item -LiteralPath $source -Destination $target -Recurse

Write-Host "Installed to $target" -ForegroundColor Green
Write-Host 'Verify with:'
Write-Host ("  echo '{0}' | powershell -NoProfile -File ""{1}""" -f
            '{"command":"read","reportPath":"<a .rpt>"}',
            (Join-Path $target 'scripts\vibey.ps1'))
