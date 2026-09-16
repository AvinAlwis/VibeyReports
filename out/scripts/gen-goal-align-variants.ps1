# Produces the two DELIVERABLE variants of Report 2 - Goal Alignment & Cascading Report.
#
#   out/archive/reports/PMSV10_GoalAlignCascade.base.rpt   (base: laid out + detail sub-report embedded)
#     -> out/reports/PMSV10_GoalAlignCascade.rpt      Administrator variant
#     -> out/reports/PMSV10_GoalAlignCascadeSS.rpt    Employee Self Service variant
#
# The spec (docs/solution/8-Reports/2-Goal Alignment & Cascading Report.md) says the two
# variants share the SAME layout and IDENTICAL content - only the Report Navigator parameter
# form differs, and that lives in HS_HR_RN_PAR_DEF, not in the .rpt. Both bind to
# sp_perf_goal_align_cascade. So the two files are produced from one base by one plan, applied
# twice, which is what keeps "identical content" true by construction rather than by hand.
#
# Two layout changes over the base, both spec conformance:
#
#   1. The header's **Generated timestamp**, which the spec's header row requires and the base
#      did not carry. Deliberately NOT re-adding the Report ID or the "Crystal Reports [.rpt]"
#      format label - the spec removes both for this report ("no Report ID and no
#      report-format label").
#
#   2. The legend's tick and cross are split into their own text objects set to **Segoe UI
#      Symbol**. U+2713 / U+2717 are not in Segoe UI, so as part of the legend sentence they
#      rendered as tofu boxes in the exported PDF. The .rpt stored the right characters all
#      along - it is the font that could not draw them.

$ErrorActionPreference = 'Stop'

$root   = 'D:/VibeyReports'
$worker = "$root/dist/worker/VibeyReports.CrystalWorker.exe"
$base   = "$root/out/archive/reports/PMSV10_GoalAlignCascade.base.rpt"

$MUTED = '#6B7280'; $GREEN = '#1E8E4E'; $RED = '#C0392B'
$RH = 'ReportHeaderSection1'; $RF = 'ReportFooterSection1'
$SYMBOL = 'Segoe UI Symbol'

$ops = New-Object System.Collections.ArrayList
function Op($h) { [void]$ops.Add($h) }

# --------------------------------------------------------------------------------------------
# 1. Header - Generated timestamp, grouped tight against the right margin (body ends at 11186).
#    FontAnchor (the system title) ends at 6100, so this strip starts well clear of it.
#
#    "Generated" is right-aligned so it ends at a known x; the date and time are LEFT-aligned so
#    each starts at a known x. That is what makes the two gaps fixed and small (150 twips). With
#    all three right-aligned the text's left edge floated on its own width, which is what left
#    the first version reading "Generated        03/09/2026        16:19:05".
# --------------------------------------------------------------------------------------------
Op @{ action='addText'; section=$RH; newName='MetaGenLbl'; text='Generated'
      leftTwips=7800; topTwips=70; widthTwips=1350; heightTwips=200 }          # ends 9150
Op @{ action='addSpecialField'; section=$RH; newName='MetaGenDate'; specialType='printDate'
      leftTwips=9300; topTwips=70; widthTwips=1000; heightTwips=200 }          # starts 9300
Op @{ action='addSpecialField'; section=$RH; newName='MetaGenTime'; specialType='printTime'
      leftTwips=10330; topTwips=70; widthTwips=856; heightTwips=200 }          # starts 10330
foreach ($n in 'MetaGenLbl','MetaGenDate','MetaGenTime') {
    Op @{ action='setFontSize';  target=$n; fontSizePt=8 }
    Op @{ action='setTextColor'; target=$n; color=$MUTED }
}
Op @{ action='setAlignment'; target='MetaGenLbl';  alignment='Right' }
Op @{ action='setAlignment'; target='MetaGenDate'; alignment='Left' }
Op @{ action='setAlignment'; target='MetaGenTime'; alignment='Left' }

