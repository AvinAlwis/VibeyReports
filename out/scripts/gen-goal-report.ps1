# Generates a Vibey Reports layout plan that builds the "Goal Alignment & Cascading Report"
# onto SampleReport.rpt (a near-empty .rpt shell), with colour.
#
# Fully reproducible from the pristine fixture: the shell's own objects are deleted with
# removeObject and their sections collapsed, so no manual cleanup in the Designer is needed.
#
# Style follows PMSV10_IndPerfOverview.rpt: Segoe UI throughout.
#
# Two tricks worth knowing:
#  1. Added objects inherit the font of the FIRST fontable object in their section, so we add one
#     anchor, set it to Segoe UI, and everything after inherits it (~60 setFont ops saved).
#  2. Z-order is fixed at Add time (later = on top), so every filled box is added BEFORE the text
#     that sits on it. Colour operations afterwards do not change z-order.

$ops = New-Object System.Collections.ArrayList
function Op($h) { [void]$ops.Add($h) }

$SEC  = 'ReportHeaderSection1'
$W    = 11186          # printable width  (A4 portrait, 0.25in margins)
$PH   = 16118          # printable height (16838 - 360 - 360)
$SECH = 16050          # section height - tuned to fill the page

# --- vertical rhythm ---------------------------------------------------------------------------
$BANNER = 420          # band banner height
$GAP    = 300          # gap between bands

# --- palette (from the user's mockup) ----------------------------------------------------------
$NAVY   = '#2C3A47'    # band banners, rules
$WHITE  = '#FFFFFF'
$INK    = '#1F2937'    # body text
$MUTED  = '#6B7280'    # labels, secondary text
$BORDER = '#D5D9DE'    # box outlines, dividers
$PANEL  = '#F7F8FA'    # panel fill
$THEAD  = '#DCE3EA'    # table header row fill
$GREEN  = '#1E8E4E'
$AMBER  = '#C2620F'
$RED    = '#C0392B'

function Text($name, $txt, $l, $t, $w, $h, $pt, $bold, $align) {
  Op @{ action='addText'; section=$SEC; newName=$name; text=$txt;
        leftTwips=$l; topTwips=$t; widthTwips=$w; heightTwips=$h }
  Op @{ action='setFontSize'; target=$name; fontSizePt=$pt }
  if ($bold)  { Op @{ action='setBold'; target=$name; bold=$true } }
  if ($align) { Op @{ action='setAlignment'; target=$name; alignment=$align } }
}
function Box($name, $l, $t, $w, $h) { Op @{ action='addBox';  section=$SEC; newName=$name; leftTwips=$l; topTwips=$t; widthTwips=$w; heightTwips=$h } }
function HLine($name, $l, $t, $w)   { Op @{ action='addLine'; section=$SEC; newName=$name; leftTwips=$l; topTwips=$t; widthTwips=$w; heightTwips=0 } }
function VLine($name, $l, $t, $h)   { Op @{ action='addLine'; section=$SEC; newName=$name; leftTwips=$l; topTwips=$t; widthTwips=0;  heightTwips=$h } }

# colour ops are collected separately and appended last (z-order is already fixed by then)
$col = New-Object System.Collections.ArrayList
function Ink($name, $c)    { [void]$col.Add(@{ action='setTextColor'; target=$name; color=$c }) }
function Fill($name, $c)   { [void]$col.Add(@{ action='setFillColor'; target=$name; color=$c }) }
function Stroke($name, $c) { [void]$col.Add(@{ action='setLineColor'; target=$name; color=$c }) }

# =============================== SHELL CLEANUP =================================================
Op @{ action='resizeSection'; section=$SEC; heightTwips=$SECH }

# Delete the shell's own objects. Collapsing them to 0x0 in a zero-height section was tried first
# and made Crystal fail with "page size was not large enough to format the contents of an object"
# - a degenerate zero-sized object upsets the formatter. Deleting is cleaner.
# NOTE: removing every bound field discards the report's saved data, so rendering then needs a live
# connection to this shell's sample database. That is why this file has no PDF preview.
foreach ($n in 'PrintDate1','Text1','Text2','CardCode1','CardName1') {
  Op @{ action='removeObject'; target=$n }
}
foreach ($s in 'PageHeaderSection1','DetailSection1','ReportFooterSection1') {
  Op @{ action='resizeSection'; section=$s; heightTwips=0 }
}

