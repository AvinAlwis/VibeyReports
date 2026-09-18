<#
    Replaces the whole text of named text objects in a report, in place.

        set-text.ps1 -ReportPath <path.rpt> -MapFile <map.json> [-WhatIf]

    map.json is UTF-8: { "ObjectName": "new text", ... }. Object names are looked up in the main
    report only. The text comes from a file rather than the command line because Windows
    PowerShell 5.1 re-encodes arguments and pipes with the console code page, which mangles
    anything outside ASCII (an em dash, a tick).

    WHY THIS IS A SEPARATE SCRIPT AND NOT A LAYOUT OPERATION
    --------------------------------------------------------
    The worker can add a text object but has no operation that rewrites an existing one's words.
    Deleting and re-adding would lose the object's font, colour and name. Here the text element
    is edited on a clone and handed to ReportObjectController.Modify, so everything else about
    the object is kept.

    An object whose text is split across more than one element (mixed formatting) is refused
    rather than flattened.
#>
param(
    [Parameter(Mandatory = $true)][string]$ReportPath,
    [Parameter(Mandatory = $true)][string]$MapFile,
    [switch]$WhatIf
)

$ErrorActionPreference = 'Stop'

# The Crystal assemblies are x86 and will not load in a 64-bit host.
if ([Environment]::Is64BitProcess) {
    $x86 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
              '-ReportPath', $ReportPath, '-MapFile', $MapFile)
    if ($WhatIf) { $argv += '-WhatIf' }
    & $x86 @argv
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

$map = [IO.File]::ReadAllText($MapFile, [Text.Encoding]::UTF8) | ConvertFrom-Json
$wanted = @{}
foreach ($p in $map.PSObject.Properties) { $wanted[$p.Name] = [string]$p.Value }

$rd = New-Object CrystalDecisions.CrystalReports.Engine.ReportDocument
$rd.Load($ReportPath)
$tmpPath = $null
try {
    $doc = $rd.ReportClientDocument
    Write-Output $ReportPath

    # Collect first, so a missing or unsuitable object fails the run before anything is modified.
    $edits = @()
    foreach ($area in $doc.ReportDefinition.Areas) {
        foreach ($section in $area.Sections) {
            foreach ($obj in @($section.ReportObjects)) {
                if (-not $wanted.ContainsKey($obj.Name)) { continue }
                $elements = @()
                foreach ($p in $obj.Paragraphs) { foreach ($e in $p.ParagraphElements) { $elements += $e } }
                if ($elements.Count -ne 1) {
                    throw "$($obj.Name) holds $($elements.Count) text elements; refusing to flatten mixed formatting."
                }
                $edits += [pscustomobject]@{ Obj = $obj; Before = $elements[0].Text; After = $wanted[$obj.Name] }
            }
        }
    }
    $missing = @($wanted.Keys | Where-Object { $_ -notin @($edits | ForEach-Object { $_.Obj.Name }) })
    if ($missing.Count) { throw "Not found in the main report: $($missing -join ', ')" }

    foreach ($ed in $edits) {
        Write-Output ("  {0}`n    was: {1}`n    now: {2}" -f $ed.Obj.Name, $ed.Before, $ed.After)
        if ($WhatIf) { continue }
        # RAS objects cannot be mutated in place: clone, change the clone, Modify.
        $clone = $ed.Obj.Clone($true)
        foreach ($p in $clone.Paragraphs) { foreach ($e in $p.ParagraphElements) { $e.Text = $ed.After } }
        $doc.ReportDefController.ReportObjectController.Modify($ed.Obj, $clone)
    }
    if ($WhatIf) { Write-Output '  (WhatIf - nothing saved)'; return }

    # Save beside the original and move over it: SaveAs onto the open file fails, and a save
    # that dies partway must not leave a corrupt file at the destination.
    $dirName = Split-Path $ReportPath -Parent
    $tmpName = '_set_text_tmp.rpt'
    $tmpPath = Join-Path $dirName $tmpName
    if (Test-Path -LiteralPath $tmpPath) { Remove-Item -LiteralPath $tmpPath -Force }
    $doc.SaveAs($tmpName, $dirName, 0)
}
finally {
    $rd.Close()
}
Move-Item -LiteralPath $tmpPath -Destination $ReportPath -Force
Write-Output '  saved'
