# Rebuilds the Goal Alignment & Cascading Report against LIVE data.
#
# Input:  out/reports/GoalAlignmentReport.v2.rpt  (already has both SPs wired up)
# Output: out/reports/GoalAlignmentReport.live.rpt
#
# Restructures from "everything static in the Report Header" into the proper Crystal shape:
#   ReportHeader  - header block, employee/cycle details (bound), overview tiles (bound), table banner
#   PageHeader    - the 5 column headings + header fill, so they repeat across pages
#   Details       - ONE row of bound fields; Crystal repeats it per goal
#   ReportFooter  - legend + footer block
#
# Font inheritance is PER SECTION, so each section gets its own anchor object set to Segoe UI;
# everything added to that section afterwards inherits it.

$schema = (Get-Content 'D:\VibeyReports\out\json\schema-live.json' -Raw | ConvertFrom-Json).schema

$ops = New-Object System.Collections.ArrayList
$col = New-Object System.Collections.ArrayList
function Op($h)  { [void]$ops.Add($h) }
function Ink($n, $c)    { [void]$col.Add(@{ action='setTextColor'; target=$n; color=$c }) }
function Fill($n, $c)   { [void]$col.Add(@{ action='setFillColor'; target=$n; color=$c }) }
function Stroke($n, $c) { [void]$col.Add(@{ action='setLineColor'; target=$n; color=$c }) }

$W = 11186
$RH = 'ReportHeaderSection1'; $PH = 'PageHeaderSection1'
$DT = 'DetailSection1';       $RF = 'ReportFooterSection1'

$BANNER = 420
$GAP    = 300

$NAVY='#2C3A47'; $WHITE='#FFFFFF'; $INK='#1F2937'; $MUTED='#6B7280'
$BORDER='#D5D9DE'; $PANEL='#F7F8FA'; $THEAD='#DCE3EA'
$GREEN='#1E8E4E'; $AMBER='#C2620F'; $RED='#C0392B'

function Text($sec, $name, $txt, $l, $t, $w, $h, $pt, $bold, $align) {
  Op @{ action='addText'; section=$sec; newName=$name; text=$txt; leftTwips=$l; topTwips=$t; widthTwips=$w; heightTwips=$h }
  Op @{ action='setFontSize'; target=$name; fontSizePt=$pt }
  if ($bold)  { Op @{ action='setBold'; target=$name; bold=$true } }
  if ($align) { Op @{ action='setAlignment'; target=$name; alignment=$align } }
}
function Field($sec, $name, $ref, $l, $t, $w, $h, $pt, $bold, $align) {
  Op @{ action='addField'; section=$sec; newName=$name; fieldRef=$ref; leftTwips=$l; topTwips=$t; widthTwips=$w; heightTwips=$h }
  Op @{ action='setFontSize'; target=$name; fontSizePt=$pt }
  if ($bold)  { Op @{ action='setBold'; target=$name; bold=$true } }
  if ($align) { Op @{ action='setAlignment'; target=$name; alignment=$align } }
}
function Box($sec, $n, $l, $t, $w, $h) { Op @{ action='addBox';  section=$sec; newName=$n; leftTwips=$l; topTwips=$t; widthTwips=$w; heightTwips=$h } }
function HLine($sec, $n, $l, $t, $w)   { Op @{ action='addLine'; section=$sec; newName=$n; leftTwips=$l; topTwips=$t; widthTwips=$w; heightTwips=0 } }
function VLine($sec, $n, $l, $t, $h)   { Op @{ action='addLine'; section=$sec; newName=$n; leftTwips=$l; topTwips=$t; widthTwips=0;  heightTwips=$h } }

$CAS = 'sp_perf_goal_align_cascade;1'
$DET = 'sp_perf_goal_align_detail;1'

# =============================== CLEAR THE STATIC LAYOUT =======================================
 $rhSec = $schema.sections | Where-Object { $_.name -eq $RH }
foreach ($o in $rhSec.objects) { Op @{ action='removeObject'; target=$o.name } }
$cleared = $rhSec.objects.Count

