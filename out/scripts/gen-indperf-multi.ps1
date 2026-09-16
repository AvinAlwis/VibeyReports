<#
    REPORT 1 - Individual Performance Overview: MULTI-EMPLOYEE BULK GENERATION

        out/reports/PMSV10_IndPerfOverview.rpt      (output of gen-indperf-fixes.ps1)
          -> out/reports/PMSV10_IndPerfOverview.rpt      Administrator
          -> out/reports/PMSV10_IndPerfOverviewSS.rpt    Employee Self Service

    Run gen-indperf-fixes.ps1 FIRST - this script starts from its output, not from the
    copy in the PMS repo, so the layout fixes are carried forward.

    Needs VIBEY_DB_PASSWORD: addTable contacts the server to discover the new table's
    columns. Needs sp_perf_ind_perf_sheet to exist - deploy
    D:\PMS_Module\report-sql\Report-1-Multi-Employee.sql first.

    WHAT THIS DOES AND WHY IT IS A RESTRUCTURE RATHER THAN A TWEAK
    -------------------------------------------------------------
    The report prints one sheet per employee. Today the whole sheet lives in the
    Details section, which repeats once per pushed row, and the stage table is a
    SUB-REPORT.

    Under RN_SP = '1' a sub-report is evaluated ONCE for the whole run and renders
    identically in every repetition of the section holding it, so a per-employee
    sub-report can only ever be right for one employee. Linking it is not available:
    a Crystal link compiles into hidden "Pm-<field>" parameters that only Crystal's
    linkage pass fills, and that pass never runs under a push model (MEASURED on
    Report 2 - with links, export failed "Missing parameter values"; without, it
    succeeded).

    So the stage rows move into the MAIN dataset. sp_perf_ind_perf_sheet returns one
    row per employee PER STAGE, the report groups on emp_display_number, and:

        group header  <- the employee sheet (panels, tiles, stage-table headings)
        details       <- ONE stage row
        group footer  <- the final comments
        page break    <- before each group, so every employee starts a page

    WHY THE DATA OBJECTS ARE REBUILT RATHER THAN REPOINTED
    ------------------------------------------------------
    An .rpt carries a CACHED result-set schema and the RAS SDK cannot refresh one
    (sdk-notes.md, "The RAS SDK cannot re-verify a report's schema"). setTableLocation
    is the obvious tool and does not work: MEASURED again here, repointing
    PMSV10_GoalAlignCascade's table from sp_perf_goal_align_cascade to
    sp_perf_ind_perf_sheet returned ok:true and the reopened report still listed the
    ORIGINAL thirteen fields. On this report it does not even save, failing in SaveAs
    with "Failed to save database information ... the file is in use".

    addTable is the one operation that discovers columns from the server. It needs a
    name the report does not already carry, hence a new procedure rather than an
    ALTER - and it cannot reuse the old alias, so every object bound to the old table
    has to be deleted and re-created here against the new one. Their geometry and
    formatting are READ BACK OFF THE BASE REPORT rather than typed out, so nothing is
    lost in transcription and re-running after a further layout fix picks the fix up
    automatically.

    The objects that carry no data - headings, boxes, dividers - are rebuilt too, NOT
    moved, even though moveToSection exists and would preserve them perfectly. Moving
    leaves the Details section un-shrinkable: Crystal holds a section's minimum height
    at the deepest object ever placed in it and only removeObject lowers it. See the
    measurement beside Add-Static below.

    The three tiles that are text-objects-with-an-embedded-field (Text32/36/37) are
    bound to the old table, so they cannot survive either way; they are re-created as
    Field objects, and Text37's "/ {total_score}" splits into a literal and a field
    because no single operation builds a mixed run.

    FIVE APPLIES, NOT ONE
    ---------------------
    A plan is validated against the schema read before it runs. addTable, removeTable
    and addGroup each CHANGE that schema - new fields, deleted objects, two new
    sections - so anything referring to the result has to be planned against a fresh
    read. Hence four files. (Also: running a layout plan against an already-cascaded
    report killed the worker with STATUS_HEAP_CORRUPTION once before, which is its own
    reason to keep a cascade in an apply of its own.)
#>

$ErrorActionPreference = 'Stop'

$root  = 'D:/VibeyReports'
$work  = "$root/out/_build"
$base  = "$root/out/reports/PMSV10_IndPerfOverview.rpt"

