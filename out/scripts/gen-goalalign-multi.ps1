<#
    REPORT 2 - Goal Alignment & Cascading: MULTI-EMPLOYEE BULK GENERATION

        out/reports/PMSV10_GoalAlignCascade.rpt   (output of gen-goal-align-variants.ps1)
          -> out/reports/PMSV10_GoalAlignCascade.rpt      Administrator
          -> out/reports/PMSV10_GoalAlignCascadeSS.rpt    Employee Self Service

    Run gen-goal-align-variants.ps1 FIRST - this starts from its output, so the borders,
    tile centring and Goal ID column work are carried forward.

    Needs VIBEY_DB_PASSWORD: addTable contacts the server to discover the new table's
    columns. Needs sp_perf_goal_align_sheet to exist - deploy
    D:\PMS_Module\report-sql\Report-2-Multi-Employee.sql first.

    THE SAME RESTRUCTURE AS gen-indperf-multi.ps1, ON A DIFFERENT SHAPE
    ------------------------------------------------------------------
    Under RN_SP = '1' a sub-report is evaluated ONCE for the whole run and renders
    identically in every repetition of the section holding it, so a per-employee
    sub-report can only ever be right for one employee. Links cannot fix it under a push
    model - measured on THIS report: with links, export failed "Missing parameter
    values"; without, it succeeded.

    So the goal rows leave the `GoalDetail` sub-report and join the main dataset:
    sp_perf_goal_align_sheet returns one row per employee PER GOAL, and

        group header  <- the employee panel, the alignment tiles, the table headings
        details       <- ONE goal row
        group footer  <- the alignment legend
        page break    <- before each group, so every employee starts a page

    Report 1's sheet lived in the Details section. This one lives in the REPORT HEADER,
    which prints once for the whole report - that is exactly why it is single-employee
    today. Report Header and Report Footer both collapse to nothing here and their
    contents move into the group sections.

    `companylogo` STAYS a sub-report: it is the same image for every employee, so one
    push serves every repetition. It is dropped and re-imported from its own .rpt rather
    than relocated, because moveToSection leaves the source section un-shrinkable (see
    docs/sdk-notes.md) and the Report Header has to reach zero. Re-importing with the
    same newName keeps the name HS_HR_RN_SUBRPT_SP_MAP looks it up by.

    Everything else in the Report Header and Report Footer is a Text, Box, Line, bound
    Field or special field, all of which the layout API can reproduce exactly. The table
    headings and the goal row are rebuilt from the same column arithmetic the sub-report
    used, so the two cannot drift apart.
#>

$ErrorActionPreference = 'Stop'

$root  = 'D:/VibeyReports'
$work  = "$root/out/_build2"
$base  = "$root/out/reports/PMSV10_GoalAlignCascade.rpt"
$logo  = "$root/out/archive/reports/PMSV10_GoalAlignLogo.rpt"

$OLD_ALIAS = 'sp_perf_goal_align_cascade;1'
$NEW_TABLE = 'sp_perf_goal_align_sheet;1'
$NEW_ALIAS = 'sheet'

$RH = 'ReportHeaderSection1'
$RF = 'ReportFooterSection1'
$DT = 'DetailSection1'

# The goal table. Column widths are gen-detail-subreport.ps1's, unchanged, so the
# rebuilt table lines up exactly where the sub-report's did.
$colW  = @(1400, 2300, 1300, 4200, 1986)
$colX  = @(0); for ($i = 0; $i -lt 4; $i++) { $colX += ($colX[$i] + $colW[$i]) }
$hdrs  = @('Goal ID', 'Goal Name', 'Category', 'Cascading Objective Hierarchy', 'Alignment Status')
$keys  = @('goal_id', 'goal_name', 'category', 'cascading_objective_hierarchy', 'alignment_status')
$hdrH  = 480
$rowH  = 900

$INK = '#1F2937'; $BORDER = '#D5D9DE'; $THEAD = '#DCE3EA'

