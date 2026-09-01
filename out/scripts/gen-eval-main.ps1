# Lays out the Individual Detailed Evaluation Report main report and embeds its
# seven sub-reports.
#
# Input:  out/reports/_shell_overview.rpt  (blank, bound to sp_perf_detailed_eval_overview)
# Output: out/reports/PMSV10_IndDetailedEval.rpt
#
# WHY TWO SECTIONS, NOT ONE
# A Crystal section cannot exceed one printable page (16118 twips on A4) and the
# validator enforces it. The report is two pages of content (~25000 twips), so it
# cannot live in one section. Page 1 goes in the Report Header, page 2 in Details.
#
# Consequence worth knowing: the Report Header renders ONCE per report, so this
# shape is right for "one report instance per selected employee" (which is how the
# spec describes bulk generation, and the only shape for Self Service). It will NOT
# repeat page 1 per employee if several employees are rendered into one file - that
# needs a Group Header, which no Vibey Reports operation can create.
#
# OTHER KNOWN GAPS, both needing the Designer (no operation exists):
#   * No explicit page break; Crystal breaks when the Report Header is full.
#   * No page-number field ("Page 1 of 2") - that is a Special Field.

$SRC = 'sp_perf_detailed_eval_overview;1'
$W   = 11186
$P1  = 'ReportHeaderSection1'      # page 1
$P2  = 'DetailSection1'            # page 2

$INK    = '#1F2937'
$MUTED  = '#6B7280'
$BORDER = '#D5D9DE'
$BARCLR = '#263445'
$BARTXT = '#FFFFFF'
$PANEL  = '#F7F9FB'

$ops = New-Object System.Collections.ArrayList
$col = New-Object System.Collections.ArrayList
$SEC = $P1
$bandSeq = 0

function Op($h)        { [void]$script:ops.Add($h) }
function Ink($n,$c)    { [void]$script:col.Add(@{ action='setTextColor'; target=$n; color=$c }) }
function Fill($n,$c)   { [void]$script:col.Add(@{ action='setFillColor'; target=$n; color=$c }) }
function Stroke($n,$c) { [void]$script:col.Add(@{ action='setLineColor'; target=$n; color=$c }) }

function Txt($n,$t,$l,$tp,$w,$h,$pt,$bold) {
  Op @{ action='addText'; section=$script:SEC; newName=$n; text=$t; leftTwips=$l; topTwips=$tp; widthTwips=$w; heightTwips=$h }
  Op @{ action='setFontSize'; target=$n; fontSizePt=$pt }
  if ($bold) { Op @{ action='setBold'; target=$n; bold=$true } }
}
function Fld($n,$f,$l,$tp,$w,$h,$pt) {
  Op @{ action='addField'; section=$script:SEC; newName=$n; fieldRef=("{" + $SRC + "." + $f + "}");
        leftTwips=$l; topTwips=$tp; widthTwips=$w; heightTwips=$h }
  Op @{ action='setFontSize'; target=$n; fontSizePt=$pt }
}
function Ln($n,$l,$tp,$w,$h) {
  Op @{ action='addLine'; section=$script:SEC; newName=$n; leftTwips=$l; topTwips=$tp; widthTwips=$w; heightTwips=$h }
  Stroke $n $BORDER
}
function Bx($n,$tp,$h) {
  Op @{ action='addBox'; section=$script:SEC; newName=$n; leftTwips=0; topTwips=$tp; widthTwips=$W; heightTwips=$h }
  Fill $n $PANEL; Stroke $n $BORDER
}
# Dark banner with white caption. Counter is $bandSeq, NOT $bar: PowerShell
# variable names are case-insensitive, so a $bar counter and a $BAR colour are
# the same variable and the counter silently overwrites the colour.
function Band($caption, $tp) {
  $script:bandSeq++
  $b = "Bar$script:bandSeq"; $c = "BarT$script:bandSeq"
  Op @{ action='addBox'; section=$script:SEC; newName=$b; leftTwips=0; topTwips=$tp; widthTwips=$W; heightTwips=340 }
  Fill $b $BARCLR; Stroke $b $BARCLR
  Txt $c $caption 150 ($tp+95) 8000 220 9 $true
  Ink $c $BARTXT
  return ($tp + 340)
}

# Sections must be grown BEFORE anything is placed in them: the validator checks
# each add against the height simulated at that point in the plan.
Op @{ action='resizeSection'; section=$P1; heightTwips=12600 }
Op @{ action='resizeSection'; section=$P2; heightTwips=13100 }
Op @{ action='resizeSection'; section='PageHeaderSection1';   heightTwips=0 }
Op @{ action='resizeSection'; section='PageFooterSection1';   heightTwips=0 }
Op @{ action='resizeSection'; section='ReportFooterSection1'; heightTwips=0 }

# =============================== PAGE 1 =======================================
$SEC = $P1