# --------------------------------------------------------------------------------------------
# 1b. Overview tiles - make the numbers actually centre.
#
# The three tile fields were already alignment=Centre and still rendered off-centre, because
# EnableSystemDefault was left TRUE on their number format. sdk-notes.md records what that gate
# does: Crystal formats from locale defaults and silently discards the field's own settings, so
# the glyphs are laid out against a format nobody in this report chose. setNumberFormat clears
# the gate as its first act, which is what makes the centring stick.
#
# These are counts - no decimals, no thousands separator.
# --------------------------------------------------------------------------------------------
foreach ($t in 'B2T0num','B2T1num','B2T3num') {
    Op @{ action='setNumberFormat'; target=$t; decimalPlaces=0; thousandsSeparator=$false }
    Op @{ action='setAlignment';    target=$t; alignment='Centre' }
}

# --------------------------------------------------------------------------------------------
# 1c. Company logo, as a SUB-REPORT rather than a second table on the main report.
#
# Report Navigator's SP mode pushes one DataTable into the main report, so a second main-report
# table never receives data and Crystal falls back to its own connection ("Database logon
# failed"). A sub-report gets its own push, keyed by name from HS_HR_RN_SUBRPT_SP_MAP - which is
# how PMSV10_IndPerfOverview.rpt has always carried its logo.
#
# Requires, on the database side:
#   * sp_perf_company_logo taking the same five parameters as the others, because
#     OraBuildSubReport binds every registered parameter to every sub-report procedure
#   * INSERT HS_HR_RN_SUBRPT_SP_MAP ('660755', 'companylogo', 'sp_perf_company_logo')
#     - RN_SUBRPT_NAME must equal the sub-report's ReportDocument.Name, i.e. 'companylogo'
#
# The blob field it replaces is removed explicitly rather than left to stage 2's removeTable
# cascade, so the two never overlap in the header.
# --------------------------------------------------------------------------------------------
Op @{ action='removeObject'; target='HIELOGOIMAGE1' }
Op @{ action='addSubreport'; section=$RH; newName='companylogo'
      reportPath="$root/out/archive/reports/PMSV10_GoalAlignLogo.rpt"
      leftTwips=0; topTwips=60; widthTwips=800; heightTwips=640 }

# --------------------------------------------------------------------------------------------
# 2. Re-import the detail sub-report WITHOUT field links.
#
# A Crystal sub-report link compiles into hidden "Pm-<main field>" parameter fields inside the
# sub-report, which Crystal fills during its own linkage pass. Report Navigator's SP mode
# (RN_SP = '1') never runs that pass - SPViewer.BindReport executes the stored procedures itself
# and pushes DataTables with SetDataSource - so those parameters stay empty and Export throws
# "Missing parameter values". Measured on the linked build:
#
#   Pm-sp_perf_goal_align_cascade;1.performance_cycle_id   report='GoalDetail'  hasValue=False
#   Pm-sp_perf_goal_align_cascade;1.emp_number             report='GoalDetail'  hasValue=False
#
# PMSV10_IndPerfOverview.rpt - the one PMS report that renders in the viewer today - has no
# sub-report parameters at all, which is the shape this now matches.
#
# The links are removed by DROPPING the sub-report and re-importing it, not by clearing the link
# collection: SubreportController.SetSubreportLinks with an empty SubreportLinksClass sends
# Crystal XI R2 into an infinite loop (measured: ~85% CPU, 400+ CPU-seconds, no completion, no
# throw). Never call it that way. See docs/sdk-notes.md.
#
# Per-employee scoping moves into the sub-report's own procedure, which now takes the same five
# parameters as the main one and filters on @emp_number_list.
Op @{ action='removeObject'; target='Subreport1' }
Op @{ action='addSubreport'; section='DetailSection1'; newName='GoalDetail'
      reportPath="$root/out/archive/reports/PMSV10_GoalAlignDetail.rpt"
      leftTwips=0; topTwips=0; widthTwips=11186; heightTwips=880 }

