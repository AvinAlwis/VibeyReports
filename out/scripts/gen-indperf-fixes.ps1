<#
    Two alignment fixes to Report 1 (Individual Performance Overview), and both variants out.

        docs/solution/8-Reports/PMSV10_IndPerfOverview.rpt
          -> out/reports/PMSV10_IndPerfOverview.rpt      Administrator
          -> out/reports/PMSV10_IndPerfOverviewSS.rpt    Employee Self Service

    1. EVALUATION CYCLE NAME OVERFLOWS ITS ROW
       cyclename1 has canGrow=True and a real cycle name wraps to two lines
       ("Competency Assessment Dev Testing (90 Reviewer On, Goals Off)"), so it grows from 214 to
       ~428 twips: 2126..2554. The row divider Line10 sits at 2390, so the second line prints
       across the divider and into the Appraiser row underneath.

       The panel has the room already - Box11 is 870 tall and the two rows split it 380/490, with
       the lower row holding a single 221-high field. Rebalancing to 530/340 gives the wrapped
       value its second line inside its own row. The dividers on both panels are also brought to
       the SAME y: they were 30 twips apart (2390 and 2420), which is visible as a step between
       the two halves.

       This does not make the row infinitely tall - a THIRD line would overflow again. If cycle
       names get longer than this, the row needs more height still, or the field needs a smaller
       font.

    2. "EMPLOYEE NAME" / "EMPLOYEE ID" BREAK THE COLUMN DIVIDER
       Both labels are 1854 wide from x=172, so they end at 2026. The vertical divider Line8 is at
       x=2030 - four twips away. Four twips is a fifth of a pixel at screen resolution, so the
       label's opaque background lands on the same pixel as the line and wins, leaving a white gap
       in the divider exactly beside each label. It exports cleanly to PDF (measured: the crossing
       is solid at 300 dpi), which is why it only shows in the viewer.

       Narrowing both labels to 1700 puts 158 twips between text and line. Nothing else in either
       panel is closer than 90 twips to its divider, so only these two are touched.

    NOT CHANGED: Box8 (60,510,5898x910) is a near-duplicate of Box10 (60,490,5650x880), 20 twips
    lower and 248 wider, and its right edge at 5958 reaches past the left edge of the right-hand
    panel (Box9 starts at 5820). It looks like a leftover from hand layout and it doubles the
    panel outline. Removing it is plausible but it is not what was reported, and deleting a border
    from someone else's report on a guess is worse than a doubled hairline.
#>

$ErrorActionPreference = 'Stop'

$root   = 'D:/VibeyReports'
$worker = "$root/dist/worker/VibeyReports.CrystalWorker.exe"
$base   = 'D:/PMS_Module/HRM-PMS-NET/docs/solution/8-Reports/PMSV10_IndPerfOverview.rpt'

$ops = New-Object System.Collections.ArrayList
function Op($h) { [void]$ops.Add($h) }

# --- 1. Evaluation Cycle Details: give row 1 the height a wrapped value needs -----------------
# Row 1 objects move up to the top of the row so two lines fit below them.
Op @{ action='move'; target='Text6';             leftTwips=193;  topTwips=2065 }   # 'Evaluation Cycle Name'
Op @{ action='move'; target='cyclename1';        leftTwips=2688; topTwips=2060 }   # wraps to 2060..2488
Op @{ action='move'; target='Text7';             leftTwips=5958; topTwips=2065 }   # 'Evaluation Period'
Op @{ action='move'; target='evaluationperiod1'; leftTwips=7996; topTwips=2065 }

# The dividers move down together - 2390 and 2420 become one line at 2540.
Op @{ action='move'; target='Line10'; leftTwips=60;   topTwips=2540 }
Op @{ action='move'; target='Line12'; leftTwips=5810; topTwips=2540 }

