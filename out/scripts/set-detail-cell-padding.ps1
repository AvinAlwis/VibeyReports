<#
    Adds horizontal text padding to the Goal Alignment detail sub-report's data cells.

    WHY THIS IS A SEPARATE SCRIPT AND NOT A LAYOUT OPERATION
    --------------------------------------------------------
    The detail row's fields deliberately span their whole cell, because the table's ruling comes
    from each cell's own border rather than from drawn lines - a Line has no can-grow, so when the
    Cascading Objective Hierarchy column grows the drawn dividers stay behind at the original row
    height and the text runs past them (see gen-detail-subreport.ps1 and docs/briefs/borders.md).

    A full-width field has no margin, so every value sits hard against its own left divider and the
    numeric column sits hard against its right one. Insetting the field geometry would fix the text
    and break the ruling, which is the trade the border approach existed to avoid.

    Crystal's real answer is paragraph indentation, which moves the TEXT inside the field without
    moving the field:

        ISCRStringFieldFormat.IndentAndSpacingFormat.LeftIndent  / .RightIndent   (twips)

    MEASURED: that property exists on StringFormat only. NumericFormat has no indentation at all,
    which is why Goal ID is centred in gen-detail-subreport.ps1 instead - centring is the only
    padding a numeric field can have.

    The worker exposes no operation for this, so it is done here against the RAS SDK directly.
    It should become a `setIndent` operation; until then this runs as a pass over the saved file.

    ORDER
    -----
        1. gen-detail-subreport.ps1  -> plan  -> worker  -> PMSV10_GoalAlignDetail.rpt
        2. THIS SCRIPT                                   -> same file, padded
        3. gen-goal-align-variants.ps1                   -> re-imports it into both variants
#>
param(
    [string]$ReportPath = 'D:\VibeyReports\out\archive\reports\PMSV10_GoalAlignDetail.rpt',
    [int]$LeftIndentTwips  = 90,
    [int]$RightIndentTwips = 90
)

$ErrorActionPreference = 'Stop'

# The Crystal assemblies are x86 and will not load in a 64-bit host.
if ([Environment]::Is64BitProcess) {
    $x86 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    & $x86 -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath `
        -ReportPath $ReportPath -LeftIndentTwips $LeftIndentTwips -RightIndentTwips $RightIndentTwips
    exit $LASTEXITCODE
}

$dir = 'C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2'
foreach ($d in 'CrystalDecisions.CrystalReports.Engine.dll',
               'CrystalDecisions.Shared.dll',
               'CrystalDecisions.ReportAppServer.ClientDoc.dll',
               'CrystalDecisions.ReportAppServer.ReportDefModel.dll',
               'CrystalDecisions.ReportAppServer.Controllers.dll') {
    Add-Type -Path (Join-Path $dir $d) | Out-Null
}

$rd = New-Object CrystalDecisions.CrystalReports.Engine.ReportDocument
$rd.Load($ReportPath)
$doc = $rd.ReportClientDocument

$padded = 0
$skipped = @()
foreach ($area in $doc.ReportDefinition.Areas) {
    foreach ($section in $area.Sections) {
        if ($section.Name -ne 'DetailSection1') { continue }
        foreach ($obj in @($section.ReportObjects)) {

            $sf = $null
            try { $sf = $obj.FieldFormat.StringFormat } catch { }
            if ($null -eq $sf -or $null -eq $sf.IndentAndSpacingFormat) {
                # A numeric field has no StringFormat: Goal ID is centred instead.
                $skipped += $obj.Name
                continue
            }

            # RAS objects cannot be mutated in place (sdk-notes.md): clone, change the clone, and
            # hand both to Modify. Reading is fine on the original; writing is not.
            $clone = $obj.Clone($true)
            $clone.FieldFormat.StringFormat.IndentAndSpacingFormat.LeftIndent  = $LeftIndentTwips
            $clone.FieldFormat.StringFormat.IndentAndSpacingFormat.RightIndent = $RightIndentTwips
            $doc.ReportDefController.ReportObjectController.Modify($obj, $clone)
            $padded++
        }
    }
}

$dirName  = Split-Path $ReportPath -Parent
$fileName = Split-Path $ReportPath -Leaf
$tmpName  = "_padding_tmp.rpt"
$tmpPath  = Join-Path $dirName $tmpName

# Save beside the original and move over it: SaveAs onto the file currently open fails, and a
# save that dies partway must not leave a corrupt file at the destination.
if (Test-Path -LiteralPath $tmpPath) { Remove-Item -LiteralPath $tmpPath -Force }
$doc.SaveAs($tmpName, $dirName, 0)
$rd.Close()
Move-Item -LiteralPath $tmpPath -Destination $ReportPath -Force

Write-Output ("padded  : {0} field(s) with left/right indent {1}/{2} twips" -f $padded, $LeftIndentTwips, $RightIndentTwips)
if ($skipped.Count) {
    Write-Output ("skipped : {0}  (no StringFormat - numeric fields carry no indentation)" -f ($skipped -join ', '))
}
Write-Output ("saved   : {0}" -f $ReportPath)