# grow the sections we are about to fill
Op @{ action='resizeSection'; section=$RH; heightTwips=7600 }
Op @{ action='resizeSection'; section=$PH; heightTwips=480 }
Op @{ action='resizeSection'; section=$DT; heightTwips=900 }
Op @{ action='resizeSection'; section=$RF; heightTwips=3100 }

# =============================== REPORT HEADER =================================================
Text $RH 'FontAnchor' 'PeoplesHR - Performance Management' 900 70 5200 250 9 $false $null
Op @{ action='setFont'; target='FontAnchor'; fontName='Segoe UI' }
Ink 'FontAnchor' $MUTED

Box  $RH 'LogoBox' 0 40 800 640;                             Stroke 'LogoBox' $BORDER
Text $RH 'LogoText' 'LOGO' 60 300 680 200 8 $false 'Centre';  Ink 'LogoText' $MUTED

Text $RH 'HdrTitle' 'Goal Alignment & Cascading Report' 900 320 6200 470 20 $true $null; Ink 'HdrTitle' $INK
Text $RH 'HdrConf'  'CONFIDENTIAL - For Internal HR Use Only' 900 820 5200 220 8 $false $null; Ink 'HdrConf' $MUTED
Text $RH 'MetaFmt'  'Format: Crystal Reports (.rpt)' 7200 70 3986 200 8 $false 'Right'; Ink 'MetaFmt' $MUTED
Text $RH 'MetaSys'  'PeoplesHR PMS v3.4'             7200 300 3986 200 8 $false 'Right'; Ink 'MetaSys' $MUTED
HLine $RH 'HdrRule' 0 1150 $W; Stroke 'HdrRule' $NAVY

# --- band 1: employee & cycle details (BOUND) ---------------------------------------------------
$b1 = 1250
Box  $RH 'B1Banner' 0 $b1 $W $BANNER; Fill 'B1Banner' $NAVY; Stroke 'B1Banner' $NAVY
Text $RH 'B1Title' 'EMPLOYEE & CYCLE DETAILS' 150 ($b1+95) 6000 240 11 $true $null; Ink 'B1Title' $WHITE

$b1b = $b1 + $BANNER; $b1h = 3050
Box $RH 'B1Box' 0 $b1b $W $b1h; Fill 'B1Box' $PANEL; Stroke 'B1Box' $BORDER

$leftPairs  = @(
  @('Employee Name', "{$CAS.employee_name}"),
  @('Employee ID',   "{$CAS.emp_display_number}"),
  @('Designation',   "{$CAS.designation}"),
  @('Department',    "{$CAS.department}"))
$rightPairs = @(
  @('Evaluation Cycle',  "{$CAS.cycle_name}"),
  @('Evaluation Period', "{$CAS.evaluation_period}"),
  @('Evaluation Status', "{$CAS.cycle_status}"),
  @('Reporting Manager', "{$CAS.reporting_manager}"))

for ($i=0; $i -lt 4; $i++) {
  $y = $b1b + 180 + ($i * 700)
  Text  $RH ("B1L{0}k" -f $i) $leftPairs[$i][0]  150  $y 2100 240 9 $true $null;  Ink ("B1L{0}k" -f $i) $MUTED
  Field $RH ("B1L{0}v" -f $i) $leftPairs[$i][1]  2350 $y 2450 240 9 $false $null; Ink ("B1L{0}v" -f $i) $INK
  Text  $RH ("B1R{0}k" -f $i) $rightPairs[$i][0] 5300 $y 2300 240 9 $true $null;  Ink ("B1R{0}k" -f $i) $MUTED
  Field $RH ("B1R{0}v" -f $i) $rightPairs[$i][1] 7700 $y 3336 240 9 $false $null; Ink ("B1R{0}v" -f $i) $INK
  HLine $RH ("B1Ldiv{0}" -f $i) 150  ($y+480) 4650; Stroke ("B1Ldiv{0}" -f $i) $BORDER
  HLine $RH ("B1Rdiv{0}" -f $i) 5300 ($y+480) 5736; Stroke ("B1Rdiv{0}" -f $i) $BORDER
}