# B3Title ("GOAL ALIGNMENT DETAILS") ends at 7475 and the old Report Header was 7600,
# so the headings sit immediately below it and the group header grows to fit them.
$HEAD_Y = 7600
$GH_H   = 8100
$GF_H   = 1800   # the legend block, unchanged from the Report Footer it comes from

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
    $psi.FileName = "$root/dist/worker/VibeyReports.CrystalWorker.exe"
    $psi.UseShellExecute = $false
    $psi.RedirectStandardInput = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $utf8 = New-Object System.Text.UTF8Encoding $false
    $psi.StandardOutputEncoding = $utf8
    $psi.StandardErrorEncoding  = $utf8

    $p = [System.Diagnostics.Process]::Start($psi)

    # Start BOTH reads before blocking on either: a COM failure dumps a stack trace
    # larger than the stderr pipe buffer, and reading stdout to the end first deadlocks
    # against it. See the same note in gen-indperf-multi.ps1.
    $stdout = $p.StandardOutput.ReadToEndAsync()
    $stderr = $p.StandardError.ReadToEndAsync()

    $p.StandardInput.Write(($Request | ConvertTo-Json -Depth 12 -Compress))
    $p.StandardInput.Close()

    $raw = $stdout.GetAwaiter().GetResult()
    $err = $stderr.GetAwaiter().GetResult()
    $p.WaitForExit()

    $res = if ([string]::IsNullOrWhiteSpace($raw)) { $null } else { $raw | ConvertFrom-Json }
    if ($null -eq $res) {
        if ($p.ExitCode -eq $HEAP_CORRUPTION -and $Attempt -lt 4) {
            Write-Output ("  {0}: worker died with STATUS_HEAP_CORRUPTION, retry {1}/3" -f $Label, $Attempt)
            return Invoke-Worker $Label $Request ($Attempt + 1)
        }
        throw "$Label`: worker returned no response (exit $($p.ExitCode)).`n$err"
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

function Read-Report($Path) { (Invoke-Worker "read $([IO.Path]::GetFileName($Path))" @{ command='read'; reportPath=$Path }).schema }

function Invoke-Plan($Label, $In, $Out, $Ops) {
    $r = Invoke-Worker $Label @{
        command='apply'; reportPath=$In; outputPath=$Out; overwrite=$true
        plan=@{ planVersion=1; operations=$Ops }
    }
    Write-Output ("{0,-28} {1,3} operations -> {2}" -f $Label, $r.operationsApplied, [IO.Path]::GetFileName($Out))
    return $r
}

foreach ($f in @($base, $logo)) { if (-not (Test-Path $f)) { throw "Not found: $f" } }
New-Item -ItemType Directory -Force -Path $work | Out-Null

# ---------------------------------------------------------------------------------
# Capture the base BEFORE anything is destroyed.
# ---------------------------------------------------------------------------------
$baseSchema = Read-Report $base

if (-not ($baseSchema.availableFields | Where-Object { $_.tableAlias -eq $OLD_ALIAS })) {
    throw "$base does not carry the table `"$OLD_ALIAS`", so it has already been " +
          "restructured. Re-run gen-goal-align-variants.ps1 to rebuild the base, then run this again."
}

$sec = @{}
foreach ($s in $baseSchema.sections) { $sec[$s.name] = $s }

$printableWidth = $baseSchema.page.widthTwips - $baseSchema.page.marginLeftTwips - $baseSchema.page.marginRightTwips
$clips = New-Object System.Collections.ArrayList

# Returns ONLY the width - anything written to the output stream would be collected
# into the return value alongside the integer, and a widthTwips arriving as an array is
# rejected by the worker's deserialiser rather than by the validator.
function Fit($o) {
    $max = $printableWidth - $o.leftTwips
    if ($o.widthTwips -gt $max) {
        [void]$clips.Add(("  clipped {0}: {1} -> {2} twips wide" -f $o.name, $o.widthTwips, $max))
        return $max
    }
    return $o.widthTwips
}

$headerObjects = @($sec[$RH].objects)
$footerObjects = @($sec[$RF].objects)
$detailSub     = @($sec[$DT].objects | Where-Object { $_.kind -eq 'Subreport' })
$logoObject    = @($headerObjects | Where-Object { $_.kind -eq 'Subreport' })[0]

Write-Output ("base: report header {0} objects, details {1}, report footer {2}" -f `
    $headerObjects.Count, $sec[$DT].objects.Count, $footerObjects.Count)
Write-Output ("      logo sub-report = {0} ({1})" -f $logoObject.name, $logoObject.subreportName)

# ---------------------------------------------------------------------------------
# 1. Empty the Report Header, the Report Footer and the Details section.
#
#    Everything goes, including the objects that carry no data. Leaving one behind
#    would make its section un-shrinkable: Crystal holds a section's minimum height at
#    the deepest object ever placed in it, and only removeObject lowers it (measured -
#    see docs/sdk-notes.md). Both sections have to reach zero here.
#
#    THIS RUNS FIRST, BEFORE addTable, and the order is not cosmetic. Report 1's
#    equivalent added the table first and emptied afterwards; doing that here killed
#    the worker outright with STATUS_HEAP_CORRUPTION (exit 0xC0000374, no response at
#    all). The same 56 removals against the untouched base succeed. This base is
#    already post-removeTable - gen-goal-align-variants.ps1 cascades the logo table
#    away - and sdk-notes.md records that a layout plan run against an already-cascaded
#    report is what triggers this crash. Adding a table and then deleting 56 objects in
#    the next apply is evidently the same shape. Emptying first avoids it.
# ---------------------------------------------------------------------------------
$f1 = "$work/1-emptied.rpt"
$dropOps = New-Object System.Collections.ArrayList
foreach ($o in $headerObjects) { [void]$dropOps.Add(@{ action='removeObject'; target=$o.name }) }
foreach ($o in $footerObjects) { [void]$dropOps.Add(@{ action='removeObject'; target=$o.name }) }
foreach ($o in $detailSub)     { [void]$dropOps.Add(@{ action='removeObject'; target=$o.name }) }

$r1 = Invoke-Plan 'empty the fixed sections' $base $f1 $dropOps.ToArray()
Write-Output ("  removed {0} objects" -f $r1.removedObjects.Count)

# ---------------------------------------------------------------------------------
# 2. Add the new table, then drop the old one. Its columns are discovered from the
#    server, which is the whole reason the procedure had to be given a new name - the
#    RAS SDK cannot refresh an .rpt's cached result-set schema.
#
#    Two applies, because a report must keep at least one table: the new one has to be
#    in place and SAVED before the old one can go.
# ---------------------------------------------------------------------------------
$f2a = "$work/2a-table.rpt"
Invoke-Plan 'add sp_perf_goal_align_sheet' $f1 $f2a @(
    @{ action='addTable'; target=$OLD_ALIAS; tableName=$NEW_TABLE; newName=$NEW_ALIAS }
) | Out-Null

$f2 = "$work/2b-untabled.rpt"
Invoke-Plan 'drop the old table' $f2a $f2 @(
    @{ action='removeTable'; target=$OLD_ALIAS }
) | Out-Null

# ---------------------------------------------------------------------------------
# 3. Group on the employee. This creates the two sections everything below places into,
#    so it cannot share a plan with the placements.
# ---------------------------------------------------------------------------------
$f3 = "$work/3-grouped.rpt"
$r3 = Invoke-Plan 'group by employee' $f2 $f3 @(
    @{ action='addGroup'; fieldRef="{$NEW_ALIAS.emp_display_number}"; direction='ascending' }
)
$GH = $r3.schema.groups[0].headerSection
$GF = $r3.schema.groups[0].footerSection
Write-Output ("  group header = {0}, group footer = {1}" -f $GH, $GF)

# ---------------------------------------------------------------------------------
# 4. The restructure.
# ---------------------------------------------------------------------------------
$ops = New-Object System.Collections.ArrayList
function Op($h) { [void]$ops.Add($h) }

# Sections first: a placement is rejected if it would end past its section's height,
# and both new sections start 250 twips tall.
Op @{ action='resizeSection'; section=$GH; heightTwips=$GH_H }
Op @{ action='resizeSection'; section=$GF; heightTwips=$GF_H }

# Rebuild one object into a target section at a given y. Formatting is restored
# explicitly: a newly constructed RAS object inherits none of it, and a Field left with
# EnableSystemDefault on is formatted from the machine's locale, discarding whatever
# decimal places were asked for.
function Rebuild($o, $Section, $Top) {
    $n = $o.name
    switch ($o.kind) {
        'Box' {
            Op @{ action='addBox'; section=$Section; newName=$n
                  leftTwips=$o.leftTwips; topTwips=$Top; widthTwips=(Fit $o); heightTwips=$o.heightTwips }
            if ($o.fillColorHex) { Op @{ action='setFillColor'; target=$n; color=$o.fillColorHex } }
            if ($o.lineColorHex) { Op @{ action='setLineColor'; target=$n; color=$o.lineColorHex } }
            if ($null -ne $o.lineThicknessTwips) { Op @{ action='setLineThickness'; target=$n; lineThicknessTwips=$o.lineThicknessTwips } }
            if ($o.border) {
                Op @{ action='setBorder'; target=$n; color=$o.border.colorHex
                      left=$o.border.left; right=$o.border.right
                      top=$o.border.top; bottom=$o.border.bottom }
            }
            return
        }
        'Line' {
            Op @{ action='addLine'; section=$Section; newName=$n
                  leftTwips=$o.leftTwips; topTwips=$Top; widthTwips=$o.widthTwips; heightTwips=$o.heightTwips }
            if ($o.lineColorHex) { Op @{ action='setLineColor'; target=$n; color=$o.lineColorHex } }
            if ($null -ne $o.lineThicknessTwips) { Op @{ action='setLineThickness'; target=$n; lineThicknessTwips=$o.lineThicknessTwips } }
            return
        }
        'Field' {
            if ($o.dataSource -and $o.dataSource.StartsWith('{')) {
                Op @{ action='addField'; section=$Section; newName=$n
                      fieldRef=($o.dataSource -replace [regex]::Escape($OLD_ALIAS), $NEW_ALIAS)
                      leftTwips=$o.leftTwips; topTwips=$Top; widthTwips=(Fit $o); heightTwips=$o.heightTwips }
                if ($o.numberFormat) {
                    Op @{ action='setNumberFormat'; target=$n
                          decimalPlaces=$o.numberFormat.decimalPlaces
                          thousandsSeparator=[bool]$o.numberFormat.thousandsSeparator }
                }
            } else {
                # A special field - PrintDate, PrintTime, PageNofM. It belongs to no
                # table, so it survives none of the above and is re-placed by TYPE.
                # read_report names it in Crystal's PascalCase; addSpecialField's
                # vocabulary is the same word camel-cased.
                $t = $o.dataSource
                $type = ($t.Substring(0,1).ToLowerInvariant() + $t.Substring(1)) -replace '^pageNofM$', 'pageNOfM'
                Op @{ action='addSpecialField'; section=$Section; newName=$n; specialType=$type
                      leftTwips=$o.leftTwips; topTwips=$Top; widthTwips=(Fit $o); heightTwips=$o.heightTwips }
            }
        }
        default {
            Op @{ action='addText'; section=$Section; newName=$n; text=$o.text
                  leftTwips=$o.leftTwips; topTwips=$Top; widthTwips=(Fit $o); heightTwips=$o.heightTwips }
        }
    }

    # Text and Field share the font/colour tail.
    Op @{ action='setFont';      target=$n; fontName=$o.fontName }
    Op @{ action='setFontSize';  target=$n; fontSizePt=$o.fontSizePt }
    Op @{ action='setBold';      target=$n; bold=[bool]$o.bold }
    Op @{ action='setTextColor'; target=$n; color=$o.textColorHex }
    Op @{ action='setCanGrow';   target=$n; canGrow=[bool]$o.canGrow }
    # "Default" is Crystal's inherited alignment and is not a value setAlignment
    # accepts; leaving it alone reproduces it exactly.
    if ($o.alignment -and $o.alignment -ne 'Default') {
        Op @{ action='setAlignment'; target=$n; alignment=$o.alignment }
    }
    if ($o.border -and ($o.border.left -ne 'none' -or $o.border.right -ne 'none' -or
                        $o.border.top -ne 'none' -or $o.border.bottom -ne 'none')) {
        Op @{ action='setBorder'; target=$n; color=$o.border.colorHex
              left=$o.border.left; right=$o.border.right
              top=$o.border.top; bottom=$o.border.bottom }
    }
}

# --- the employee sheet moves into the group header, y unchanged -----------------
# The logo is re-imported rather than rebuilt: addSubreport takes a .rpt, and this is
# the same file gen-goal-align-variants.ps1 imported. newName is what ends up as the
# sub-report's own name, which is the key HS_HR_RN_SUBRPT_SP_MAP looks it up by, so it
# must stay 'companylogo'.
Op @{ action='addSubreport'; section=$GH; newName=$logoObject.subreportName
      reportPath=$logo
      leftTwips=$logoObject.leftTwips; topTwips=$logoObject.topTwips
      widthTwips=$logoObject.widthTwips; heightTwips=$logoObject.heightTwips }

foreach ($o in $headerObjects) {
    if ($o.kind -eq 'Subreport') { continue }
    Rebuild $o $GH $o.topTwips
}

# --- the legend moves into the group footer, y unchanged -------------------------
# It goes per employee rather than once at the end: every sheet is a standalone page
# and the glyphs it explains are on every one of them.
foreach ($o in $footerObjects) { Rebuild $o $GF $o.topTwips }

# --- the goal table ---------------------------------------------------------------
# Headings in the group header, one row in Details. Both are driven by $colX/$colW so a
# column cannot be widened in one place and not the other. The heading block is
# gen-detail-subreport.ps1's Page Header, shifted down to $HEAD_Y.
Op @{ action='addBox'; section=$GH; newName='GTHFill'
      leftTwips=0; topTwips=$HEAD_Y; widthTwips=11186; heightTwips=$hdrH }
Op @{ action='setFillColor'; target='GTHFill'; color=$THEAD }
Op @{ action='setLineColor'; target='GTHFill'; color=$BORDER }

for ($c = 0; $c -lt 5; $c++) {
    $n = "GTH$c"
    Op @{ action='addText'; section=$GH; newName=$n; text=$hdrs[$c]
          leftTwips=($colX[$c] + 70); topTwips=($HEAD_Y + 130)
          widthTwips=($colW[$c] - 140); heightTwips=260 }
    Op @{ action='setFont';      target=$n; fontName='Segoe UI' }
    Op @{ action='setFontSize';  target=$n; fontSizePt=8 }
    Op @{ action='setBold';      target=$n; bold=$true }
    Op @{ action='setTextColor'; target=$n; color=$INK }
    # Goal ID's heading follows its centred values, so the column reads as one thing.
    if ($c -eq 0) { Op @{ action='setAlignment'; target=$n; alignment='Centre' } }
    if ($c -gt 0) {
        Op @{ action='addLine'; section=$GH; newName="GTHV$c"
              leftTwips=$colX[$c]; topTwips=$HEAD_Y; widthTwips=0; heightTwips=$hdrH }
        Op @{ action='setLineColor'; target="GTHV$c"; color=$BORDER }
    }
}

for ($c = 0; $c -lt 5; $c++) {
    $n = "GTR$c"
    Op @{ action='addField'; section=$DT; newName=$n; fieldRef=("{$NEW_ALIAS." + $keys[$c] + '}')
          leftTwips=$colX[$c]; topTwips=0; widthTwips=$colW[$c]; heightTwips=$rowH }
    Op @{ action='setFont';      target=$n; fontName='Segoe UI' }
    Op @{ action='setFontSize';  target=$n; fontSizePt=8 }
    Op @{ action='setTextColor'; target=$n; color=$INK }
    if ($c -eq 0) {
        # goal_id is a BIGINT identifier, not a quantity: no decimals, no separators.
        # Centred, not right-aligned: a field spans its whole cell so the ruling comes
        # from the cell's own borders, which leaves a numeric field's default right
        # alignment sitting hard against the divider with the goal name flush on the
        # other side of it.
        Op @{ action='setNumberFormat'; target=$n; decimalPlaces=0; thousandsSeparator=$false }
        Op @{ action='setAlignment';    target=$n; alignment='Centre' }
    }
    # The hierarchy column carries a full parent chain and clips without can-grow.
    if ($c -eq 3) { Op @{ action='setCanGrow'; target=$n; canGrow=$true } }
    # Ruled by the cells' own borders, not by drawn lines: a Line has no can-grow, so
    # the hierarchy column growing would leave drawn dividers behind at the original
    # row height with the text running past them.
    Op @{ action='setBorder'; target=$n; left='single'; right='single'; bottom='single'; color=$BORDER }
}

# --- collapse the sections that printed once --------------------------------------
# Both are empty now. They shrink to nothing because everything left them via
# removeObject; had anything been moved instead, Crystal would hold the section at the
# deepest object it ever contained.
Op @{ action='resizeSection'; section=$RH; heightTwips=0 }
Op @{ action='resizeSection'; section=$RF; heightTwips=0 }

# The Report Header is already empty and 0 twips tall here, and this report does NOT show
# the blank first page Report 1 did. Suppressed anyway: a zero-height section still lays
# out, so the moment anything gives this one height again the blank page comes back.
Op @{ action='setSuppress'; section=$RH; suppress=$true }

# Each employee starts a page. NewPageBefore on the group header, not NewPageAfter on
# the footer: "after" breaks following the LAST group too and prints a trailing blank
# page, and there is no conditional-suppression operation to guard it with.
Op @{ action='setSectionBreak'; section=$GH; newPageBefore=$true }

@{ planVersion=1; operations=$ops.ToArray() } | ConvertTo-Json -Depth 12 | Set-Content "$work/plan.json" -Encoding utf8
Write-Output ("plan: {0} operations -> {1}" -f $ops.Count, "$work/plan.json")

if ($clips.Count) {
    Write-Output ("printable area ends at {0} twips; {1} object(s) ran past it and were clipped:" -f $printableWidth, $clips.Count)
    $clips | ForEach-Object { Write-Output $_ }
}

foreach ($v in @(
    @{ label='Administrator';         out="$root/out/reports/PMSV10_GoalAlignCascade.rpt"   },
    @{ label='Employee Self Service'; out="$root/out/reports/PMSV10_GoalAlignCascadeSS.rpt" })) {
    Invoke-Plan $v.label $f3 $v.out $ops.ToArray() | Out-Null
}

# The goal row's fields span their whole cell, because the table's ruling comes from each
# cell's own border rather than from drawn lines. A full-width field has no margin, so its
# text sits hard against the divider - reported as text running through the table lines.
# The fix is paragraph indentation, which moves the TEXT inside the field without moving
# the field, and it exists on StringFormat only; the numeric Goal ID column is centred
# instead, which is the only padding a numeric field can have. No layout operation exposes
# it, so this runs as a pass over each saved variant.
foreach ($v in @("$root/out/reports/PMSV10_GoalAlignCascade.rpt",
                 "$root/out/reports/PMSV10_GoalAlignCascadeSS.rpt")) {
    & "$root/out/scripts/set-detail-cell-padding.ps1" -ReportPath $v
}

Write-Output ''
Write-Output 'Done. Both variants carry the same layout; only the Report Navigator'
Write-Output 'parameter form differs, and that lives in HS_HR_RN_PAR_DEF.'