# Row 2 follows the divider down. 2625 + 221 = 2846, inside Box11 (ends 2880) and Box12 (2890).
# The x values also line row 2 up with row 1 - see the column-alignment note below.
Op @{ action='move'; target='Text10';         leftTwips=193;  topTwips=2625 }      # 'Appraiser'   was 230
Op @{ action='move'; target='appraisername1';  leftTwips=2688; topTwips=2625 }      #              was 2661
Op @{ action='move'; target='Text11';         leftTwips=5958; topTwips=2625 }      # 'Reviewer'
Op @{ action='move'; target='reviewername1';   leftTwips=7996; topTwips=2625 }      #              was 8015

# --- Column alignment: every value in a panel starts at the same x ---------------------------
# Measured across all four label/value pairs. Three panels had their second row starting at a
# different x from their first, which reads as a ragged left edge down the value column:
#
#   Employee left    Text3  172  / employeename1      2066   Text2  172  / empdisplaynumber1 2066   OK
#   Employee right   Text4  5958 / designation1       7573   Text5  5958 / department1       7653   80 out
#   Cycle    left    Text6  193  / cyclename1         2688   Text10 230  / appraisername1    2661   37/27 out
#   Cycle    right   Text7  5958 / evaluationperiod1  7996   Text11 5958 / reviewername1     8015   19 out
#
# Row 1 is taken as the reference in each panel, so only row 2 moves. 80 twips is about 1.7px on
# screen, which is why Business Unit's value was the one that looked wrong; the other three are
# under half a pixel each and only show as a slightly soft edge.
Op @{ action='move'; target='department1'; leftTwips=7573; topTwips=1048 }         # was 7653

# --- 2. Employee Details: keep the labels off the column divider ------------------------------
Op @{ action='resize'; target='Text3'; widthTwips=1700; heightTwips=221 }          # 'Employee Name'
Op @{ action='resize'; target='Text2'; widthTwips=1700; heightTwips=221 }          # 'Employee ID'

# --- 3. Overall Rating tile: the value must fit on ONE line -----------------------------------
# Text32 holds {overall_rating} at 14pt. It started 1430 wide, which clipped "Expectations"
# (about 1680 at that size) to "Expectatio" - in the PDF export, not just the viewer - and it was
# Left-aligned under a Centre-aligned caption.
#
# Widening to 2000 fixed the clipping but not the real constraint: canGrow is off, so a value too
# wide for the field WRAPS and then overflows its 470 height. "Below Benchmark" took two lines at
# ~280 each = 560, spilling 90 twips past the bottom and printing over Text33 beneath it.
#
# Sized for the longest descriptor in the data instead. MEASURED: the longest grade_description
# across every rating method is 21 characters ("Approaching Benchmark"), about 3230 twips at 14pt.
# 4400 clears that with room, and stays centred on 2596 where Text30 ('Overall Rating') and
# Text33 centre: 396..4796, inside the tile's box (60..5710) and clear of the right-hand tile at
# 5700.
#
# A longer descriptor than 21 characters would wrap and collide again. The durable fix is
# canGrow plus a taller tile, but that moves everything below it down the page.
Op @{ action='move';         target='Text32'; leftTwips=396;  topTwips=3870 }
Op @{ action='resize';       target='Text32'; widthTwips=4400; heightTwips=470 }
Op @{ action='setAlignment'; target='Text32'; alignment='Centre' }

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

# Both variants from one plan applied twice: same layout, identical content, only the Report
# Navigator parameter form differs and that lives in HS_HR_RN_PAR_DEF.
foreach ($v in @(
    @{ label='Administrator';         out="$root/out/reports/PMSV10_IndPerfOverview.rpt"   },
    @{ label='Employee Self Service'; out="$root/out/reports/PMSV10_IndPerfOverviewSS.rpt" })) {
    $r = Invoke-Worker $v.label @{
        command='apply'; reportPath=$base; outputPath=$v.out; overwrite=$true
        plan=@{ planVersion=1; operations=$ops.ToArray() }
    }
    Write-Output ("{0,-22} -> {1}  ({2} operations applied)" -f $v.label, (Split-Path $v.out -Leaf), $r.operationsApplied)
}