# --- band 2: overview tiles (3 bound, 1 static placeholder) -------------------------------------
$b2 = $b1b + $b1h + $GAP
Box  $RH 'B2Banner' 0 $b2 $W $BANNER; Fill 'B2Banner' $NAVY; Stroke 'B2Banner' $NAVY
Text $RH 'B2Title' 'GOAL ALIGNMENT OVERVIEW' 150 ($b2+95) 6000 240 11 $true $null; Ink 'B2Title' $WHITE

$b2b = $b2 + $BANNER; $b2h = 1400
Box $RH 'B2Box' 0 $b2b $W $b2h; Fill 'B2Box' $PANEL; Stroke 'B2Box' $BORDER

$tw = [int]($W / 4)
$tileLbl = @('TOTAL GOALS','FULLY ALIGNED','PARTIALLY ALIGNED','NOT ALIGNED')
$tileRef = @("{$CAS.total_goals}", "{$CAS.aligned}", $null, "{$CAS.not_aligned}")
$tileInk = @($INK, $GREEN, $AMBER, $RED)
for ($i=0; $i -lt 4; $i++) {
  $x = $i * $tw
  Text $RH ("B2T{0}lbl" -f $i) $tileLbl[$i] ($x+80) ($b2b+240) ($tw-160) 220 8 $false 'Centre'; Ink ("B2T{0}lbl" -f $i) $MUTED
  if ($tileRef[$i]) {
    Field $RH ("B2T{0}num" -f $i) $tileRef[$i] ($x+80) ($b2b+620) ($tw-160) 500 22 $true 'Centre'
  } else {
    # no partially_aligned column exists - static placeholder, per the user's decision
    Text  $RH ("B2T{0}num" -f $i) '-'         ($x+80) ($b2b+620) ($tw-160) 500 22 $true 'Centre'
  }
  Ink ("B2T{0}num" -f $i) $tileInk[$i]
  if ($i -gt 0) { VLine $RH ("B2Tdiv{0}" -f $i) $x ($b2b+80) 1240; Stroke ("B2Tdiv{0}" -f $i) $BORDER }
}

# --- band 3 banner (the table itself lives in PageHeader + Details) ------------------------------
$b3 = $b2b + $b2h + $GAP
Box  $RH 'B3Banner' 0 $b3 $W $BANNER; Fill 'B3Banner' $NAVY; Stroke 'B3Banner' $NAVY
Text $RH 'B3Title' 'GOAL ALIGNMENT DETAILS' 150 ($b3+95) 6000 240 11 $true $null; Ink 'B3Title' $WHITE

# =============================== PAGE HEADER: column headings ==================================
# 5 columns - the SP returns one cascading_objective_hierarchy, not three separate goal levels.
$colW = @(1100, 2600, 1300, 4200, 1986)
$colX = @(0); for ($i=0; $i -lt 4; $i++) { $colX += ($colX[$i] + $colW[$i]) }
$hdrs = @('Goal ID','Goal Name','Category','Cascading Objective Hierarchy','Alignment Status')
$hdrH = 480

Box $PH 'PHFill' 0 0 $W $hdrH; Fill 'PHFill' $THEAD; Stroke 'PHFill' $BORDER
for ($c=0; $c -lt 5; $c++) {
  $n = ("PHH{0}" -f $c)
  Text $PH $n $hdrs[$c] ($colX[$c]+70) 130 ($colW[$c]-140) 260 8 $true $null
  if ($c -eq 0) { Op @{ action='setFont'; target=$n; fontName='Segoe UI' } }   # section font anchor
  Ink $n $INK
  if ($c -gt 0) { VLine $PH ("PHV{0}" -f $c) $colX[$c] 0 $hdrH; Stroke ("PHV{0}" -f $c) $BORDER }
}

