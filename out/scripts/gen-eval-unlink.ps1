<#
    Makes Report 3 (Individual Detailed Evaluation) renderable under Report Navigator's SP mode,
    and produces both variants.

        out/reports/PMSV10_IndDetailedEval.rpt   (in place, via a temp)
          -> out/reports/PMSV10_IndDetailedEval.rpt      Administrator
          -> out/reports/PMSV10_IndDetailedEvalSS.rpt    Employee Self Service

    WHY
    ---
    Six of the seven sub-reports were joined to the main report by Crystal FIELD LINKS. A link
    compiles into hidden "Pm-<main field>" parameter fields inside the sub-report, which Crystal
    fills during its own linkage pass. Report Navigator's SP mode never runs that pass - SPViewer
    executes the procedures itself and pushes DataTables with SetDataSource - so those parameters
    stay empty and Export throws "Missing parameter values". Same failure Report 2 had.

    The links are removed by DROPPING each placed sub-report and re-importing it, NOT by clearing
    the link collection: SubreportController.SetSubreportLinks with an empty SubreportLinksClass
    sends Crystal XI R2 into an infinite loop (measured; see docs/sdk-notes.md).

    Per-employee scoping moves into each sub-report's own procedure, which now takes the same five
    parameters as the main one and derives @emp_number from @emp_number_list - see
    docs/solution/8-Reports/Report-3-SQL.sql section 2.

    CompanyLogo is left alone: it never had links.
#>

$ErrorActionPreference = 'Stop'

$root   = 'D:/VibeyReports'
$worker = "$root/dist/worker/VibeyReports.CrystalWorker.exe"
$src    = "$root/out/reports/PMSV10_IndDetailedEval.rpt"
$arch   = "$root/out/archive/reports"

# Geometry read back off the saved report rather than assumed, so a re-import lands exactly
# where the linked original sat. Object names are Crystal's own auto-numbering.
$linked = @(
  @{ obj='Subreport1'; name='StageWise';  section='empnumberHeaderSection1'; file='PMSV10_IndDetEval_Stages.rpt';          left=0; top=10210; width=11186; height=2400 },
  @{ obj='Subreport3'; name='RatingCmp';  section='DetailSection1';          file='PMSV10_IndDetEval_Feedback.rpt';         left=0; top=460;   width=11186; height=2200 },
  @{ obj='Subreport4'; name='FdpDetails'; section='DetailSection6';          file='PMSV10_IndDetEval_FeedbackDetails.rpt';  left=0; top=460;   width=11186; height=2000 },
  @{ obj='Subreport5'; name='FdpDev';     section='DetailSection7';          file='PMSV10_IndDetEval_DevelopmentPlan.rpt';  left=0; top=460;   width=11186; height=2000 },
  @{ obj='Subreport6'; name='FdpJob';     section='DetailSection8';          file='PMSV10_IndDetEval_JobPreferences.rpt';   left=0; top=460;   width=11186; height=2000 },
  @{ obj='Subreport7'; name='FdpTn';      section='DetailSection9';          file='PMSV10_IndDetEval_TrainingNeeds.rpt';    left=0; top=460;   width=11186; height=2000 }
)

function Invoke-Worker($Label, $Request) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $worker
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding  = $utf8

    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write(($Request | ConvertTo-Json -Depth 12 -Compress))
    $p.StandardInput.Close()
    $raw = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()

    # A crashed worker writes nothing to stdout, so a $null response must be reported as itself
    # rather than as a set of empty properties.
    $res = if ([string]::IsNullOrWhiteSpace($raw)) { $null } else { $raw | ConvertFrom-Json }
    if ($null -eq $res) { throw "$Label`: worker returned no response (exit $($p.ExitCode)).`n$err" }
    if (-not $res.ok) {
        $detail = $res.error
        if ($res.validationErrors) {
            $detail += "`n" + (($res.validationErrors | ForEach-Object { "  [op $($_.operationIndex)] $($_.message)" }) -join "`n")
        }
        throw "$Label`: $detail`n$err"
    }
    return $res
}

# --------------------------------------------------------------------------------------------
# STAGE 1 - drop each linked sub-report and re-import it with no links.
#
# One apply: removeObject targets names that exist now, and addSubreport's newName is the
# SubreportName the validator checks, so the whole set validates together. The placed objects are
# re-numbered by Crystal, which is why stage 2 reads the names back instead of assuming them.
# --------------------------------------------------------------------------------------------
$ops = New-Object System.Collections.ArrayList
foreach ($l in $linked) { [void]$ops.Add(@{ action='removeObject'; target=$l.obj }) }
foreach ($l in $linked) {
    [void]$ops.Add(@{ action='addSubreport'; section=$l.section; newName=$l.name
                      reportPath="$arch/$($l.file)"
                      leftTwips=$l.left; topTwips=$l.top; widthTwips=$l.width; heightTwips=$l.height })
}

$tmp = "$root/out/archive/_stage1_PMSV10_IndDetailedEval.rpt"
$r1 = Invoke-Worker 'unlink' @{
    command='apply'; reportPath=$src; outputPath=$tmp; overwrite=$true
    plan=@{ planVersion=1; operations=$ops.ToArray() }
}
Write-Output ("stage 1: {0} operations applied ({1} sub-reports re-imported without links)" -f $r1.operationsApplied, $linked.Count)

# --------------------------------------------------------------------------------------------
# STAGE 2 - clear the default sub-report borders, then write both variants.
#
# Crystal gives a placed sub-report a single black border, which draws a box around every one of
# them. Clearing it must happen in a later apply than the addSubreport: the name to target does
# not exist while the creating plan is being validated.
# --------------------------------------------------------------------------------------------
$borderOps = New-Object System.Collections.ArrayList
foreach ($sec in $r1.schema.sections) {
    foreach ($o in $sec.objects) {
        if ($o.kind -eq 'Subreport') {
            [void]$borderOps.Add(@{ action='setBorder'; target=$o.name
                                    left='none'; right='none'; top='none'; bottom='none' })
        }
    }
}

# Both variants share the SAME layout and identical content - only the Report Navigator parameter
# form differs, and that lives in HS_HR_RN_PAR_DEF, not in the .rpt. Applying one plan twice is
# what keeps that true by construction.
$variants = @(
  @{ label='Administrator';         out="$root/out/reports/PMSV10_IndDetailedEval.rpt"   },
  @{ label='Employee Self Service'; out="$root/out/reports/PMSV10_IndDetailedEvalSS.rpt" }
)
foreach ($v in $variants) {
    $r2 = Invoke-Worker $v.label @{
        command='apply'; reportPath=$tmp; outputPath=$v.out; overwrite=$true
        plan=@{ planVersion=1; operations=$borderOps.ToArray() }
    }
    Write-Output ("{0,-22} -> {1}  ({2} borders cleared)" -f $v.label, (Split-Path $v.out -Leaf), $r2.operationsApplied)
}
Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
