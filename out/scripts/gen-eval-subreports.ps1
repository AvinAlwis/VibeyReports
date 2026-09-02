# Lays out the six table sub-reports of the Individual Detailed Evaluation Report.
#
# Input:  out/reports/_shell_<name>.rpt   (blank shells, each bound to one procedure)
# Output: out/reports/PMSV10_IndDetEval_<Name>.rpt
#
# These are sub-reports, so they carry NO page furniture of their own - no title, no
# footer, no margins decoration. Just column headings (Page Header) and one bound row
# (Details), which Crystal repeats inside the host report's frame. ReportHeader,
# PageFooter and ReportFooter are collapsed to 0 so a sub-report adds no vertical
# padding of its own.
#
# Every column set sums to exactly 11186 twips - the A4 printable width the host uses.

$W      = 11186
$INK    = '#1F2937'
$BORDER = '#D5D9DE'
$THEAD  = '#DCE3EA'

# name  = shell/output suffix
# alias = the procedure's table alias inside that shell
# cols  = label, field, width
$reports = @(
  @{ name='Stages';  alias='sp_perf_detailed_eval_stages;1'
     cols=@(
       @{ h='Stage Name';      f='stage_name';          w=2900 },
       @{ h='Stage Period';    f='stage_period';        w=2300 },
       @{ h='Status';          f='stage_status';        w=1400 },
       @{ h='Outcome / Score'; f='stage_outcome_score'; w=1700 },
       @{ h='Comment';         f='stage_comment';       w=2886 }) },

  # The spec says comments are not shown inside the rating table, but the Final
  # Evaluation Comments section it wants instead is fed by this same procedure and
  # the supplied mockup does show them in-table. Kept here so all three comments are
  # rendered somewhere; split them out if the reviewed layout demands it.
  @{ name='Feedback'; alias='sp_perf_detailed_eval_feedback;1'
     cols=@(
       @{ h='Evaluator';           f='evaluator'; w=2000 },
       @{ h='Goal Rating';         f='goal_rate'; w=1700 },
       @{ h='Competency Rating';   f='comp_rate'; w=2000 },
       @{ h='Comments';            f='comment_text'; w=5486 }) },

  @{ name='FeedbackDetails'; alias='sp_perf_detailed_eval_fdp;1'
     cols=@(
       @{ h='Achievement Description'; f='achievement_description'; w=3000 },
       @{ h='Learning Description';    f='learning_description';    w=3000 },
       @{ h="Appraiser Comment";       f='appraiser_comment';       w=2600 },
       @{ h="Reviewer Comment";        f='reviewer_comment';        w=2586 }) },

  @{ name='DevelopmentPlan'; alias='sp_perf_detailed_eval_fdp_dev;1'
     cols=@(
       @{ h='Development Area';     f='development_area';    w=3000 },
       @{ h='Learning Description'; f='learning_description'; w=3000 },
       @{ h="Appraiser Comment";    f='appraiser_comment';   w=2600 },
       @{ h="Reviewer Comment";     f='reviewer_comment';    w=2586 }) },

  @{ name='JobPreferences'; alias='sp_perf_detailed_eval_fdp_job;1'
     cols=@(
       @{ h='Preferred Position / Function'; f='preferred_position_function'; w=2500 },
       @{ h='Preferred Location';            f='preferred_location';          w=1900 },
       @{ h='Reason';                        f='reason';                      w=2400 },
       @{ h="Appraiser Comment";             f='appraiser_comment';           w=2200 },
       @{ h="Reviewer Comment";              f='reviewer_comment';            w=2186 }) },

  @{ name='TrainingNeeds'; alias='sp_perf_detailed_eval_fdp_tn;1'
     cols=@(
       @{ h='Training Need Type'; f='training_need_type';  w=1700 },
       @{ h='Training Need';      f='training_need';       w=1800 },
       @{ h='Objective';          f='objective';           w=1900 },
       @{ h='Relevance to Job';   f='relevance_to_job';    w=1900 },
       @{ h='Benefit to You';     f='benefit_to_you';      w=1900 },
       @{ h='Benefit to Company'; f='benefit_to_company';  w=1986 }) }
)

$shellFor = @{ Stages='stages'; Feedback='feedback'; FeedbackDetails='fdp';
               DevelopmentPlan='fdp_dev'; JobPreferences='fdp_job'; TrainingNeeds='fdp_tn' }

$PH = 'PageHeaderSection1'
$DT = 'DetailSection1'
$hdrH = 480
$rowH = 1100          # generous: comment columns carry long free text