# =============================== DETAILS: one bound row ========================================
$rowH = 900
$cellRefs = @("{$DET.goal_id}", "{$DET.goal_name}", "{$DET.category}", "{$DET.cascading_objective_hierarchy}", "{$DET.alignment_status}")
for ($c=0; $c -lt 5; $c++) {
  $n = ("DR{0}" -f $c)
  Field $DT $n $cellRefs[$c] ($colX[$c]+70) 130 ($colW[$c]-140) 640 8 $false $null
  if ($c -eq 0) { Op @{ action='setFont'; target=$n; fontName='Segoe UI' } }   # section font anchor
  Ink $n $INK
  if ($c -gt 0) { VLine $DT ("DV{0}" -f $c) $colX[$c] 0 $rowH; Stroke ("DV{0}" -f $c) $BORDER }
}
HLine $DT 'DRule' 0 ($rowH-1) $W; Stroke 'DRule' $BORDER

# =============================== REPORT FOOTER: legend + footer =================================
Box  $RF 'B4Banner' 0 0 $W $BANNER; Fill 'B4Banner' $NAVY; Stroke 'B4Banner' $NAVY
Text $RF 'B4Title' 'ALIGNMENT LEGEND' 150 95 6000 240 11 $true $null
Op @{ action='setFont'; target='B4Title'; fontName='Segoe UI' }                # section font anchor
Ink 'B4Title' $WHITE

$b4b = $BANNER; $b4h = 1300
Box $RF 'B4Box' 0 $b4b $W $b4h; Fill 'B4Box' $PANEL; Stroke 'B4Box' $BORDER
$legend    = @(
  'Aligned - Goal is linked to a higher-level objective through the cascade.',
  'Partially Aligned - not currently produced by the stored procedure.',
  'Not Aligned - Goal has no cascading objective hierarchy.')
$legendInk = @($GREEN, $AMBER, $RED)
for ($i=0; $i -lt 3; $i++) {
  $n = ("B4L{0}" -f $i)
  Text $RF $n $legend[$i] 200 ($b4b + 260 + ($i*330)) 9000 240 8 $false $null
  Ink $n $legendInk[$i]
}

$ft = $b4b + $b4h + 340
HLine $RF 'FtRule' 0 $ft $W; Stroke 'FtRule' $NAVY
Text $RF 'FtByK' 'Generated By'   0    ($ft+160) 3000 200 8 $true $null;  Ink 'FtByK' $MUTED
Text $RF 'FtByV' 'PeoplesHR PMS'  0    ($ft+400) 3600 240 8 $false $null; Ink 'FtByV' $INK
Text $RF 'FtCyK' 'Evaluation Cycle' 3900 ($ft+160) 3000 200 8 $true $null; Ink 'FtCyK' $MUTED
Field $RF 'FtCyV' "{$CAS.cycle_name}" 3900 ($ft+400) 3400 240 8 $false $null; Ink 'FtCyV' $INK
Text $RF 'FtNote' 'PeoplesHR Performance Management - Confidential' 0 ($ft+740) 6000 200 8 $false $null; Ink 'FtNote' $MUTED

foreach ($c in $col) { Op $c }

$plan = @{
  command    = 'apply'
  reportPath = 'D:/VibeyReports/out/reports/GoalAlignmentReport.v2.rpt'
  outputPath = 'D:/VibeyReports/out/reports/GoalAlignmentReport.live.rpt'
  overwrite  = $true
  plan       = @{ planVersion = 1; operations = $ops }
}
[IO.File]::WriteAllText('D:\VibeyReports\out\json\goal-plan-live.json', ($plan | ConvertTo-Json -Depth 12 -Compress))

$bound = ($ops | Where-Object { $_.action -eq 'addField' }).Count
Write-Output "cleared static objects : $cleared"
Write-Output "operations             : $($ops.Count)  (colour $($col.Count), bound fields $bound)"
Write-Output "table columns end at   : $($colX[4] + $colW[4]) / $W"
Write-Output "section heights        : RH 7600  PH 480  Details 900  RF 3100"
Write-Output "one-page budget        : 7600 + 480 + (900 x N goals) + 3100  vs printable 16118"
Write-Output "                         -> fits on page 1 up to N = $([math]::Floor((16118 - 7600 - 480 - 3100) / 900)) goals"