# ---- Report header block ------------------------------------------------------
Txt 'SysTitle' 'PeoplesHR - Performance Management' 1900 60 6000 250 9 $false
Ink 'SysTitle' $MUTED
Txt 'RptTitle' 'Individual Detailed Evaluation Report' 1900 330 7000 380 15 $true
Ink 'RptTitle' $INK
Txt 'Conf' 'CONFIDENTIAL - Performance Evaluation' 1900 780 6000 230 8 $false
Ink 'Conf' $MUTED
Ln 'HdrRule' 0 1130 $W 0

# ---- Employee Details ---------------------------------------------------------
$y = Band 'EMPLOYEE DETAILS' 1250
Bx 'EmpBox' $y 2160
# Location was removed from this report: it had no source column (the spec's
# emp_work_station does not exist on hs_hr_employee) and was dropped from the
# spec too. Five fields remain, so the last row carries only a left-hand pair.
$rows = @(
  @{ lk='Employee Name'; lf='employee_name';      rk='Designation'; rf='designation' },
  @{ lk='Employee ID';   lf='emp_display_number'; rk='Department';  rf='department' },
  @{ lk='Reporting Manager'; lf='reporting_manager'; rk=''; rf='' })
for ($i=0; $i -lt 3; $i++) {
  $ry = $y + 180 + ($i * 660)
  Txt "EL$i" $rows[$i].lk 150 $ry 2100 230 8 $false;  Ink "EL$i" $MUTED
  Fld "EV$i" $rows[$i].lf 2350 $ry 2450 260 9;        Ink "EV$i" $INK
  if ($rows[$i].rk -ne '') {
    Txt "ER$i" $rows[$i].rk 5800 $ry 2100 230 8 $false; Ink "ER$i" $MUTED
    Fld "ERV$i" $rows[$i].rf 8000 $ry 3036 260 9;       Ink "ERV$i" $INK
  }
  if ($i -lt 2) { Ln "EDiv$i" 150 ($ry+480) 10886 0 }
}
$y = $y + 2160

# ---- Evaluation Cycle Details -------------------------------------------------
$y = Band 'EVALUATION CYCLE DETAILS' ($y + 200)
Bx 'CycBox' $y 1500
$crows = @(
  @{ lk='Evaluation Cycle';  lf='cycle_name';        rk='Appraiser Name'; rf='appraiser_name' },
  @{ lk='Evaluation Period'; lf='evaluation_period'; rk='Reviewer Name';  rf='reviewer_name' })
for ($i=0; $i -lt 2; $i++) {
  $ry = $y + 180 + ($i * 660)
  Txt "CL$i" $crows[$i].lk 150 $ry 2100 230 8 $false;  Ink "CL$i" $MUTED
  Fld "CV$i" $crows[$i].lf 2350 $ry 2450 260 9;        Ink "CV$i" $INK
  Txt "CR$i" $crows[$i].rk 5800 $ry 2100 230 8 $false; Ink "CR$i" $MUTED
  Fld "CRV$i" $crows[$i].rf 8000 $ry 3036 260 9;       Ink "CRV$i" $INK
  if ($i -eq 0) { Ln 'CDiv0' 150 ($ry+480) 10886 0 }
}
$y = $y + 1500

# ---- Overall Performance Summary - four tiles ---------------------------------
# NOTE: the spec asks for "Appraiser Final" and "Reviewer Final" as single numbers,
# but perf_cp_stage_score stores goal and competency rates separately with no
# combined figure. The goal rate is shown; confirm the intended combination.
$y = Band 'OVERALL PERFORMANCE SUMMARY' ($y + 200)
Bx 'TileBox' $y 1200
$tiles = @(
  @{ lbl='FINAL OVERALL RATING'; fld='final_rating_descriptor' },
  @{ lbl='FINAL OVERALL SCORE';  fld='overall_score' },
  @{ lbl='APPRAISER FINAL';      fld='mgr_goal_rate' },
  @{ lbl='REVIEWER FINAL';       fld='reviewer_goal_rate' })
$tw = [int](($W - 4) / 4)
for ($i=0; $i -lt 4; $i++) {
  $tx = $i * $tw
  Txt "TL$i" $tiles[$i].lbl ($tx+80) ($y+200) ($tw-160) 220 8 $false
  Ink "TL$i" $MUTED
  Op @{ action='setAlignment'; target="TL$i"; alignment='Centre' }
  Fld "TV$i" $tiles[$i].fld ($tx+80) ($y+560) ($tw-160) 420 14
  Ink "TV$i" $INK
  Op @{ action='setAlignment'; target="TV$i"; alignment='Centre' }
  Op @{ action='setBold'; target="TV$i"; bold=$true }
  if ($i -gt 0) { Ln "TDiv$i" $tx ($y+120) 0 960 }
}
$y = $y + 1200

# ---- Overall Performance Summary Comment --------------------------------------
$y = Band 'OVERALL PERFORMANCE SUMMARY COMMENT' ($y + 200)
Bx 'ComBox' $y 1000
Fld 'ComVal' 'overall_summary_comment' 150 ($y+150) 10886 700 9
Ink 'ComVal' $INK
$y = $y + 1000

