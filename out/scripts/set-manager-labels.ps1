<#
    Relabels "Appraiser" as "Manager" in a report's text objects, main report and embedded
    sub-reports alike, in place.

        set-manager-labels.ps1 -ReportPath <path.rpt> [-WhatIf]

    WHY THIS IS A SEPARATE SCRIPT AND NOT A LAYOUT OPERATION
    --------------------------------------------------------
    The worker has no operation that changes an existing text object's words, and none that
    reaches inside an embedded sub-report. RAS does both: each paragraph's text elements are
    editable on a clone handed to ReportObjectController.Modify, and
    SubreportController.GetSubreport(name) returns a document with its own ReportDefController
    (reached through a small C# cast - see below).

    Case is preserved per match: "Appraiser" -> "Manager", "APPRAISER" -> "MANAGER". Only the
    word Appraiser is touched - "Appraisee" does not match.
#>
param(
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# The Crystal assemblies are x86 and will not load in a 64-bit host.
if ([Environment]::Is64BitProcess) {
    $x86 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-ReportPath', $ReportPath)
    if ($WhatIf) { $argv += '-WhatIf' }
    & $x86 @argv
    exit $LASTEXITCODE
}

$dir = 'C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2'
$refs = 'CrystalDecisions.CrystalReports.Engine.dll',
        'CrystalDecisions.Shared.dll',
        'CrystalDecisions.ReportAppServer.ClientDoc.dll',
        'CrystalDecisions.ReportAppServer.ReportDefModel.dll',
        'CrystalDecisions.ReportAppServer.Controllers.dll' | ForEach-Object { Join-Path $dir $_ }
foreach ($r in $refs) { Add-Type -Path $r | Out-Null }

# MEASURED: GetSubreport's result is a bare __ComObject to PowerShell - no properties, and
# InvokeMember fails too. Cast in C# to ISCRSubreportClientDocument and hand back its
# ReportDefController (typed), whose ReportDefinition PowerShell can then walk. The interface
# has no ReportDefinition of its own.
Add-Type -ReferencedAssemblies $refs -TypeDefinition @'
using CrystalDecisions.ReportAppServer.ClientDoc;
using CrystalDecisions.ReportAppServer.Controllers;
public static class VibeySubreportAccess {
    public static ReportDefController2 DefController(ISCDReportClientDocument doc, string name) {
        return ((ISCRSubreportClientDocument)doc.SubreportController.GetSubreport(name)).ReportDefController;
    }
}
'@

$pattern = '\bAppraiser\b'
function Convert-Label([string]$s) {
    [regex]::Replace($s, $pattern, {
        param($m)
        if ($m.Value -ceq $m.Value.ToUpperInvariant()) { 'MANAGER' } else { 'Manager' }
    }, 'IgnoreCase')
}

# Walks one document's text objects; $defController is where Modify has to go.
function Update-Labels($definition, $defController, [string]$where) {
    $changed = 0
    foreach ($area in $definition.Areas) {
        foreach ($section in $area.Sections) {
            foreach ($obj in @($section.ReportObjects)) {
                $paras = $null
                try { $paras = $obj.Paragraphs } catch { }
                if ($null -eq $paras) { continue }

                $hit = $false
                foreach ($p in $paras) {
                    foreach ($e in $p.ParagraphElements) {
                        try { if ($e.Text -match $pattern) { $hit = $true } } catch { }
                    }
                }
                if (-not $hit) { continue }

                # RAS objects cannot be mutated in place: clone, change the clone, Modify.
                $clone = $obj.Clone($true)
                $before = @(); $after = @()
                foreach ($p in $clone.Paragraphs) {
                    foreach ($e in $p.ParagraphElements) {
                        $t = $null
                        try { $t = $e.Text } catch { }
                        if ($null -eq $t -or $t -notmatch $pattern) { continue }
                        $before += $t
                        $e.Text = Convert-Label $t
                        $after += $e.Text
                    }
                }
                Write-Host ("  {0,-14} {1,-8} '{2}' -> '{3}'" -f $where, $obj.Name, ($before -join ''), ($after -join ''))
                if (-not $WhatIf) { $defController.ReportObjectController.Modify($obj, $clone) }
                $changed++
            }
        }
    }
    return $changed
}

$rd = New-Object CrystalDecisions.CrystalReports.Engine.ReportDocument
$rd.Load($ReportPath)
try {
    $doc = $rd.ReportClientDocument
    Write-Output $ReportPath
    $total = Update-Labels $doc.ReportDefinition $doc.ReportDefController 'main'

    foreach ($name in @($doc.SubreportController.GetSubreportNames())) {
        $ctl = [VibeySubreportAccess]::DefController($doc, $name)
        $total += Update-Labels $ctl.ReportDefinition $ctl ("sub:" + $name)
    }

    if ($WhatIf -or $total -eq 0) {
        Write-Output ("  {0} object(s) would change; nothing saved" -f $total)
        return
    }

    # Save beside the original and move over it: SaveAs onto the open file fails, and a save
    # that dies partway must not leave a corrupt file at the destination.
    $dirName = Split-Path $ReportPath -Parent
    $tmpName = '_manager_labels_tmp.rpt'
    $tmpPath = Join-Path $dirName $tmpName
    if (Test-Path -LiteralPath $tmpPath) { Remove-Item -LiteralPath $tmpPath -Force }
    $doc.SaveAs($tmpName, $dirName, 0)
}
finally {
    $rd.Close()
}
Move-Item -LiteralPath $tmpPath -Destination $ReportPath -Force
Write-Output ("  {0} object(s) relabelled; saved" -f $total)