$OLD_ALIAS = 'sp_perf_ind_perf_overview;1'
$NEW_TABLE = 'sp_perf_ind_perf_sheet;1'
$NEW_ALIAS = 'sheet'

# Text20 "FINAL COMMENTS" is the top of the block that becomes the group footer.
# Everything above it is the sheet; everything from it down is the comments.
$FOOTER_START = 8152

# The stage table. The sub-report it replaces occupied y 5497..8030 across the full
# 11050-twip width; the headings take the top of that band in the group header and
# the row itself becomes the Details section.
$HEAD_Y   = 5500
$RULE_Y   = 5800
$GH_H     = 5900          # Text19 ends at 5375, headings at 5760, rule at 5800
$GF_H     = 4750          # tallest shifted object (Box5) ends at 4673
$DET_H    = 340

# The section-title banners. Report 1's .rpt never had these - its headings were plain
# black text - but the approved prototype shows them, and Report 2 already draws exactly
# this: a filled bar with the caption reversed out of it.
#
# A BOX behind the text, not a background on the text, because Crystal XI has no
# per-object background: ISCRObjectFormat carries EnableCanGrow, EnableSuppress and
# alignment, and nothing else - BackgroundColor exists only on a SECTION format.
#
# The five titles are identified structurally (a full-width 360-twip Text at x=60)
# rather than by name, so a renamed or added section heading is picked up automatically.
$BANNER_FILL = '#2C3A47'
$BANNER_INK  = '#FFFFFF'
$BANNER_DX   = 150   # caption inset from the banner's left edge, as Report 2 uses
$BANNER_DY   = 60    # and down from its top, which centres a 240-twip caption in 360

# Column x/width, shared by the heading and the row so they cannot drift apart.
$COLS = @(
    @{ key='stage_name';          head='Stage';  x=120;  w=3800; align='Left'  },
    @{ key='stage_period';        head='Period'; x=4000; w=3000; align='Left'  },
    @{ key='stage_status';        head='Status'; x=7100; w=1800; align='Left'  },
    @{ key='stage_outcome_score'; head='Score';  x=9000; w=2000; align='Right' }
)

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

    # Start BOTH reads before blocking on either. Reading stdout to the end first and
    # stderr afterwards deadlocks: a COM failure dumps a stack trace larger than the
    # stderr pipe buffer, the worker blocks writing it, and the parent blocks reading
    # a stdout that will never be closed. Seen here as a worker sitting at 0.48
    # CPU-seconds indefinitely - no spin, no progress, no output.
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

if (-not (Test-Path $base)) { throw "Base report not found: $base. Run gen-indperf-fixes.ps1 first." }
New-Item -ItemType Directory -Force -Path $work | Out-Null

# ---------------------------------------------------------------------------------
# Capture the base BEFORE anything is destroyed. Every rebuilt object's geometry and
# formatting comes from here.
# ---------------------------------------------------------------------------------
$baseSchema = Read-Report $base