foreach ($rep in $reports) {
  $ops = New-Object System.Collections.ArrayList
  $col = New-Object System.Collections.ArrayList
  function Op($h)        { [void]$script:ops.Add($h) }
  function Ink($n, $c)   { [void]$script:col.Add(@{ action='setTextColor'; target=$n; color=$c }) }
  function Fill($n, $c)  { [void]$script:col.Add(@{ action='setFillColor'; target=$n; color=$c }) }
  function Stroke($n,$c) { [void]$script:col.Add(@{ action='setLineColor'; target=$n; color=$c }) }
  $script:ops = $ops; $script:col = $col

  $cols = $rep.cols
  $alias = $rep.alias

  # running x offsets, and a total check
  $x = @(0); for ($i=0; $i -lt ($cols.Count-1); $i++) { $x += ($x[$i] + $cols[$i].w) }
  $total = $x[$cols.Count-1] + $cols[$cols.Count-1].w
  if ($total -ne $W) { Write-Output ("!! {0}: columns sum to {1}, expected {2}" -f $rep.name, $total, $W); continue }

  Op @{ action='resizeSection'; section='ReportHeaderSection1'; heightTwips=0 }
  Op @{ action='resizeSection'; section='PageFooterSection1';   heightTwips=0 }
  Op @{ action='resizeSection'; section='ReportFooterSection1'; heightTwips=0 }
  Op @{ action='resizeSection'; section=$PH; heightTwips=$hdrH }
  Op @{ action='resizeSection'; section=$DT; heightTwips=$rowH }

  # ---- Page Header: column headings -------------------------------------------
  Op @{ action='addBox'; section=$PH; newName='PHFill'; leftTwips=0; topTwips=0; widthTwips=$W; heightTwips=$hdrH }
  Fill 'PHFill' $THEAD; Stroke 'PHFill' $BORDER

  for ($c=0; $c -lt $cols.Count; $c++) {
    $n = "PHH$c"
    Op @{ action='addText'; section=$PH; newName=$n; text=$cols[$c].h;
          leftTwips=($x[$c]+70); topTwips=130; widthTwips=($cols[$c].w-140); heightTwips=260 }
    Op @{ action='setFontSize'; target=$n; fontSizePt=8 }
    Op @{ action='setBold'; target=$n; bold=$true }
    if ($c -eq 0) { Op @{ action='setFont'; target=$n; fontName='Segoe UI' } }   # section font anchor
    Ink $n $INK
    if ($c -gt 0) {
      Op @{ action='addLine'; section=$PH; newName="PHV$c"; leftTwips=$x[$c]; topTwips=0; widthTwips=0; heightTwips=$hdrH }
      Stroke "PHV$c" $BORDER
    }
  }

  # ---- Details: one bound row --------------------------------------------------
  #
  # THE ROW IS RULED BY THE CELLS' OWN BORDERS, NOT BY DRAWN LINES.
  #
  # This row grows: the comment columns are NVARCHAR(2000) and setCanGrow lets a
  # cell expand to several times $rowH. A Line has no can-grow, so the divider and
  # rule this used to draw stayed at the original height while the text ran on past
  # them, and the table looked like it was leaking out of its own ruling.
  # A border belongs to the object, so it grows with it.
  #
  # Consequence: each cell now spans its WHOLE column (x .. x+w) instead of sitting
  # inset by 70 twips. Inset cells would leave a 140-twip gap between neighbouring
  # borders, which reads as separate boxes rather than a table. The trade is that
  # text starts hard against the left border - Crystal fields have no padding.
  for ($c=0; $c -lt $cols.Count; $c++) {
    $n = "DR$c"
    Op @{ action='addField'; section=$DT; newName=$n; fieldRef=("{" + $alias + "." + $cols[$c].f + "}");
          leftTwips=$x[$c]; topTwips=0; widthTwips=$cols[$c].w; heightTwips=$rowH }
    Op @{ action='setFontSize'; target=$n; fontSizePt=8 }
    Op @{ action='setCanGrow'; target=$n; canGrow=$true }
    if ($c -eq 0) { Op @{ action='setFont'; target=$n; fontName='Segoe UI' } }   # section font anchor
    Ink $n $INK
    [void]$col.Add(@{ action='setBorder'; target=$n
                      left='single'; right='single'; bottom='single'; color=$BORDER })
  }

  foreach ($c in $col) { Op $c }

  $shell = $shellFor[$rep.name]
  $plan = @{
    command    = 'apply'
    reportPath = "D:/VibeyReports/out/reports/_shell_$shell.rpt"
    outputPath = "D:/VibeyReports/out/reports/PMSV10_IndDetEval_$($rep.name).rpt"
    overwrite  = $true
    plan       = @{ planVersion = 1; operations = $ops }
  }
  $json = "D:\VibeyReports\out\json\eval-sub-$($rep.name).json"
  [IO.File]::WriteAllText($json, ($plan | ConvertTo-Json -Depth 12 -Compress))
  Write-Output ("{0,-16} {1} cols  {2} ops  width {3}" -f $rep.name, $cols.Count, $ops.Count, $total)
}