# --------------------------------------------------------------------------------------------
# 3. Alignment legend - glyph in its own symbol-font object, sentence in the section font.
# --------------------------------------------------------------------------------------------
Op @{ action='removeObject'; target='LegAligned' }
Op @{ action='removeObject'; target='LegNotAligned' }

$legend = @(
  @{ glyph=([char]0x2713); name='Aligned';     colour=$GREEN; top=680
     text='Aligned (Aligned to Strategic Goal) ' + ([char]0x2014) + ' cascaded from the company objective hierarchy' },
  @{ glyph=([char]0x2717); name='NotAligned';  colour=$RED;   top=1010
     text='Not Aligned ' + ([char]0x2014) + ' created outside the objective hierarchy for individual/team contribution' }
)
foreach ($l in $legend) {
    $g = "LegGlyph$($l.name)"; $t = "LegText$($l.name)"
    Op @{ action='addText'; section=$RF; newName=$g; text=$l.glyph
          leftTwips=200; topTwips=$l.top; widthTwips=300; heightTwips=240 }
    Op @{ action='setFont';      target=$g; fontName=$SYMBOL }
    Op @{ action='setFontSize';  target=$g; fontSizePt=8 }
    Op @{ action='setBold';      target=$g; bold=$true }
    Op @{ action='setTextColor'; target=$g; color=$l.colour }

    Op @{ action='addText'; section=$RF; newName=$t; text=$l.text
          leftTwips=560; topTwips=$l.top; widthTwips=9600; heightTwips=240 }
    Op @{ action='setFontSize';  target=$t; fontSizePt=8 }
    Op @{ action='setBold';      target=$t; bold=$true }
    Op @{ action='setTextColor'; target=$t; color=$l.colour }
}

# --------------------------------------------------------------------------------------------
# The request must survive the pipe into a native x86 process, and PowerShell 5.1 re-encodes
# that pipe with the console code page - which would mangle the tick, the cross and the em dash.
# Escaping every non-ASCII character to \uXXXX makes the payload pure ASCII, so the encoding of
# the pipe stops mattering; the worker's JSON reader turns them back into the real characters.
# --------------------------------------------------------------------------------------------
function ConvertTo-AsciiJson($Object) {
    $json = $Object | ConvertTo-Json -Depth 12 -Compress
    ($json.ToCharArray() | ForEach-Object {
        if ([int]$_ -lt 128) { $_ } else { '\u{0:x4}' -f [int]$_ }
    }) -join ''
}

# STATUS_HEAP_CORRUPTION is a known, INTERMITTENT failure of this Crystal build: the worker
# dies with exit 0xC0000374 and returns nothing at all. Measured today on the same unchanged
# plan - failed, then succeeded on the next run, then failed four times in a row. It is made
# more likely by how much COM string writing a plan does (setNumberFormat now writes three
# format strings), but it predates that and is not caused by it.
#
# A crashed worker writes NOTHING - the output file is only produced by a successful SaveAs -
# so re-running the identical request is safe and is the only thing that helps.
$HEAP_CORRUPTION = -1073740940   # 0xC0000374

function Invoke-Worker($Label, $Request, [int]$Attempt = 1) {
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
    $p.StandardInput.Write((ConvertTo-AsciiJson $Request))
    $p.StandardInput.Close()
    $raw = $p.StandardOutput.ReadToEnd()
    $err = $p.StandardError.ReadToEnd()
    $p.WaitForExit()

    # A crashed worker writes nothing to stdout, so ConvertFrom-Json yields $null and every
    # property read off it is empty - which made an earlier failure surface as a bare label with
    # no message at all. Report the raw streams when there is no parsable response.
    $res = if ([string]::IsNullOrWhiteSpace($raw)) { $null } else { $raw | ConvertFrom-Json }
    if ($null -eq $res) {
        if ($p.ExitCode -eq $HEAP_CORRUPTION -and $Attempt -lt 4) {
            Write-Output ("  {0}: worker died with STATUS_HEAP_CORRUPTION, retry {1}/3" -f $Label, $Attempt)
            return Invoke-Worker $Label $Request ($Attempt + 1)
        }
        throw "$Label`: the worker returned no response (exit $($p.ExitCode)).`nstderr:`n$err"
    }
    if (-not $res.ok) {
        $detail = $res.error
        if ($res.validationErrors) {
            $detail += "`n" + (($res.validationErrors | ForEach-Object { "  [op $($_.operationIndex)] $($_.message)" }) -join "`n")
        }
        throw "$Label`: $detail`n$err"
    }
    return $res
}

