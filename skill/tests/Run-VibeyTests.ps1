# Dependency-free test harness. Pester 5 is not installed on this machine and Pester 3.4's
# syntax diverges sharply, so the skill carries its own runner and needs nothing installed.
[CmdletBinding()]
param([string]$Filter = '*')

$script:Pass = 0
$script:Fail = 0
$script:Failures = @()

function It([string]$Name, [scriptblock]$Body) {
    try   { & $Body; $script:Pass++; Write-Host "  PASS  $Name" -ForegroundColor DarkGreen }
    catch { $script:Fail++; $script:Failures += "$Name : $($_.Exception.Message)"
            Write-Host "  FAIL  $Name" -ForegroundColor Red
            Write-Host "        $($_.Exception.Message)" -ForegroundColor DarkGray }
}
function Should-Be($Actual, $Expected, [string]$What = 'value') {
    if ($Actual -ne $Expected) { throw "$What expected [$Expected] but was [$Actual]" }
}
function Should-Contain([string]$Actual, [string]$Needle) {
    if ($null -eq $Actual -or $Actual -notlike "*$Needle*") {
        throw "expected text containing [$Needle] but was [$Actual]"
    }
}
function Should-Throw([scriptblock]$Body, [string]$Needle) {
    $threw = $false
    try { & $Body } catch { $threw = $true
        if ($Needle -and $_.Exception.Message -notlike "*$Needle*") {
            throw "threw, but message [$($_.Exception.Message)] lacks [$Needle]" } }
    if (-not $threw) { throw 'expected an exception, none thrown' }
}

$files = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.Tests.ps1' | Where-Object { $_.Name -like $Filter })
if ($files.Count -eq 0) { Write-Host "No test files matched filter '$Filter'" -ForegroundColor Red; exit 2 }

foreach ($f in $files) {
    Write-Host "`n$($f.Name)" -ForegroundColor Cyan
    . $f.FullName
}

Write-Host "`n$script:Pass passed, $script:Fail failed" -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })
if ($script:Fail) { exit 1 }
