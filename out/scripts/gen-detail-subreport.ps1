# Lays out the Goal Alignment DETAIL sub-report.
#
# Input:  out/reports/Report1.rpt        (blank, bound only to sp_perf_goal_align_detail)
# Output: out/reports/PMSV10_GoalAlignDetail.rpt
#
# This is a sub-report, so it carries NO page furniture of its own - no title, no margins
# decoration, no footer. Just the column headings (Page Header) and one bound row (Details),
# which Crystal repeats per goal inside the host report's frame.
#
# Column widths match the host report exactly so the two line up when embedded.
#
# goal_id is a BIGINT, so Crystal's default number format renders it as
# "10,311.00" - thousands separator and two decimals. setNumberFormat now fixes
# that here, so it survives regeneration; it used to be a manual Designer step.
#
# Note this only applies to THIS file. The copy already embedded inside
# PMSV10_GoalAlignCascade.final.rpt is a separate document that layout
# operations cannot reach, and it carries the equivalent fix applied by hand in
# the Designer. Re-importing this sub-report would pick up the automated one.

$ops = New-Object System.Collections.ArrayList
$col = New-Object System.Collections.ArrayList
function Op($h)        { [void]$ops.Add($h) }
function Ink($n, $c)   { [void]$col.Add(@{ action='setTextColor'; target=$n; color=$c }) }
function Fill($n, $c)  { [void]$col.Add(@{ action='setFillColor'; target=$n; color=$c }) }
function Stroke($n,$c) { [void]$col.Add(@{ action='setLineColor'; target=$n; color=$c }) }

$PH = 'PageHeaderSection1'
$DT = 'DetailSection1'
$DET = 'sp_perf_goal_align_detail;1'

$INK='#1F2937'; $BORDER='#D5D9DE'; $THEAD='#DCE3EA'

# same five columns, same widths, as the host report
$colW = @(1100, 2600, 1300, 4200, 1986)
$colX = @(0); for ($i=0; $i -lt 4; $i++) { $colX += ($colX[$i] + $colW[$i]) }
$hdrs = @('Goal ID','Goal Name','Category','Cascading Objective Hierarchy','Alignment Status')
$refs = @("{$DET.goal_id}", "{$DET.goal_name}", "{$DET.category}",
          "{$DET.cascading_objective_hierarchy}", "{$DET.alignment_status}")

$hdrH = 480
$rowH = 900

# collapse the sections this sub-report does not use - a sub-report must not add
# vertical padding of its own inside the host's frame
Op @{ action='resizeSection'; section='ReportHeaderSection1'; heightTwips=0 }
Op @{ action='resizeSection'; section='PageFooterSection1';   heightTwips=0 }
Op @{ action='resizeSection'; section='ReportFooterSection1'; heightTwips=0 }
Op @{ action='resizeSection'; section=$PH; heightTwips=$hdrH }
Op @{ action='resizeSection'; section=$DT; heightTwips=$rowH }

# ---- Page Header: column headings ------------------------------------------------------------
Op @{ action='addBox'; section=$PH; newName='PHFill'; leftTwips=0; topTwips=0; widthTwips=11186; heightTwips=$hdrH }
Fill 'PHFill' $THEAD; Stroke 'PHFill' $BORDER

for ($c=0; $c -lt 5; $c++) {
  $n = "PHH$c"
  Op @{ action='addText'; section=$PH; newName=$n; text=$hdrs[$c];
        leftTwips=($colX[$c]+70); topTwips=130; widthTwips=($colW[$c]-140); heightTwips=260 }
  Op @{ action='setFontSize'; target=$n; fontSizePt=8 }
  Op @{ action='setBold'; target=$n; bold=$true }
  if ($c -eq 0) { Op @{ action='setFont'; target=$n; fontName='Segoe UI' } }   # section font anchor
  Ink $n $INK
  if ($c -gt 0) {
    Op @{ action='addLine'; section=$PH; newName="PHV$c"; leftTwips=$colX[$c]; topTwips=0; widthTwips=0; heightTwips=$hdrH }
    Stroke "PHV$c" $BORDER
  }
}

# ---- Details: one bound row ------------------------------------------------------------------
for ($c=0; $c -lt 5; $c++) {
  $n = "DR$c"
  Op @{ action='addField'; section=$DT; newName=$n; fieldRef=$refs[$c];
        leftTwips=($colX[$c]+70); topTwips=130; widthTwips=($colW[$c]-140); heightTwips=640 }
  Op @{ action='setFontSize'; target=$n; fontSizePt=8 }
  # goal_id is a BIGINT identifier, not a quantity: no decimals, no separators.
  if ($c -eq 0) { Op @{ action='setNumberFormat'; target=$n; decimalPlaces=0; thousandsSeparator=$false } }
  # The hierarchy column carries a full parent chain and clips without can-grow.
  if ($c -eq 3) { Op @{ action='setCanGrow'; target=$n; canGrow=$true } }
  if ($c -eq 0) { Op @{ action='setFont'; target=$n; fontName='Segoe UI' } }   # section font anchor
  Ink $n $INK
  if ($c -gt 0) {
    Op @{ action='addLine'; section=$DT; newName="DV$c"; leftTwips=$colX[$c]; topTwips=0; widthTwips=0; heightTwips=$rowH }
    Stroke "DV$c" $BORDER
  }
}
Op @{ action='addLine'; section=$DT; newName='DRule'; leftTwips=0; topTwips=($rowH-1); widthTwips=11186; heightTwips=0 }
Stroke 'DRule' $BORDER

foreach ($c in $col) { Op $c }

$plan = @{
  command    = 'apply'
  reportPath = 'D:/VibeyReports/out/reports/Report1.rpt'
  outputPath = 'D:/VibeyReports/out/reports/PMSV10_GoalAlignDetail.rpt'
  overwrite  = $true
  plan       = @{ planVersion = 1; operations = $ops }
}
[IO.File]::WriteAllText('D:\VibeyReports\out\json\detail-plan.json', ($plan | ConvertTo-Json -Depth 12 -Compress))

Write-Output "operations   : $($ops.Count)  (colour $($col.Count))"
Write-Output "columns end  : $($colX[4] + $colW[4]) / 11186"
Write-Output "section h    : PageHeader $hdrH   Details $rowH   others 0"