$variants = @(
  @{ label = 'Administrator';         out = "$root/out/reports/PMSV10_GoalAlignCascade.rpt"   },
  @{ label = 'Employee Self Service'; out = "$root/out/reports/PMSV10_GoalAlignCascadeSS.rpt" }
)

foreach ($v in $variants) {
    # STAGE 1 - the layout plan, offline, base -> temp.
    $tmp = "$root/out/archive/_stage1_" + (Split-Path $v.out -Leaf)
    $r1 = Invoke-Worker $v.label @{
        command    = 'apply'
        reportPath = $base
        outputPath = $tmp
        overwrite  = $true
        plan       = @{ planVersion = 1; operations = $ops.ToArray() }
    }

    # Crystal gives a placed sub-report a single black border by default, which draws a box around
    # the logo and a second rule around the details table. Clearing it has to happen in a LATER
    # apply than the addSubreport, because the name it has to target does not exist while the plan
    # that creates it is being validated: ImportSubreportEx's newName becomes the sub-report's
    # SubreportName, while the placed OBJECT is auto-numbered by Crystal (sdk-notes.md). So the
    # names are read back off stage 1's output rather than assumed.
    $stage2 = New-Object System.Collections.ArrayList
    foreach ($sec in $r1.schema.sections) {
        foreach ($o in $sec.objects) {
            if ($o.kind -eq 'Subreport') {
                [void]$stage2.Add(@{ action='setBorder'; target=$o.name
                                     left='none'; right='none'; top='none'; bottom='none' })
            }
        }
    }

    # STAGE 2 - drop the company-logo procedure, temp -> final.
    #
    # Report Navigator's SP mode pushes ONE DataTable into the main report
    # (SPViewer.BindReport: m_CrDoc.SetDataSource(rptDataSet.Tables[0])). A second table on the
    # main report is therefore never given data, and Crystal falls back to opening its own
    # connection - which fails in the viewer as "Database logon failed". Measured: with this
    # table present the export fails; with it gone, and both the main and sub-report result sets
    # pushed, it succeeds.
    #
    # This is a SEPARATE apply on purpose. removeTable cascades (it deletes the fields bound to
    # the table - here HIELOGOIMAGE1), and running the layout plan against an already-cascaded
    # report crashed the worker outright with STATUS_HEAP_CORRUPTION (0xC0000374). Layout first,
    # then the cascade, is the order that holds.
    #
    # It also needs VIBEY_DB_PASSWORD: removeTable contacts the server, and Crystal persists a
    # connection's user name but never its password.
    #
    # The logo is NOT restored here. Putting it back means making it a SUB-report bound to
    # sp_perf_company_logo and registering it in HS_HR_RN_SUBRPT_SP_MAP, the way
    # PMSV10_IndPerfOverview.rpt does - and that procedure would first have to take the same
    # five parameters as the others, because OraBuildSubReport passes every registered parameter
    # to every sub-report procedure.
    $r2 = Invoke-Worker $v.label @{
        command    = 'apply'
        reportPath = $tmp
        outputPath = $v.out
        overwrite  = $true
        plan       = @{ planVersion = 1
                        operations = @($stage2.ToArray()) + @(@{ action='removeTable'; target='sp_perf_company_logo;1' }) }
    }
    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue

    Write-Output ("{0,-22} -> {1}  ({2} layout ops, dropped {3})" -f `
        $v.label, (Split-Path $v.out -Leaf), $r1.operationsApplied, ($r2.removedTables -join ', '))
}