# --- font anchor -------------------------------------------------------------------------------
Text 'FontAnchor' 'PeoplesHR - Performance Management' 900 70 5200 250 9 $false $null
Op @{ action='setFont'; target='FontAnchor'; fontName='Segoe UI' }
Ink 'FontAnchor' $MUTED

# =============================== HEADER ========================================================
Box  'LogoBox' 0 40 800 640;                             Stroke 'LogoBox' $BORDER
Text 'LogoText' 'LOGO' 60 300 680 200 8 $false 'Centre'; Ink 'LogoText' $MUTED

Text 'HdrTitle' 'Goal Alignment & Cascading Report' 900 320 6200 470 20 $true $null; Ink 'HdrTitle' $INK
Text 'HdrConf'  'CONFIDENTIAL - For Internal HR Use Only' 900 820 5200 220 8 $false $null; Ink 'HdrConf' $MUTED

Text 'MetaId'  'Report ID: RPT-GAC-00187'        7200 70  3986 200 8 $false 'Right'; Ink 'MetaId'  $MUTED
Text 'MetaGen' 'Generated: 01/09/2026, 09:17:03' 7200 300 3986 200 8 $false 'Right'; Ink 'MetaGen' $MUTED
Text 'MetaFmt' 'Format: Crystal Reports (.rpt)'  7200 530 3986 200 8 $false 'Right'; Ink 'MetaFmt' $MUTED

HLine 'HdrRule' 0 1150 $W; Stroke 'HdrRule' $NAVY

# =============================== BAND 1: EMPLOYEE & CYCLE DETAILS ==============================
$b1 = 1250
Box  'B1Banner' 0 $b1 $W $BANNER; Fill 'B1Banner' $NAVY; Stroke 'B1Banner' $NAVY
Text 'B1Title' 'EMPLOYEE & CYCLE DETAILS' 150 ($b1+95) 6000 240 11 $true $null; Ink 'B1Title' $WHITE

$b1b = $b1 + $BANNER
$b1h = 3050
Box 'B1Box' 0 $b1b $W $b1h; Fill 'B1Box' $PANEL; Stroke 'B1Box' $BORDER

$leftRows  = @('Employee Name|Emily Rodriguez','Employee ID|EMP-10472','Designation|Senior Software Engineer','Department|Engineering - Platform')
$rightRows = @('Evaluation Cycle|Annual Performance Cycle 2025','Evaluation Period|01-Jan-2025 to 31-Dec-2025','Evaluation Status|In Progress','Reporting Manager|Michael Chen - Engineering Manager')

for ($i=0; $i -lt 4; $i++) {
  $y = $b1b + 180 + ($i * 700)
  $lp = $leftRows[$i].Split('|'); $rp = $rightRows[$i].Split('|')
  Text ("B1L{0}k" -f $i) $lp[0] 150  $y 2100 240 9 $true  $null; Ink ("B1L{0}k" -f $i) $MUTED
  Text ("B1L{0}v" -f $i) $lp[1] 2350 $y 2450 240 9 $false $null; Ink ("B1L{0}v" -f $i) $INK
  Text ("B1R{0}k" -f $i) $rp[0] 5300 $y 2300 240 9 $true  $null; Ink ("B1R{0}k" -f $i) $MUTED
  Text ("B1R{0}v" -f $i) $rp[1] 7700 $y 3336 240 9 $false $null; Ink ("B1R{0}v" -f $i) $INK
  HLine ("B1Ldiv{0}" -f $i) 150  ($y+480) 4650; Stroke ("B1Ldiv{0}" -f $i) $BORDER
  HLine ("B1Rdiv{0}" -f $i) 5300 ($y+480) 5736; Stroke ("B1Rdiv{0}" -f $i) $BORDER
}
Ink 'B1R2v' $AMBER      # "In Progress"