# This script REPLACES its own base, so running it twice without regenerating that
# base first would start from an already-restructured report. That fails anyway - the
# old table is gone by then - but it fails several operations in, so say so here
# instead.
if (-not ($baseSchema.availableFields | Where-Object { $_.tableAlias -eq $OLD_ALIAS })) {
    throw "$base does not carry the table `"$OLD_ALIAS`", so it has already been " +
          "restructured (or was never Report 1). Re-run gen-indperf-fixes.ps1 to " +
          "rebuild the base from the copy in the PMS repo, then run this again."
}

$details = $baseSchema.sections | Where-Object { $_.kind -eq 'Details' }

$subreports = @($details.objects | Where-Object { $_.kind -eq 'Subreport' })
$bound      = @($details.objects | Where-Object {
                    $_.kind -ne 'Subreport' -and
                    ($_.dataSource -or ($_.text -and $_.text -match [regex]::Escape($OLD_ALIAS))) })

# A FieldHeading is a text object Crystal keeps ATTACHED to the field it labels, and
# it is deleted along with that field - measured: removing the 13 field objects took
# all 8 headings ("Employee Name", "Designation", ...) with them, and the next plan
# rejected every moveToSection with "does not exist in the report". So they are
# rebuilt rather than moved. They hold literal text, so addText reproduces them
# exactly; the rebuilt object is a plain Text rather than a FieldHeading, which
# differs only in that Crystal no longer ties it to a field's lifetime.
$headings   = @($details.objects | Where-Object { $_.kind -eq 'FieldHeading' })

$static     = @($details.objects | Where-Object {
                    $_.kind -ne 'Subreport' -and
                    $_.kind -ne 'FieldHeading' -and
                    -not $_.dataSource -and
                    -not ($_.text -and $_.text -match [regex]::Escape($OLD_ALIAS)) })

# Objects in this report already reach past the printable area - reviewername2 runs
# to 11223 against a limit of 11186. Crystal tolerated it when the report was drawn;
# re-adding it does not, so widths are clipped to the margin. The largest clip is 37
# twips, well under a pixel on screen.
$printableWidth = $baseSchema.page.widthTwips - $baseSchema.page.marginLeftTwips - $baseSchema.page.marginRightTwips
$clips = New-Object System.Collections.ArrayList

# Returns ONLY the width. Anything this function writes to the output stream would be
# collected into its return value alongside the integer, and a widthTwips that arrives
# as a two-element array is rejected by the worker's deserialiser, not by the
# validator: "The JSON value could not be converted to System.Nullable`1[System.Int32]".
function Fit($o) {
    $max = $printableWidth - $o.leftTwips
    if ($o.widthTwips -gt $max) {
        [void]$clips.Add(("  clipped {0}: {1} -> {2} twips wide" -f $o.name, $o.widthTwips, $max))
        return $max
    }
    return $o.widthTwips
}

Write-Output ("base: {0} objects in Details - {1} data-bound, {2} headings, {3} static, {4} sub-report" -f `
    $details.objects.Count, $bound.Count, $headings.Count, $static.Count, $subreports.Count)

# ---------------------------------------------------------------------------------
# 1. Add the new table. Its columns are discovered from the server, which is the
#    whole reason the procedure had to be given a new name.
# ---------------------------------------------------------------------------------
$f1 = "$work/1-table.rpt"
Invoke-Plan 'add sp_perf_ind_perf_sheet' $base $f1 @(
    @{ action='addTable'; target=$OLD_ALIAS; tableName=$NEW_TABLE; newName=$NEW_ALIAS }
) | Out-Null

# ---------------------------------------------------------------------------------
# 2. Drop the old table, and everything bound to it first. removeTable does NOT
#    cascade - it refuses while any object still binds the table and names them - so
#    the removals are explicit and in the same plan.
#
#    The three tiles whose data is an EMBEDDED FIELD inside a text object
#    (Text32/36/37) are not among the ones removeTable counts: the validator sees a
#    Text object with no dataSource. They are removed here anyway. Leaving them would
#    keep a reference to a table the report no longer has, and they have to be rebuilt
#    against the new alias regardless.
#
#    A second main-report table cannot simply be left in place instead: under the push
#    model Report Navigator supplies data to one table only and the other fails with
#    "Database logon failed" (measured on Report 2's logo table).
# ---------------------------------------------------------------------------------
#    The removals and the drop are two applies, not one. In a single plan Crystal
#    rejects the removeTable with "The request could not be submitted for background
#    processing" - it still counts the objects deleted moments earlier as referring to
#    the table. Saving in between clears that; MEASURED both ways.
# ---------------------------------------------------------------------------------
$f2a = "$work/2a-unbound.rpt"
#    The static objects go too. They are not bound to anything and could in principle
#    stay, but leaving them behind makes the Details section un-shrinkable - see the
#    measurement beside Add-Static below. Everything in Details is removed here and
#    rebuilt in the group sections in step 4.
#
#    The 8 FieldHeadings are NOT listed: Crystal deletes each one along with the field
#    it labels, so by the time this plan finishes they are already gone, and naming
#    them here would fail with "does not exist in the report".
$dropOps = New-Object System.Collections.ArrayList
foreach ($o in $subreports) { [void]$dropOps.Add(@{ action='removeObject'; target=$o.name }) }
foreach ($o in $bound)      { [void]$dropOps.Add(@{ action='removeObject'; target=$o.name }) }
foreach ($o in $static)     { [void]$dropOps.Add(@{ action='removeObject'; target=$o.name }) }

$r2 = Invoke-Plan 'remove the bound objects' $f1 $f2a $dropOps.ToArray()
Write-Output ("  removed {0} objects: {1}" -f $r2.removedObjects.Count, ($r2.removedObjects -join ', '))

$f2 = "$work/2b-untabled.rpt"
Invoke-Plan 'drop the old table' $f2a $f2 @(
    @{ action='removeTable'; target=$OLD_ALIAS }
) | Out-Null

# ---------------------------------------------------------------------------------
# 3. Group on the employee. This creates the two sections everything below places
#    into, so it cannot share a plan with the placements.
# ---------------------------------------------------------------------------------
$f3 = "$work/3-grouped.rpt"
$r3 = Invoke-Plan 'group by employee' $f2 $f3 @(
    @{ action='addGroup'; fieldRef="{$NEW_ALIAS.emp_display_number}"; direction='ascending' }
)
$GH = $r3.schema.groups[0].headerSection
$GF = $r3.schema.groups[0].footerSection
Write-Output ("  group header = {0}, group footer = {1}" -f $GH, $GF)

# ---------------------------------------------------------------------------------
# 4. The restructure itself.
# ---------------------------------------------------------------------------------
$ops = New-Object System.Collections.ArrayList
function Op($h) { [void]$ops.Add($h) }

# Sections first: a placement is rejected if it would end past its section's height,
# and both new sections are 250 twips tall to begin with.
Op @{ action='resizeSection'; section=$GH; heightTwips=$GH_H }
Op @{ action='resizeSection'; section=$GF; heightTwips=$GF_H }

# --- the section-title banners ----------------------------------------------------
# Emitted BEFORE the loop below so every banner box precedes every caption in the
# section's object order. Crystal draws in that order, so the caption lands on top of
# its bar; reversed, the bar would paint over the caption.
$bannerNames = @{}
foreach ($o in $static) {
    if ($o.kind -eq 'Text' -and $o.leftTwips -eq 60 -and $o.heightTwips -eq 360) {
        $bannerNames[$o.name] = $true
        $isFooter = $o.topTwips -ge $FOOTER_START
        $sec = if ($isFooter) { $GF } else { $GH }
        $top = if ($isFooter) { $o.topTwips - $FOOTER_START } else { $o.topTwips }
        $bn  = 'Banner_' + $o.name
        Op @{ action='addBox'; section=$sec; newName=$bn
              leftTwips=$o.leftTwips; topTwips=$top; widthTwips=(Fit $o); heightTwips=$o.heightTwips }
        Op @{ action='setFillColor'; target=$bn; color=$BANNER_FILL }
        Op @{ action='setLineColor'; target=$bn; color=$BANNER_FILL }
        Op @{ action='setBorder'; target=$bn; left='none'; right='none'; top='none'; bottom='none'; color=$BANNER_FILL }
    }
}
Write-Output ("banners: {0} section titles reversed out of a {1} bar" -f $bannerNames.Count, $BANNER_FILL)

# --- the static objects are rebuilt, NOT moved -----------------------------------
# Every one of them is a plain Text, Box or Line, so addText/addBox/addLine reproduce
# them exactly. They are rebuilt rather than relocated because MOVING them leaves the
# Details section un-shrinkable.
#
# MEASURED. moveToSection is clone -> Remove from the old section -> Add to the new
# one, and the read-back confirms the object really does end up in the new section
# and really is gone from the old one. But Crystal keeps the SOURCE section's minimum
# height at the deepest object that was ever in it, and that survives save and
# reopen: after moving all 30 objects out, "resizeSection DetailSection1" was refused
# with COM 0x80042022 "The section height is not valid" for any height below 4870 -
# exactly Box18's bottom edge (3460 + 1410) in the layout before the move. Removing
# the same 30 objects with removeObject instead let Details shrink to 340 straight
# away. So: rebuild.
function Add-Static($o, $Section, $Top) {
    switch ($o.kind) {
        'Box' {
            Op @{ action='addBox'; section=$Section; newName=$o.name
                  leftTwips=$o.leftTwips; topTwips=$Top; widthTwips=(Fit $o); heightTwips=$o.heightTwips }
            # The tiles are filled (#E1E1E1 on the Overall Rating box); a rebuilt box starts
            # white, so omitting this quietly flattens the panel shading.
            if ($o.fillColorHex) { Op @{ action='setFillColor'; target=$o.name; color=$o.fillColorHex } }
            if ($o.lineColorHex) { Op @{ action='setLineColor'; target=$o.name; color=$o.lineColorHex } }
            if ($null -ne $o.lineThicknessTwips) { Op @{ action='setLineThickness'; target=$o.name; lineThicknessTwips=$o.lineThicknessTwips } }
            if ($o.border) {
                Op @{ action='setBorder'; target=$o.name; color=$o.border.colorHex
                      left=$o.border.left; right=$o.border.right
                      top=$o.border.top; bottom=$o.border.bottom }
            }
        }
        'Line' {
            Op @{ action='addLine'; section=$Section; newName=$o.name
                  leftTwips=$o.leftTwips; topTwips=$Top; widthTwips=$o.widthTwips; heightTwips=$o.heightTwips }
            if ($o.lineColorHex) { Op @{ action='setLineColor'; target=$o.name; color=$o.lineColorHex } }
            if ($null -ne $o.lineThicknessTwips) { Op @{ action='setLineThickness'; target=$o.name; lineThicknessTwips=$o.lineThicknessTwips } }
        }
        default {
            # A section title sits inset inside its banner and is reversed out of it;
            # everything else keeps the geometry and ink it was read with.
            $isBanner = $bannerNames.ContainsKey($o.name)
            $x   = if ($isBanner) { $o.leftTwips + $BANNER_DX } else { $o.leftTwips }
            $y   = if ($isBanner) { $Top + $BANNER_DY }         else { $Top }
            $w   = if ($isBanner) { (Fit $o) - $BANNER_DX }     else { (Fit $o) }
            $h   = if ($isBanner) { 240 }                       else { $o.heightTwips }
            $ink = if ($isBanner) { $BANNER_INK }               else { $o.textColorHex }

            Op @{ action='addText'; section=$Section; newName=$o.name; text=$o.text
                  leftTwips=$x; topTwips=$y; widthTwips=$w; heightTwips=$h }
            Op @{ action='setFont';      target=$o.name; fontName=$o.fontName }
            Op @{ action='setFontSize';  target=$o.name; fontSizePt=$o.fontSizePt }
            Op @{ action='setBold';      target=$o.name; bold=[bool]$o.bold }
            Op @{ action='setTextColor'; target=$o.name; color=$ink }
            if ($o.alignment -and $o.alignment -ne 'Default') {
                Op @{ action='setAlignment'; target=$o.name; alignment=$o.alignment }
            }
        }
    }
}

# Section coordinates are section-relative, so the footer block has to be rebased:
# Text20 sits at y=8152 in a 13104-tall Details and would be clipped by a 4750-tall
# group footer. Everything above FOOTER_START keeps its y exactly.
foreach ($o in $static) {
    if ($o.topTwips -ge $FOOTER_START) { Add-Static $o $GF ($o.topTwips - $FOOTER_START) }
    else                               { Add-Static $o $GH $o.topTwips }
}

# --- the field headings are re-created as plain text -----------------------------
foreach ($o in $headings) {
    $isFooter = $o.topTwips -ge $FOOTER_START
    $section  = if ($isFooter) { $GF } else { $GH }
    $top      = if ($isFooter) { $o.topTwips - $FOOTER_START } else { $o.topTwips }

    Op @{ action='addText'; section=$section; newName=$o.name; text=$o.text
          leftTwips=$o.leftTwips; topTwips=$top; widthTwips=(Fit $o); heightTwips=$o.heightTwips }
    Op @{ action='setFont';      target=$o.name; fontName=$o.fontName }
    Op @{ action='setFontSize';  target=$o.name; fontSizePt=$o.fontSizePt }
    Op @{ action='setBold';      target=$o.name; bold=[bool]$o.bold }
    Op @{ action='setTextColor'; target=$o.name; color=$o.textColorHex }
    if ($o.alignment -and $o.alignment -ne 'Default') {
        Op @{ action='setAlignment'; target=$o.name; alignment=$o.alignment }
    }
}

# --- the data objects are re-created against the new alias -----------------------
# Formatting is restored explicitly because a newly constructed RAS object inherits
# none of it: font, size, weight, colour and can-grow all start at defaults, and a
# Field with EnableSystemDefault left on is formatted from the machine's locale and
# discards whatever decimal places were asked for.
function Add-Rebuilt($o, $Section, $Top, $FieldRef) {
    Op @{ action='addField'; section=$Section; newName=$o.name; fieldRef=$FieldRef
          leftTwips=$o.leftTwips; topTwips=$Top; widthTwips=(Fit $o); heightTwips=$o.heightTwips }
    Op @{ action='setFont';     target=$o.name; fontName=$o.fontName }
    Op @{ action='setFontSize'; target=$o.name; fontSizePt=$o.fontSizePt }
    Op @{ action='setBold';     target=$o.name; bold=[bool]$o.bold }
    Op @{ action='setTextColor'; target=$o.name; color=$o.textColorHex }
    Op @{ action='setCanGrow';  target=$o.name; canGrow=[bool]$o.canGrow }
    # "Default" is Crystal's inherited alignment and is not a value setAlignment
    # accepts; leaving it alone reproduces it exactly.
    if ($o.alignment -and $o.alignment -ne 'Default') {
        Op @{ action='setAlignment'; target=$o.name; alignment=$o.alignment }
    }
}

foreach ($o in $bound) {
    $isFooter = $o.topTwips -ge $FOOTER_START
    $section  = if ($isFooter) { $GF } else { $GH }
    $top      = if ($isFooter) { $o.topTwips - $FOOTER_START } else { $o.topTwips }

    if ($o.dataSource) {
        Add-Rebuilt $o $section $top ($o.dataSource -replace [regex]::Escape($OLD_ALIAS), $NEW_ALIAS)
        continue
    }

    # A text object whose paragraph holds an embedded field. Two shapes occur:
    # the whole text IS the field, or a literal precedes it.
    $m = [regex]::Match($o.text, '^(?<lead>.*?)\{' + [regex]::Escape($OLD_ALIAS) + '\.(?<field>\w+)\}\s*$')
    if (-not $m.Success) { throw "Unrecognised embedded-field text on $($o.name): $($o.text)" }

    $lead = $m.Groups['lead'].Value.Trim()
    if ($lead -eq '') {
        Add-Rebuilt $o $section $top ("{$NEW_ALIAS." + $m.Groups['field'].Value + '}')
        continue
    }

    # Text37 is "/ {total_score}". No operation builds a mixed literal-and-field run,
    # so it becomes two objects that read as one: the literal right-aligned in the
    # left third of the original box, the value left-aligned in the rest. The pair
    # still straddles the same centre the tile above it is centred on.
    $slashW = [int]($o.widthTwips * 0.35)
    Op @{ action='addText'; section=$section; newName=($o.name + 'lead'); text=$lead
          leftTwips=$o.leftTwips; topTwips=$top; widthTwips=$slashW; heightTwips=$o.heightTwips }
    Op @{ action='setFont';      target=($o.name + 'lead'); fontName=$o.fontName }
    Op @{ action='setFontSize';  target=($o.name + 'lead'); fontSizePt=$o.fontSizePt }
    Op @{ action='setBold';      target=($o.name + 'lead'); bold=[bool]$o.bold }
    Op @{ action='setTextColor'; target=($o.name + 'lead'); color=$o.textColorHex }
    Op @{ action='setAlignment'; target=($o.name + 'lead'); alignment='Right' }

    $valueX = $o.leftTwips + $slashW + 60
    Op @{ action='addField'; section=$section; newName=$o.name
          fieldRef=("{$NEW_ALIAS." + $m.Groups['field'].Value + '}')
          leftTwips=$valueX; topTwips=$top; widthTwips=($o.widthTwips - $slashW - 60); heightTwips=$o.heightTwips }
    Op @{ action='setFont';      target=$o.name; fontName=$o.fontName }
    Op @{ action='setFontSize';  target=$o.name; fontSizePt=$o.fontSizePt }
    Op @{ action='setBold';      target=$o.name; bold=[bool]$o.bold }
    Op @{ action='setTextColor'; target=$o.name; color=$o.textColorHex }
    Op @{ action='setAlignment'; target=$o.name; alignment='Left' }
    Op @{ action='setNumberFormat'; target=$o.name; decimalPlaces=2; thousandsSeparator=$false }
}

# --- the stage table -------------------------------------------------------------
# Headings and rule in the group header, the row in Details. Both use $COLS so a
# column cannot be widened in one place and not the other.
#
# Deliberately NO per-row box or divider: a cycle has between 1 and 12 stages
# (measured), the Details section grows to fit whatever arrives, and ruling every
# row would need a repeating border this API does not draw. The single rule under
# the headings is what separates the table from the panel above it.
foreach ($c in $COLS) {
    $n = 'sth_' + $c.key
    Op @{ action='addText'; section=$GH; newName=$n; text=$c.head
          leftTwips=$c.x; topTwips=$HEAD_Y; widthTwips=$c.w; heightTwips=260 }
    Op @{ action='setFont';      target=$n; fontName='Segoe UI' }
    Op @{ action='setFontSize';  target=$n; fontSizePt=8 }
    Op @{ action='setBold';      target=$n; bold=$true }
    Op @{ action='setAlignment'; target=$n; alignment=$c.align }
}
Op @{ action='addLine'; section=$GH; newName='stageRule'
      leftTwips=120; topTwips=$RULE_Y; widthTwips=10880; heightTwips=0 }
Op @{ action='setLineColor'; target='stageRule'; color='#E1E1E1' }

foreach ($c in $COLS) {
    $n = 'str_' + $c.key
    Op @{ action='addField'; section='DetailSection1'; newName=$n; fieldRef=("{$NEW_ALIAS." + $c.key + '}')
          leftTwips=$c.x; topTwips=40; widthTwips=$c.w; heightTwips=220 }
    Op @{ action='setFont';      target=$n; fontName='Segoe UI' }
    Op @{ action='setFontSize';  target=$n; fontSizePt=8 }
    Op @{ action='setAlignment'; target=$n; alignment=$c.align }
    Op @{ action='setCanGrow';   target=$n; canGrow=$true }
}
# The only numeric column on the row. Without this Crystal formats it from the
# machine's locale defaults and the asked-for decimal places are discarded.
Op @{ action='setNumberFormat'; target='str_stage_outcome_score'; decimalPlaces=2; thousandsSeparator=$false }

# Details is 13104 tall and now holds one 260-twip row. Shrinking it LAST is what
# makes the sheet one page per employee instead of one page per stage; the validator
# checks nothing is left below the new height, which is why every move comes first.
Op @{ action='resizeSection'; section='DetailSection1'; heightTwips=$DET_H }

# The Report Header prints once, before the first group. It holds nothing and is only 10
# twips tall, but that is enough for Crystal to lay out a page for it -- and NewPageBefore
# on the group header then pushes the first employee onto page 2, so the report opens on a
# BLANK PAGE. Reported from Report Navigator and reproduced by pushing the real datasets in
# and exporting: 2 PDF pages for one employee, 1 after this suppression.
#
# Suppressing rather than resizing to 0: a zero-height section still lays out (Report 2's
# Report Header is 0 tall and correct only because nothing forces a break before it), and
# suppression is the state Crystal documents for this.
Op @{ action='setSuppress'; section='ReportHeaderSection1'; suppress=$true }

# Each employee starts a page. NewPageBefore on the group header, not NewPageAfter on
# the footer: "after" breaks following the LAST group too and prints a trailing blank
# page, and there is no conditional-suppression operation to guard it with.
Op @{ action='setSectionBreak'; section=$GH; newPageBefore=$true }

# The restructure plan is ~180 operations. Dumping it makes a COM rejection - which
# names an operation kind but not an index - bisectable without re-deriving it.
@{ planVersion=1; operations=$ops.ToArray() } | ConvertTo-Json -Depth 12 | Set-Content "$work/plan.json" -Encoding utf8
Write-Output ("plan: {0} operations -> {1}" -f $ops.Count, "$work/plan.json")

if ($clips.Count) {
    Write-Output ("printable area ends at {0} twips; {1} object(s) already ran past it and were clipped:" -f $printableWidth, $clips.Count)
    $clips | ForEach-Object { Write-Output $_ }
}

foreach ($v in @(
    @{ label='Administrator';         out="$root/out/reports/PMSV10_IndPerfOverview.rpt"   },
    @{ label='Employee Self Service'; out="$root/out/reports/PMSV10_IndPerfOverviewSS.rpt" })) {
    Invoke-Plan $v.label $f3 $v.out $ops.ToArray() | Out-Null
}

Write-Output ''
Write-Output 'Done. Both variants carry the same layout; only the Report Navigator'
Write-Output 'parameter form differs, and that lives in HS_HR_RN_PAR_DEF.'