# ---- Stage-Wise Evaluation Summary (sub-report) -------------------------------
$y = Band 'STAGE-WISE EVALUATION SUMMARY' ($y + 200)
Op @{ action='addSubreport'; section=$SEC; newName='StageWise';
      reportPath='D:/VibeyReports/out/reports/PMSV10_IndDetEval_Stages.rpt'
      leftTwips=0; topTwips=$y; widthTwips=$W; heightTwips=2400 }
Op @{ action='setSubreportLink'; target='StageWise'
      mainReportField=("{" + $SRC + ".performance_cycle_id}")
      subreportField='{sp_perf_detailed_eval_stages;1.performance_cycle_id}' }
Op @{ action='setSubreportLink'; target='StageWise'
      mainReportField=("{" + $SRC + ".emp_number}")
      subreportField='{sp_perf_detailed_eval_stages;1.emp_number}' }
$y = $y + 2400

# Company logo, top-left of page 1. No links - the procedure takes no parameters.
#
# This MUST point at the laid-out logo report, not the raw shell. A sub-report
# renders at the height of ITS OWN sections, not the height given here: pointing
# at an unlaid-out shell embeds five default-height empty sections, which is what
# made the logo overflow down across the Employee Details panel. The laid-out
# version collapses every section except Details and holds only the blob field.
# Inset from the page edge: at leftTwips=0 the sub-report's own border sits on the
# printable boundary and the left edge is clipped. 150 matches the left inset used
# by the text inside every panel below.
Op @{ action='addSubreport'; section=$SEC; newName='CompanyLogo';
      reportPath='D:/VibeyReports/out/reports/PMSV10_IndDetEval_Logo.rpt'
      leftTwips=150; topTwips=100; widthTwips=1600; heightTwips=700 }

$p1h = $y + 150

# =============================== PAGE 2 =======================================
$SEC = $P2
$y = 0

# Two field-to-field links per sub-report. Both sides RETURN these columns - that
# is why every procedure selects performance_cycle_id and emp_number. Types must
# match too: Crystal refuses a String-to-Number pair with COM "Invalid value type".
$subs = @(
  @{ n='RatingCmp';  cap='FEEDBACK & RATING COMPARISON';                   h=2200; file='PMSV10_IndDetEval_Feedback.rpt';        alias='sp_perf_detailed_eval_feedback;1' },
  @{ n='FdpDetails'; cap='FEEDBACK & DEVELOPMENT PLAN - FEEDBACK DETAILS'; h=2000; file='PMSV10_IndDetEval_FeedbackDetails.rpt'; alias='sp_perf_detailed_eval_fdp;1' },
  @{ n='FdpDev';     cap='FEEDBACK & DEVELOPMENT PLAN - DEVELOPMENT PLAN'; h=2000; file='PMSV10_IndDetEval_DevelopmentPlan.rpt'; alias='sp_perf_detailed_eval_fdp_dev;1' },
  @{ n='FdpJob';     cap='ADDITIONAL FEEDBACK - JOB PREFERENCES';          h=2000; file='PMSV10_IndDetEval_JobPreferences.rpt';  alias='sp_perf_detailed_eval_fdp_job;1' },
  @{ n='FdpTn';      cap='ADDITIONAL FEEDBACK - TRAINING NEEDS';           h=2000; file='PMSV10_IndDetEval_TrainingNeeds.rpt';   alias='sp_perf_detailed_eval_fdp_tn;1' })

foreach ($s in $subs) {
  $y = Band $s.cap $y
  Op @{ action='addSubreport'; section=$SEC; newName=$s.n;
        reportPath=("D:/VibeyReports/out/reports/" + $s.file)
        leftTwips=0; topTwips=$y; widthTwips=$W; heightTwips=$s.h }
  Op @{ action='setSubreportLink'; target=$s.n
        mainReportField=("{" + $SRC + ".performance_cycle_id}")
        subreportField=("{" + $s.alias + ".performance_cycle_id}") }
  Op @{ action='setSubreportLink'; target=$s.n
        mainReportField=("{" + $SRC + ".emp_number}")
        subreportField=("{" + $s.alias + ".emp_number}") }
  $y = $y + $s.h + 200
}
$p2h = $y

foreach ($c in $col) { Op $c }

# Trim both sections to what was actually used.
Op @{ action='resizeSection'; section=$P1; heightTwips=$p1h }
Op @{ action='resizeSection'; section=$P2; heightTwips=$p2h }

$plan = @{
  command    = 'apply'
  reportPath = 'D:/VibeyReports/out/reports/_shell_overview.rpt'
  outputPath = 'D:/VibeyReports/out/reports/PMSV10_IndDetailedEval.rpt'
  overwrite  = $true
  plan       = @{ planVersion = 1; operations = $ops }
}
[IO.File]::WriteAllText('D:\VibeyReports\out\json\eval-main.json', ($plan | ConvertTo-Json -Depth 12 -Compress))

Write-Output ("operations : {0}  (colour {1})" -f $ops.Count, $col.Count)
Write-Output ("page 1     : {0} twips  (limit 16118)" -f $p1h)
Write-Output ("page 2     : {0} twips  (limit 16118)" -f $p2h)