# =============================== BAND 2: GOAL ALIGNMENT OVERVIEW ===============================
$b2 = $b1b + $b1h + $GAP
Box  'B2Banner' 0 $b2 $W $BANNER; Fill 'B2Banner' $NAVY; Stroke 'B2Banner' $NAVY
Text 'B2Title' 'GOAL ALIGNMENT OVERVIEW' 150 ($b2+95) 6000 240 11 $true $null; Ink 'B2Title' $WHITE

$b2b = $b2 + $BANNER
$b2h = 1400
Box 'B2Box' 0 $b2b $W $b2h; Fill 'B2Box' $PANEL; Stroke 'B2Box' $BORDER

$tiles   = @('TOTAL GOALS|5','FULLY ALIGNED|2','PARTIALLY ALIGNED|2','NOT ALIGNED|1')
$tileInk = @($INK, $GREEN, $AMBER, $RED)
$tw = [int]($W / 4)
for ($i=0; $i -lt 4; $i++) {
  $x = $i * $tw
  $p = $tiles[$i].Split('|')
  Text ("B2T{0}lbl" -f $i) $p[0] ($x+80) ($b2b+240) ($tw-160) 220 8  $false 'Centre'; Ink ("B2T{0}lbl" -f $i) $MUTED
  Text ("B2T{0}num" -f $i) $p[1] ($x+80) ($b2b+620) ($tw-160) 500 22 $true  'Centre'; Ink ("B2T{0}num" -f $i) $tileInk[$i]
  if ($i -gt 0) { VLine ("B2Tdiv{0}" -f $i) $x ($b2b+80) 1240; Stroke ("B2Tdiv{0}" -f $i) $BORDER }
}

# =============================== BAND 3: GOAL ALIGNMENT DETAILS ================================
$b3 = $b2b + $b2h + $GAP
Box  'B3Banner' 0 $b3 $W $BANNER; Fill 'B3Banner' $NAVY; Stroke 'B3Banner' $NAVY
Text 'B3Title' 'GOAL ALIGNMENT DETAILS' 150 ($b3+95) 6000 240 11 $true $null; Ink 'B3Title' $WHITE

$b3t  = $b3 + $BANNER
$colW = @(973, 1783, 1185, 1659, 1684, 2058, 1844)
$colX = @(0); for ($i=0; $i -lt 6; $i++) { $colX += ($colX[$i] + $colW[$i]) }
$hdrs = @('Goal ID','Goal Name','Category','Team Goal','Department Goal','Organisational Goal','Alignment Status')

$rowH = 900; $hdrH = 480; $nRows = 5
$tableH = $hdrH + ($rowH * $nRows)

Box 'B3Table'    0 $b3t $W $tableH; Stroke 'B3Table' $BORDER
Box 'B3HeadFill' 0 $b3t $W $hdrH;   Fill 'B3HeadFill' $THEAD; Stroke 'B3HeadFill' $BORDER

for ($c=0; $c -lt 7; $c++) {
  Text ("B3H{0}" -f $c) $hdrs[$c] ($colX[$c]+70) ($b3t+130) ($colW[$c]-140) 260 8 $true $null; Ink ("B3H{0}" -f $c) $INK
  if ($c -gt 0) { VLine ("B3V{0}" -f $c) $colX[$c] $b3t $tableH; Stroke ("B3V{0}" -f $c) $BORDER }
}
HLine 'B3HdrRule' 0 ($b3t+$hdrH) $W; Stroke 'B3HdrRule' $BORDER

$rows = @(
  'G-1001|Migrate legacy auth service to OAuth 2.1|Strategic|Improve platform reliability|Modernise core engineering stack|Deliver secure, scalable digital platform|Fully Aligned',
  'G-1002|Reduce API response time by 25%|Team|Hit Q3 SLO targets|Customer experience excellence|Improve customer satisfaction NPS by 10 pts|Fully Aligned',
  'G-1003|Mentor 2 junior engineers on platform standards|Individual|Grow engineering bench strength|Build internal capability|-|Partially Aligned',
  'G-1004|Publish internal architecture playbook|Individual|-|Knowledge sharing initiative|-|Partially Aligned',
  'G-1005|Complete advanced Kubernetes certification|Individual|-|-|-|Not Aligned'
)
$statusInk = @($GREEN, $GREEN, $AMBER, $AMBER, $RED)

