# Turns on can-grow for every sub-report container in the built report.
#
# Input/output: out/reports/PMSV10_IndDetailedEval.rpt  (read, then rewritten)
#
# WHY THIS IS A SEPARATE SCRIPT AND A SEPARATE PLAN
# A sub-report clips its contents at the container height unless the CONTAINER
# can grow, so the FDP comment tables overflowed their frames and collided with
# the band below. Fixing that means setCanGrow on the sub-report objects - but
# Crystal renames a sub-report when it places one, so the validator rejects every
# operation except setSubreportLink on a sub-report added in the SAME plan. The
# names only exist after the report is saved.
#
# So this runs after gen-eval-main.ps1, reads the report back, and targets the
# placed names Crystal actually assigned rather than guessing at "Subreport1".

$ErrorActionPreference = 'Stop'
$worker = 'D:\VibeyReports\dist\worker\VibeyReports.CrystalWorker.exe'
$report = 'D:/VibeyReports/out/reports/PMSV10_IndDetailedEval.rpt'

$read = (@{ command = 'read'; reportPath = $report } | ConvertTo-Json -Compress) | & $worker | ConvertFrom-Json
if (-not $read.ok) { throw "read failed: $($read.error)" }

$ops = @()
foreach ($s in $read.schema.sections) {
    foreach ($o in $s.objects) {
        if ($o.kind -eq 'Subreport') {
            $ops += [pscustomobject]@{ action = 'setCanGrow'; target = $o.name; canGrow = $true }
            Write-Output ("  {0,-14} ({1}) in {2}" -f $o.name, $o.subreportName, $s.name)
        }
    }
}
if ($ops.Count -eq 0) { throw 'no sub-reports found - run gen-eval-main.ps1 first' }

$tmp = 'D:/VibeyReports/out/reports/_grow_tmp.rpt'
$plan = [pscustomobject]@{
    command = 'apply'; reportPath = $report; outputPath = $tmp; overwrite = $true
    plan = [pscustomobject]@{ planVersion = 1; operations = $ops }
}
[IO.File]::WriteAllText('D:\VibeyReports\out\json\eval-grow.json', ($plan | ConvertTo-Json -Depth 12 -Compress))

$res = (Get-Content 'D:\VibeyReports\out\json\eval-grow.json' -Raw) | & $worker | ConvertFrom-Json
if (-not $res.ok) {
    $res.validationErrors | ForEach-Object { Write-Output ("  ! " + $_.message) }
    throw "apply failed: $($res.error)"
}

Move-Item -Force -LiteralPath 'D:\VibeyReports\out\reports\_grow_tmp.rpt' `
                 -Destination 'D:\VibeyReports\out\reports\PMSV10_IndDetailedEval.rpt'
Write-Output ("can-grow set on {0} sub-reports" -f $ops.Count)
