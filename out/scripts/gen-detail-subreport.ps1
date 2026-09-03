# Lays out the Goal Alignment DETAIL sub-report.
#
# Input:  out/archive/inputs/Report1.rpt        (blank, bound only to sp_perf_goal_align_detail)
# Output: out/archive/reports/PMSV10_GoalAlignDetail.rpt
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

# same five columns, same widths, as the host report.
#
# Goal ID took 300 twips from Goal Name (1100 -> 1400, 2600 -> 2300). Widening on its own does
# NOT separate the ID from the goal name: a numeric field right-aligns flush to the right edge
# of its own cell, and the next cell's text starts flush at its left edge, so the two touch
# whatever the width is. The Goal ID cell is therefore also CENTRED (below), which is what puts
# real space on both sides of the value. The extra width is what makes centring look deliberate
# rather than cramped.
$colW = @(1400, 2300, 1300, 4200, 1986)
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
  # Goal ID's heading follows its centred values, so the column reads as one thing.
  if ($c -eq 0) { Op @{ action='setAlignment'; target=$n; alignment='Centre' } }
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
        leftTwips=$colX[$c]; topTwips=0; widthTwips=$colW[$c]; heightTwips=$rowH }
  Op @{ action='setFontSize'; target=$n; fontSizePt=8 }
  # goal_id is a BIGINT identifier, not a quantity: no decimals, no separators.
  # Centred, not right-aligned: a field spans its whole cell so the ruling can come from the
  # cell's own borders (a Line has no can-grow - see the note below), which leaves a numeric
  # field's default right alignment sitting hard against the divider with the goal name flush
  # on the other side of it. Centring is the only way to get padding without insetting the
  # field and breaking the ruling.
  if ($c -eq 0) {
    Op @{ action='setNumberFormat'; target=$n; decimalPlaces=0; thousandsSeparator=$false }
    Op @{ action='setAlignment';    target=$n; alignment='Centre' }
  }
  # The hierarchy column carries a full parent chain and clips without can-grow.
  if ($c -eq 3) { Op @{ action='setCanGrow'; target=$n; canGrow=$true } }
  if ($c -eq 0) { Op @{ action='setFont'; target=$n; fontName='Segoe UI' } }   # section font anchor
  Ink $n $INK
  # Ruled by the cells' own borders, not by drawn lines: a Line has no can-grow,
  # so the hierarchy column growing left the old dividers behind at the original
  # row height with the text running on past them. See gen-eval-subreports.ps1.
  [void]$col.Add(@{ action='setBorder'; target=$n
                    left='single'; right='single'; bottom='single'; color=$BORDER })
}

foreach ($c in $col) { Op $c }

$plan = @{
  command    = 'apply'
  reportPath = 'D:/VibeyReports/out/archive/inputs/Report1.rpt'
  outputPath = 'D:/VibeyReports/out/archive/reports/PMSV10_GoalAlignDetail.rpt'
  overwrite  = $true
  plan       = @{ planVersion = 1; operations = $ops }
}
[IO.File]::WriteAllText('D:\VibeyReports\out\archive\json\detail-plan.json', ($plan | ConvertTo-Json -Depth 12 -Compress))

Write-Output "operations   : $($ops.Count)  (colour $($col.Count))"
Write-Output "columns end  : $($colX[4] + $colW[4]) / 11186"
Write-Output "section h    : PageHeader $hdrH   Details $rowH   others 0"