for ($r=0; $r -lt $nRows; $r++) {
  $y = $b3t + $hdrH + ($r * $rowH)
  $cells = $rows[$r].Split('|')
  for ($c=0; $c -lt 7; $c++) {
    $n = ("B3R{0}C{1}" -f $r,$c)
    Text $n $cells[$c] ($colX[$c]+70) ($y+130) ($colW[$c]-140) 640 8 $false $null
    if ($c -eq 6) { Ink $n $statusInk[$r] } else { Ink $n $INK }
  }
  if ($r -gt 0) { HLine ("B3Rrule{0}" -f $r) 0 $y $W; Stroke ("B3Rrule{0}" -f $r) $BORDER }
}

# =============================== BAND 4: ALIGNMENT LEGEND ======================================
$b4 = $b3t + $tableH + $GAP
Box  'B4Banner' 0 $b4 $W $BANNER; Fill 'B4Banner' $NAVY; Stroke 'B4Banner' $NAVY
Text 'B4Title' 'ALIGNMENT LEGEND' 150 ($b4+95) 6000 240 11 $true $null; Ink 'B4Title' $WHITE

$b4b = $b4 + $BANNER
$b4h = 1300
Box 'B4Box' 0 $b4b $W $b4h; Fill 'B4Box' $PANEL; Stroke 'B4Box' $BORDER
$legend    = @(
  'Fully Aligned - Goal is directly linked to organisational objectives.',
  'Partially Aligned - Goal has indirect or partial linkage upward.',
  'Not Aligned - Goal is not linked to a higher-level objective.'
)
$legendInk = @($GREEN, $AMBER, $RED)
for ($i=0; $i -lt 3; $i++) {
  $n = ("B4L{0}" -f $i)
  Text $n $legend[$i] 200 ($b4b + 260 + ($i*330)) 9000 240 8 $false $null
  Ink $n $legendInk[$i]
}

# =============================== FOOTER ========================================================
$ft = $b4b + $b4h + 340
HLine 'FtRule' 0 $ft $W; Stroke 'FtRule' $NAVY
Text 'FtByK'  'Generated By'                 0    ($ft+160) 3000 200 8 $true  $null; Ink 'FtByK'  $MUTED
Text 'FtByV'  'Sarah Thompson - HR Manager'  0    ($ft+400) 3600 240 8 $false $null; Ink 'FtByV'  $INK
Text 'FtDtK'  'Generated Date'               3900 ($ft+160) 3000 200 8 $true  $null; Ink 'FtDtK'  $MUTED
Text 'FtDtV'  '01/09/2026, 09:17:03'         3900 ($ft+400) 3400 240 8 $false $null; Ink 'FtDtV'  $INK
Text 'FtSysK' 'System'                       7600 ($ft+160) 3586 200 8 $true  'Right'; Ink 'FtSysK' $MUTED
Text 'FtSysV' 'PeoplesHR PMS v3.4'           7600 ($ft+400) 3586 240 8 $false 'Right'; Ink 'FtSysV' $INK
Text 'FtNote' 'PeoplesHR Performance Management - Confidential' 0 ($ft+760) 6000 200 8 $false $null; Ink 'FtNote' $MUTED

foreach ($c in $col) { Op $c }

$plan = @{
  command    = 'apply'
  reportPath = 'D:/VibeyReports/tests/fixtures/SampleReport.rpt'
  outputPath = 'D:/VibeyReports/out/archive/reports/GoalAlignmentReport.rpt'
  overwrite  = $true
  plan       = @{ planVersion = 1; operations = $ops }
}
[IO.File]::WriteAllText('D:\VibeyReports\out\archive\json\goal-plan.json', ($plan | ConvertTo-Json -Depth 10 -Compress))

$bottom = $ft + 960
Write-Output "operations : $($ops.Count)  (colour: $($col.Count))"
Write-Output "content bottom : $bottom   section h: $SECH   printable h: $PH"
Write-Output "page fill      : $([math]::Round(100 * $bottom / $PH, 1))%   headroom: $($SECH - $bottom)"
if ($bottom -gt $SECH) { Write-Warning "content overflows the section by $($bottom - $SECH) twips" }
if ($SECH -gt $PH)     { Write-Warning "section taller than printable height by $($SECH - $PH) twips" }
