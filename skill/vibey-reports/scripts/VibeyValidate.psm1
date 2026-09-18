<#
    The safety gate. Pure: no SDK, no file I/O, no environment access. It simulates the plan
    cumulatively - grow a section then place a taller object in it and both pass, because by
    that point the section IS taller.

    A plan that fails here is applied NOT AT ALL. That all-or-nothing property is the whole
    point; do not add a "skip the bad ones and carry on" mode.

    Dispatch note: the brief's original draft used a PowerShell `switch` with a pattern block
    (`{ $script:AddActions -contains $_ }`) mixed in among literal-value cases. PowerShell's
    `switch` evaluates EVERY block whose condition matches, not just the first, unless each
    block ends with `break` - and a scriptblock condition is evaluated for every case with $_
    bound to the current action, so a literal case like 'move' would also be tested against
    the pattern block. Restructured below as an if/elseif chain instead, which runs exactly
    one branch per operation and needs no `break` bookkeeping to avoid duplicate errors.

    PARITY. This is a port of src\VibeyReports.Contracts\LayoutPlanValidator.cs, which is the
    reference: the same actions, the same rules and, where a rule was measured against Crystal,
    the same reasoning. Where the two ever disagree, the C# one has been exercised against more
    reports - bring this back into line rather than the other way round.

    FOUR NAME-SPACES are simulated side by side, because Crystal keeps them separate and mixing
    them up is the most common way a plan that looks right fails mid-apply:
      * report objects    - by object name ("Text3", "Subreport1")
      * sub-reports       - by SubreportName, which is NOT the placed object's name
      * data-source tables - by alias ("sp_x;1")
      * groups and sorts  - by the FIELD they act on, in formula form ("{sp_x;1.col}")
    PowerShell hashtables compare keys case-insensitively, which matches the C# validator's
    OrdinalIgnoreCase everywhere.
#>

$script:VibeyActions = @(
    'move','resize','resizeSection','setAlignment',
    'addText','addLine','addBox','addField','addSpecialField','removeObject',
    'setFont','setFontSize','setBold',
    'setTextColor','setFillColor','setLineColor','setSectionBackground',
    'setSectionBreak','setNumberFormat','setCanGrow','setSuppress',
    'setBorder','setLineThickness','moveToSection',
    'addSubreport','setSubreportLink',
    'addTable','removeTable','setTableLocation',
    'addGroup','addSort')

$script:FontableKinds = @('Text','Field','FieldHeading')
# Subreport is here deliberately: a sub-report clips its own contents at the container's height
# unless the CONTAINER can grow too. The C# validator shipped without it once and that caused a
# real defect.
$script:CanGrowKinds  = @('Text','Field','FieldHeading','Subreport')
$script:Alignments    = @('Left','Right','Centre','Center','Justified')
$script:SpecialTypes  = @('pageNumber','pageNOfM','totalPageCount','printDate','printTime','reportTitle','recordNumber')
$script:AddActions    = @('addText','addLine','addBox','addField','addSpecialField','addSubreport')
$script:BorderStyles  = @('none','single','double','dashed','dotted')
# Only these two. Crystal also has topN/bottomN orders, but each needs an N that no operation has
# a field to express, so they are rejected by name rather than passed through.
$script:Directions    = @('ascending','descending')

# Measured: the Group Header / Group Footer sections Crystal creates for a new group start at 250
# twips on every fixture tried. The simulation needs SOME height for them so a placement into one
# in the same plan can be bounds-checked. Pair addGroup with resizeSection for taller content.
$script:NewGroupSectionHeight = 250

# Coerces a plan-supplied value (leftTwips, heightTwips, fontSizePt, ...) to a number without
# ever throwing. A model can plausibly emit a string where a number belongs (e.g. "tall"), and
# the validator's contract is to report that as IsValid = $false, not to crash on a cast.
# Returns @{ Ok = <bool>; Value = <double> } - Value is meaningless when Ok is $false.
function Get-VibeyNumber($Value) {
    if ($Value -is [int] -or $Value -is [long] -or $Value -is [double] -or $Value -is [decimal]) {
        return @{ Ok = $true; Value = [double]$Value }
    }
    if ($null -ne $Value) {
        $parsed = 0.0
        if ([double]::TryParse([string]$Value, [ref]$parsed)) { return @{ Ok = $true; Value = $parsed } }
    }
    return @{ Ok = $false; Value = 0 }
}

function Get-VibeyGroupSectionName {
    <#
        The section names Crystal gives a new group, predicted rather than invented. The C#
        reference (GroupSectionNaming) is pinned by a test that adds a group and reads the names
        back, so this is the measured rule, not a guess:

            "{sp_perf_ind_perf_sheet;1.emp_display_number}"
              -> "empdisplaynumberHeaderSection1" / "empdisplaynumberFooterSection1"

        Braces stripped, everything up to the LAST dot dropped (the table part can itself contain
        dots), then every character that is not a letter or digit removed.

        Predicting them is what lets [addGroup, addText into the new header] validate as ONE plan.
    #>
    param([string]$FieldRef, [ValidateSet('Header','Footer')][string]$Band)
    $raw = ([string]$FieldRef).Trim()
    if ($raw.StartsWith('{')) { $raw = $raw.Substring(1) }
    if ($raw.EndsWith('}'))   { $raw = $raw.Substring(0, $raw.Length - 1) }
    $dot = $raw.LastIndexOf('.')
    if ($dot -ge 0 -and $dot -lt $raw.Length - 1) { $raw = $raw.Substring($dot + 1) }
    $name = New-Object System.Text.StringBuilder
    foreach ($c in $raw.ToCharArray()) { if ([char]::IsLetterOrDigit($c)) { [void]$name.Append($c) } }
    return $name.ToString() + $Band + 'Section1'
}

function Test-VibeyPlan {
    [CmdletBinding()]
    param([Parameter(Mandatory)]$Plan, [Parameter(Mandatory)]$Schema)

    $errors = New-Object System.Collections.ArrayList
    function Add-Err([int]$Index, [string]$Message) { [void]$errors.Add(@{ OperationIndex = $Index; Message = $Message }) }

    if ($null -eq $Plan.operations) {
        Add-Err -1 'Plan "operations" is null.'
        return @{ IsValid = $false; Errors = @($errors) }
    }
    if ($null -eq $Schema.page) {
        Add-Err -1 'Schema has no page information.'
        return @{ IsValid = $false; Errors = @($errors) }
    }

    $printableWidth  = [int]$Schema.page.widthTwips  - [int]$Schema.page.marginLeftTwips - [int]$Schema.page.marginRightTwips
    $printableHeight = [int]$Schema.page.heightTwips - [int]$Schema.page.marginTopTwips  - [int]$Schema.page.marginBottomTwips

    # --- simulation state, mutated as the plan is walked ---------------------------------
    $sectionHeights   = @{}
    $objects          = @{}
    $removed          = @{}
    $subreportsByName = @{}
    foreach ($s in $Schema.sections) {
        $sectionHeights[$s.name] = [int]$s.heightTwips
        foreach ($o in $s.objects) {
            $sim = @{ Section = $s.name; Left = [int]$o.leftTwips; Top = [int]$o.topTwips
                      Width = [int]$o.widthTwips; Height = [int]$o.heightTwips; Kind = $o.kind
                      SubreportName = $o.subreportName
                      # removeTable's bound-object check reads this. A Subreport carries its OWN data
                      # source and is never bound to a main-report table.
                      DataSource = $o.dataSource
                      AddedInPlan = $false }
            $objects[$o.name] = $sim
            if ($o.kind -eq 'Subreport' -and $o.subreportName) { $subreportsByName[$o.subreportName] = $sim }
        }
    }

    # addField's security boundary: fieldRef must match a formulaForm the report already exposes.
    # The same list seeds the table name-space, which addTable/removeTable then change.
    $availableFieldRefs = @{}
    $tables             = @{}
    $fieldRefsByTable   = @{}
    foreach ($f in @($Schema.availableFields)) {
        if ($null -eq $f -or -not $f.formulaForm) { continue }
        $availableFieldRefs[$f.formulaForm] = $true
        if (-not $f.tableAlias) { continue }
        $tables[$f.tableAlias] = $true
        if (-not $fieldRefsByTable.ContainsKey($f.tableAlias)) { $fieldRefsByTable[$f.tableAlias] = New-Object System.Collections.ArrayList }
        [void]$fieldRefsByTable[$f.tableAlias].Add($f.formulaForm)
    }
    # The reader CLEARS availableFields when the data source cannot be enumerated - a normal state
    # with no database connection - so an empty list means "unknown", not "no tables". Absence
    # cannot be proven from it, so the table existence checks are skipped rather than rejecting
    # every table operation. Captured BEFORE the loop, so a removeTable that empties the set part
    # way through does not silently switch addGroup/addSort's field check off.
    $tablesKnown    = $tables.Count -gt 0
    $fieldRefsKnown = $availableFieldRefs.Count -gt 0
    $removedTables  = @{}

    $groupList = New-Object System.Collections.ArrayList
    $sortList  = New-Object System.Collections.ArrayList
    $groupFields = @{}
    $sortFields  = @{}
    $ambiguousSections = @{}
    foreach ($g in @($Schema.groups)) { if ($g -and $g.fieldRef) { [void]$groupList.Add($g.fieldRef); $groupFields[$g.fieldRef] = $true } }
    foreach ($s in @($Schema.sorts))  { if ($s -and $s.fieldRef) { [void]$sortList.Add($s.fieldRef);  $sortFields[$s.fieldRef]  = $true } }

    function Test-TableExists([int]$Index, [string]$Alias, [string]$Action) {
        if ($tables.ContainsKey($Alias)) { return $true }
        if ($removedTables.ContainsKey($Alias)) { Add-Err $Index "Table `"$Alias`" was removed earlier in this plan."; return $false }
        # Not known to be absent, only not listed - and the list may simply be unavailable.
        if (-not $tablesKnown) { return $true }
        $known = ($tables.Keys | Sort-Object) -join ', '
        Add-Err $Index "`"$Action`": no table with the alias `"$Alias`" is in this report's data source. Known aliases: $known."
        return $false
    }

    # 0-based; equal to the current count means append, so the count itself is legal.
    function Test-Index([int]$OpIndex, $Value, [int]$Count, [string]$Field, [string]$What) {
        if ($null -eq $Value) { return $true }
        $n = Get-VibeyNumber $Value
        if (-not $n.Ok) { Add-Err $OpIndex "`"$Field`" must be a number, got `"$Value`"."; return $false }
        if ($n.Value -lt 0) { Add-Err $OpIndex "`"$Field`" must not be negative; got $([int]$n.Value)."; return $false }
        if ($n.Value -gt $Count) {
            Add-Err $OpIndex "`"$Field`" $([int]$n.Value) is past the end: this report has $Count $What, so the largest accepted value is $Count (which appends)."
            return $false
        }
        return $true
    }

    function Add-Predicted([ref]$ListRef, $Value, [string]$Item) {
        $n = Get-VibeyNumber $Value
        if ($null -ne $Value -and $n.Ok -and $n.Value -ge 0 -and $n.Value -le $ListRef.Value.Count) {
            $ListRef.Value.Insert([int]$n.Value, $Item)
        } else { [void]$ListRef.Value.Add($Item) }
    }

    $i = -1
    foreach ($op in $Plan.operations) {
        $i++

        if ($null -eq $op) { Add-Err $i 'Operation is null.'; continue }

        $action = [string]$op.action

        if ($script:VibeyActions -notcontains $action) {
            Add-Err $i "`"$action`" is not a supported action. Supported: $($script:VibeyActions -join ', ')."
            continue
        }

        # --- resolve the target, for the operations that take a REPORT OBJECT ----------------
        # setSubreportLink and the three table operations also take a "target", but in other
        # name-spaces, so they resolve it themselves further down.
        $needsTarget = (@('move','resize','setAlignment','removeObject','setFont','setFontSize',
                         'setBold','setTextColor','setFillColor','setLineColor','setNumberFormat',
                         'setCanGrow','setBorder','setLineThickness','moveToSection') -contains $action) -or
                       ($action -eq 'setSuppress' -and $op.target)
        $target = $null
        if ($needsTarget) {
            if (-not $op.target) { Add-Err $i "`"$action`" requires `"target`"."; continue }
            if ($objects.ContainsKey($op.target)) { $target = $objects[$op.target] }
            elseif ($removed.ContainsKey($op.target)) {
                Add-Err $i "Object `"$($op.target)`" was removed earlier in this plan."; continue
            } else {
                Add-Err $i "Object `"$($op.target)`" does not exist in the report."; continue
            }

            # Crystal never gives a placed sub-report the name addSubreport asked for - it numbers
            # the container itself ("Subreport1") and the requested name becomes its SubreportName.
            # So a sub-report added earlier in THIS plan has no object that answers to that name,
            # and every object operation on it would fail mid-apply. Reject while nothing is written.
            if ($target.AddedInPlan -and $target.Kind -eq 'Subreport') {
                Add-Err $i ("`"$action`" cannot target `"$($op.target)`": that is the newName of a sub-report added " +
                            "earlier in this same plan, and Crystal gives the placed sub-report object its own " +
                            "auto-numbered name (e.g. `"Subreport1`") at import time. addSubreport already places it " +
                            "at the geometry you gave; to change it afterwards, run a second plan against the saved " +
                            "report using the object name read returns for it. Only setSubreportLink can address a " +
                            "sub-report by this name.")
                continue
            }
        }

        # --- resolve the section, for the operations that take one ------------------------
        $needsSection = ($script:AddActions -contains $action) -or
                        ($action -in @('resizeSection','setSectionBackground','setSectionBreak','moveToSection')) -or
                        ($action -eq 'setSuppress' -and -not $op.target)
        if ($needsSection) {
            if (-not $op.section) { Add-Err $i "`"$action`" requires `"section`"."; continue }
            # An addGroup earlier in this plan whose predicted section name collided with a section
            # already in the report: the name now means two sections, so refuse to guess which.
            if ($ambiguousSections.ContainsKey($op.section)) {
                Add-Err $i ("Section `"$($op.section)`" is ambiguous: an addGroup earlier in this plan creates a " +
                            "section whose name collides with one already in the report. Run this plan without " +
                            "the placement, then read the report and place into the real section name.")
                continue
            }
            if (-not $sectionHeights.ContainsKey($op.section)) {
                Add-Err $i "Section `"$($op.section)`" does not exist in the report."; continue
            }
        }

        if ($action -eq 'resizeSection') {
            $hn = Get-VibeyNumber $op.heightTwips
            if (-not $hn.Ok) { Add-Err $i "`"heightTwips`" must be a number, got `"$($op.heightTwips)`"."; continue }
            $h = [int]$hn.Value
            if ($h -lt 0) { Add-Err $i 'Section height cannot be negative.' }
            elseif ($h -gt $printableHeight) {
                Add-Err $i "Section height $h exceeds the printable height of $printableHeight."
            } else {
                # Shrinking must not orphan an object already placed lower down - including one
                # added or moved in earlier in this same plan.
                $clipped = $false
                foreach ($k in @($objects.Keys)) {
                    $ob = $objects[$k]
                    if ($ob.Section -ne $op.section) { continue }
                    $bottom = [long]$ob.Top + $ob.Height
                    if ($bottom -gt $h) {
                        Add-Err $i "Shrinking section `"$($op.section)`" to $h would clip `"$k`", which ends at $bottom."
                        $clipped = $true
                    }
                }
                if (-not $clipped) { $sectionHeights[$op.section] = $h }
            }
        }
        elseif ($action -eq 'move') {
            $ln = Get-VibeyNumber $op.leftTwips
            if (-not $ln.Ok) { Add-Err $i "`"leftTwips`" must be a number, got `"$($op.leftTwips)`"."; continue }
            $tn = Get-VibeyNumber $op.topTwips
            if (-not $tn.Ok) { Add-Err $i "`"topTwips`" must be a number, got `"$($op.topTwips)`"."; continue }
            $l = [int]$ln.Value; $t = [int]$tn.Value
            $secH = $sectionHeights[$target.Section]

            # "Do not make it worse": an object already overflowing may be moved, as long as
            # the move does not increase the overflow. Otherwise a report that arrives broken
            # can never be repaired. Each axis is checked INDEPENDENTLY - coupling them into a
            # single boolean (as an earlier version of this file did) grants amnesty on a fine
            # axis just because the other axis was already broken: an object too tall for its
            # section could then be moved to also blow past the printable width and pass,
            # solely because the height problem pre-existed.
            $oldRight  = $target.Left + $target.Width
            $oldBottom = $target.Top + $target.Height
            $newRight  = $l + $target.Width
            $newBottom = $t + $target.Height

            $moveOk = $true
            if ($newRight -gt $printableWidth -and $newRight -gt $oldRight) {
                Add-Err $i "Moving `"$($op.target)`" to leftTwips $l puts its right edge at $newRight, past the printable width of $printableWidth."
                $moveOk = $false
            }
            if ($newBottom -gt $secH -and $newBottom -gt $oldBottom) {
                Add-Err $i "Moving `"$($op.target)`" to topTwips $t puts its bottom edge at $newBottom, past the height of section `"$($target.Section)`" ($secH)."
                $moveOk = $false
            }
            if ($moveOk) { $target.Left = $l; $target.Top = $t }
        }
        elseif ($action -eq 'moveToSection') {
            # Changes an object's OWNER. left/top are optional and default to its current
            # position - but section coordinates are section-relative, so an object from deep in a
            # tall section lands clipped in a short one unless a new topTwips is given.
            if ($target.Section -eq $op.section) {
                Add-Err $i "`"$($op.target)`" is already in section `"$($op.section)`"; use `"move`" to reposition it."
                continue
            }
            $l = $target.Left; $t = $target.Top
            if ($null -ne $op.leftTwips) {
                $ln = Get-VibeyNumber $op.leftTwips
                if (-not $ln.Ok) { Add-Err $i "`"leftTwips`" must be a number, got `"$($op.leftTwips)`"."; continue }
                $l = [int]$ln.Value
            }
            if ($null -ne $op.topTwips) {
                $tn = Get-VibeyNumber $op.topTwips
                if (-not $tn.Ok) { Add-Err $i "`"topTwips`" must be a number, got `"$($op.topTwips)`"."; continue }
                $t = [int]$tn.Value
            }
            if ($l -lt 0 -or $t -lt 0) { Add-Err $i 'Coordinates must not be negative.'; continue }
            $right  = [long]$l + $target.Width
            $bottom = [long]$t + $target.Height
            $secH = $sectionHeights[$op.section]
            $ok = $true
            if ($right -gt $printableWidth) {
                Add-Err $i "`"$($op.target)`" would end at $right, past the printable width of $printableWidth."
                $ok = $false
            }
            if ($bottom -gt $secH) {
                Add-Err $i ("`"$($op.target)`" would end at $bottom, past the height of section `"$($op.section)`" " +
                            "($secH). Resize the section first, or give a smaller topTwips.")
                $ok = $false
            }
            if ($ok) { $target.Section = $op.section; $target.Left = $l; $target.Top = $t }
        }
        elseif ($action -eq 'resize') {
            $wn = Get-VibeyNumber $op.widthTwips
            if (-not $wn.Ok) { Add-Err $i "`"widthTwips`" must be a number, got `"$($op.widthTwips)`"."; continue }
            $hn = Get-VibeyNumber $op.heightTwips
            if (-not $hn.Ok) { Add-Err $i "`"heightTwips`" must be a number, got `"$($op.heightTwips)`"."; continue }
            $w = [int]$wn.Value; $h = [int]$hn.Value
            if ($w -lt 0 -or $h -lt 0) { Add-Err $i 'Width and height cannot be negative.' }
            elseif ($target.Kind -eq 'Line' -and $w -ne 0 -and $h -ne 0) {
                Add-Err $i "A line must be horizontal or vertical: set widthTwips or heightTwips to 0 (got $w x $h)."
            } else {
                $secH = $sectionHeights[$target.Section]
                if ($target.Left + $w -gt $printableWidth) {
                    Add-Err $i "`"$($op.target)`" would end at $($target.Left + $w), past the printable width of $printableWidth."
                }
                elseif ($target.Top + $h -gt $secH) {
                    Add-Err $i "`"$($op.target)`" would end at $($target.Top + $h), past the height of section `"$($target.Section)`" ($secH)."
                } else {
                    $target.Width = $w; $target.Height = $h
                }
            }
        }
        elseif ($action -eq 'setAlignment') {
            if ($script:Alignments -notcontains [string]$op.alignment) {
                Add-Err $i "`"$($op.alignment)`" is not a valid alignment. Use: $($script:Alignments -join ', ')."
            }
        }
        elseif ($action -eq 'removeObject') {
            # Removing a Subreport's container takes the embedded sub-report with it, so drop it
            # from that name-space too, or a later setSubreportLink would resolve against it.
            if ($target.SubreportName) { [void]$subreportsByName.Remove($target.SubreportName) }
            $removed[$op.target] = $true
            [void]$objects.Remove($op.target)
        }
        elseif ($action -eq 'setFont') {
            if (-not $op.fontName) { Add-Err $i '"setFont" requires "fontName".' }
            elseif ($script:FontableKinds -notcontains $target.Kind) {
                Add-Err $i "`"$($op.target)`" is a $($target.Kind) and has no font."
            }
        }
        elseif ($action -eq 'setFontSize') {
            if ($script:FontableKinds -notcontains $target.Kind) {
                Add-Err $i "`"$($op.target)`" is a $($target.Kind) and has no font."
            } else {
                $ptn = Get-VibeyNumber $op.fontSizePt
                if (-not $ptn.Ok) { Add-Err $i "`"fontSizePt`" must be a number, got `"$($op.fontSizePt)`"." }
                else {
                    $pt = $ptn.Value
                    if ($pt -lt 4 -or $pt -gt 72) { Add-Err $i "Font size $pt is outside 4-72 points." }
                }
            }
        }
        elseif ($action -eq 'setBold') {
            if ($null -eq $op.bold) { Add-Err $i '"setBold" requires "bold".' }
            elseif ($script:FontableKinds -notcontains $target.Kind) {
                Add-Err $i "`"$($op.target)`" is a $($target.Kind) and has no font."
            }
        }
        elseif ($action -eq 'setTextColor') {
            if ($script:FontableKinds -notcontains $target.Kind) {
                Add-Err $i "`"$($op.target)`" is a $($target.Kind) and has no text colour to change."
            } else {
                if (-not (Test-VibeyColor $op.color)) {
                    Add-Err $i "`"$($op.color)`" is not a valid colour; expected the form `"#RRGGBB`" (e.g. `"#1F2A37`")."
                }
            }
        }
        elseif ($action -eq 'setFillColor') {
            if ($target.Kind -ne 'Box') {
                Add-Err $i "`"$($op.target)`" is a $($target.Kind), not a Box, and has no fill colour to change."
            } else {
                if (-not (Test-VibeyColor $op.color)) {
                    Add-Err $i "`"$($op.color)`" is not a valid colour; expected the form `"#RRGGBB`" (e.g. `"#1F2A37`")."
                }
            }
        }
        elseif ($action -eq 'setLineColor') {
            if ($target.Kind -ne 'Line' -and $target.Kind -ne 'Box') {
                Add-Err $i "`"$($op.target)`" is a $($target.Kind), not a Line or Box, and has no line colour to change."
            } else {
                if (-not (Test-VibeyColor $op.color)) {
                    Add-Err $i "`"$($op.color)`" is not a valid colour; expected the form `"#RRGGBB`" (e.g. `"#1F2A37`")."
                }
            }
        }
        elseif ($action -eq 'setLineThickness') {
            if ($target.Kind -ne 'Line' -and $target.Kind -ne 'Box') {
                Add-Err $i "`"$($op.target)`" is a $($target.Kind); only a Box or a Line has a line thickness."
            } else {
                $n = Get-VibeyNumber $op.lineThicknessTwips
                if ($null -eq $op.lineThicknessTwips) { Add-Err $i '"setLineThickness" requires "lineThicknessTwips".' }
                elseif (-not $n.Ok) { Add-Err $i "`"lineThicknessTwips`" must be a number, got `"$($op.lineThicknessTwips)`"." }
                # Twips. Negative is meaningless, and a very large value draws a filled block rather
                # than a rule, so the range is bounded here rather than at the COM boundary.
                elseif ($n.Value -lt 0 -or $n.Value -gt 100) {
                    Add-Err $i "`"lineThicknessTwips`" must be between 0 and 100 twips; got $([int]$n.Value)."
                }
            }
        }
        elseif ($action -eq 'setBorder') {
            # DELIBERATELY NO KIND CHECK: the border hangs off the report object itself, so every
            # kind carries one. What IS checked are two limits measured on drawn objects.
            $isDrawn  = $target.Kind -in @('Line','Box')
            $isLine   = $target.Kind -eq 'Line'
            # A Crystal line lies along one edge and its border IS that line: a horizontal line's
            # style is its top, a vertical line's its left. The other three are discarded on save.
            $lineSide = if ($target.Height -eq 0) { 'top' } else { 'left' }
            $anySide  = $false
            foreach ($side in 'left','right','top','bottom') {
                $v = $op.$side
                if (-not $v) { continue }
                $anySide = $true
                if ($script:BorderStyles -notcontains [string]$v) {
                    Add-Err $i "`"$v`" is not a valid border style for `"$side`"; use one of `"none`", `"single`", `"double`", `"dashed`" or `"dotted`"."
                    continue
                }
                if ($isDrawn -and [string]$v -eq 'double') {
                    Add-Err $i ("Object `"$($op.target)`" is a $($target.Kind), and Crystal has no double line style for " +
                                "a Line or a Box - it answers one with `"The line style value is not valid.`" mid-plan. " +
                                "Use `"single`", `"dashed`" or `"dotted`" for `"$side`", or put the double border on a " +
                                "Text or Field object instead.")
                }
                if ($isLine -and $side -ne $lineSide) {
                    Add-Err $i ("Object `"$($op.target)`" is a Line lying along its `"$lineSide`" edge, and a Line's " +
                                "border IS the line itself: Crystal keeps only `"$lineSide`" and discards `"$side`" on " +
                                "save, reporting success. Set `"$lineSide`" to restyle the line, or put the border on " +
                                "the Text or Field object instead of drawing a Line at all.")
                }
            }
            if (-not $anySide -and $null -eq $op.color) {
                Add-Err $i '"setBorder" requires at least one of "left", "right", "top", "bottom" or "color".'
            }
            elseif ($null -ne $op.color -and -not (Test-VibeyColor $op.color)) {
                Add-Err $i "`"$($op.color)`" is not a valid colour; expected the form `"#RRGGBB`" (e.g. `"#1F2A37`")."
            }
        }
        elseif ($action -eq 'setSectionBackground') {
            if (-not (Test-VibeyColor $op.color)) {
                Add-Err $i "`"$($op.color)`" is not a valid colour; expected the form `"#RRGGBB`" (e.g. `"#1F2A37`")."
            }
        }
        elseif ($action -eq 'setCanGrow') {
            if ($null -eq $op.canGrow) { Add-Err $i '"setCanGrow" requires "canGrow".' }
            elseif ($script:CanGrowKinds -notcontains $target.Kind) {
                Add-Err $i "`"$($op.target)`" is a $($target.Kind) and holds no flowing text."
            }
        }
        elseif ($action -eq 'setNumberFormat') {
            if ($target.Kind -ne 'Field') {
                Add-Err $i "`"$($op.target)`" is a $($target.Kind); only a Field has a number format."
            }
            elseif ($null -eq $op.decimalPlaces -and $null -eq $op.thousandsSeparator -and $null -eq $op.suppressIfZero) {
                Add-Err $i '"setNumberFormat" needs at least one of "decimalPlaces", "thousandsSeparator" or "suppressIfZero".'
            }
            elseif ($null -ne $op.decimalPlaces) {
                $dn = Get-VibeyNumber $op.decimalPlaces
                if (-not $dn.Ok) { Add-Err $i "`"decimalPlaces`" must be a number, got `"$($op.decimalPlaces)`"." }
                elseif ($dn.Value -lt 0 -or $dn.Value -gt 10) { Add-Err $i "decimalPlaces must be 0-10, was $($op.decimalPlaces)." }
            }
        }
        elseif ($action -eq 'setSectionBreak') {
            if ($null -eq $op.newPageBefore -and $null -eq $op.newPageAfter) {
                Add-Err $i '"setSectionBreak" needs "newPageBefore" or "newPageAfter".'
            }
        }
        elseif ($action -eq 'setSuppress') {
            if ($null -eq $op.suppress) { Add-Err $i '"setSuppress" requires "suppress".' }
            elseif ($op.target -and $op.section) { Add-Err $i '"setSuppress" takes "target" or "section", not both.' }
            elseif (-not $op.target -and -not $op.section) { Add-Err $i '"setSuppress" requires "target" or "section".' }
            elseif ($op.target -and $null -ne $op.suppressIfBlank) {
                Add-Err $i '"suppressIfBlank" applies to a section, not an object.'
            }
        }
        elseif ($action -in @('addGroup','addSort')) {
            # They address a FIELD, not an object or a section. addGroup CREATES sections.
            if (-not $op.fieldRef) { Add-Err $i "`"$action`" requires `"fieldRef`"."; continue }
            if ($action -eq 'addSort' -and -not $op.direction) {
                Add-Err $i '"addSort" requires "direction" ("ascending" or "descending").'; continue
            }
            if ($op.direction -and ($script:Directions -notcontains [string]$op.direction)) {
                Add-Err $i ("`"$($op.direction)`" is not a supported direction; use `"ascending`" or `"descending`". " +
                            "Crystal's topN, bottomN, topNPercentage and bottomNPercentage orders are deliberately NOT " +
                            "supported: each needs an N that this operation has no field to express.")
                continue
            }
            # Skipped when the field list is unknown (no database connection) - unlike addField,
            # neither binds new data into the report, so there is nothing to fail closed about.
            if ($fieldRefsKnown -and -not $availableFieldRefs.ContainsKey($op.fieldRef)) {
                Add-Err $i "`"$($op.fieldRef)`" is not a field in this report's data source. Use one of the formulaForm values from the report schema's availableFields."
                continue
            }
            if ($action -eq 'addGroup') {
                # Measured: a second group on an already-grouped field is "The grouping already exists."
                if ($groupFields.ContainsKey($op.fieldRef)) {
                    Add-Err $i "This report is already grouped on `"$($op.fieldRef)`". Crystal refuses a second group on the same field (`"The grouping already exists.`")."
                    continue
                }
                if (-not (Test-Index $i $op.groupIndex $groupList.Count 'groupIndex' 'groups')) { continue }
                Add-Predicted ([ref]$groupList) $op.groupIndex $op.fieldRef
                $groupFields[$op.fieldRef] = $true
                # Measured: adding a group creates a sort on its field - or adopts an existing one.
                if (-not $sortFields.ContainsKey($op.fieldRef)) {
                    Add-Predicted ([ref]$sortList) $op.groupIndex $op.fieldRef
                    $sortFields[$op.fieldRef] = $true
                }
                foreach ($band in 'Header','Footer') {
                    $name = Get-VibeyGroupSectionName -FieldRef $op.fieldRef -Band $band
                    if ($sectionHeights.ContainsKey($name)) { $ambiguousSections[$name] = $true }
                    else { $sectionHeights[$name] = $script:NewGroupSectionHeight }
                }
            } else {
                # Measured: a second sort on a sorted field is "The sorting already exists", and every
                # group carries a sort of its own, so this also covers sorting a grouped field.
                if ($sortFields.ContainsKey($op.fieldRef)) {
                    Add-Err $i ("This report is already sorted on `"$($op.fieldRef)`", and Crystal refuses a second " +
                                "sort on the same field (`"The sorting already exists`"). Every group carries a sort " +
                                "on its own field, so a grouped field is already sorted; use addGroup's `"direction`" " +
                                "to choose a group's order.")
                    continue
                }
                if (-not (Test-Index $i $op.sortIndex $sortList.Count 'sortIndex' 'sorts')) { continue }
                Add-Predicted ([ref]$sortList) $op.sortIndex $op.fieldRef
                $sortFields[$op.fieldRef] = $true
            }
        }
        elseif ($action -eq 'setSubreportLink') {
            # Resolved against the SubreportName name-space, NOT object names: SetSubreportLinks is
            # keyed by the sub-report's own name, and passing the placed container's name fails in
            # COM with the useless "This value is write-only." (measured).
            if (-not $op.target) { Add-Err $i '"setSubreportLink" requires "target".'; continue }
            if (-not $subreportsByName.ContainsKey($op.target)) {
                $byObject = if ($objects.ContainsKey($op.target)) { $objects[$op.target] } else { $null }
                if ($byObject -and $byObject.Kind -eq 'Subreport' -and $byObject.SubreportName) {
                    Add-Err $i "`"$($op.target)`" is the placed object name of a sub-report, which setSubreportLink cannot resolve. Use its `"subreportName`" instead: `"$($byObject.SubreportName)`"."
                } elseif ($byObject) {
                    Add-Err $i "Object `"$($op.target)`" is a $($byObject.Kind), not a Subreport, and cannot take a subreport link."
                } elseif ($removed.ContainsKey($op.target)) {
                    Add-Err $i "Sub-report `"$($op.target)`" was removed earlier in this plan."
                } else {
                    Add-Err $i ("No sub-report named `"$($op.target)`" is embedded in this report. Use a Subreport " +
                                "object's `"subreportName`" from read, or the `"newName`" of an addSubreport earlier in this plan.")
                }
                continue
            }
            if (-not $op.mainReportField) { Add-Err $i '"setSubreportLink" requires "mainReportField".' }
            if (-not $op.subreportField)  { Add-Err $i '"setSubreportLink" requires "subreportField".' }
            # "linkedParameter" is deliberately OPTIONAL: measured, Crystal discards whatever is
            # written and substitutes its own "{?Pm-<mainReportField>}".
        }
        elseif ($action -eq 'removeTable') {
            if (-not $op.target) { Add-Err $i '"removeTable" requires "target".'; continue }
            if (-not (Test-TableExists $i $op.target $action)) { continue }
            # THE rule this operation exists for. Crystal does NOT refuse to remove a table that
            # fields are bound to - measured, it deletes it and leaves the fields unresolvable. A
            # binding is "{alias.column}", matched as a PREFIX so alias "x;1" cannot match "{ax;1.c}".
            $prefix = '{' + $op.target + '.'
            $bound = @($objects.Keys | Where-Object {
                $ds = [string]$objects[$_].DataSource
                $ds -and $ds.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
            } | Sort-Object)
            if ($bound.Count -gt 0) {
                Add-Err $i ("Table `"$($op.target)`" cannot be removed while $($bound.Count) object(s) are still bound " +
                            "to it: $($bound -join ', '). Remove those objects (removeObject) earlier in this same plan, " +
                            "then removeTable will be accepted.")
                continue
            }
            [void]$tables.Remove($op.target)
            $removedTables[$op.target] = $true
            # The table's fields leave addField's allowlist with it.
            if ($fieldRefsByTable.ContainsKey($op.target)) {
                foreach ($r in $fieldRefsByTable[$op.target]) { [void]$availableFieldRefs.Remove($r) }
            }
        }
        elseif ($action -eq 'addTable') {
            # "target" is an EXISTING table whose connection is cloned. That clone is how addTable
            # stays credential-free: no server, database, user or password appears in a plan.
            if (-not $op.target) { Add-Err $i '"addTable" requires "target".'; continue }
            if (-not (Test-TableExists $i $op.target $action)) { continue }
            if (-not $op.tableName) { Add-Err $i '"addTable" requires "tableName".'; continue }
            if (-not $op.newName)   { Add-Err $i '"addTable" requires "newName".'; continue }
            if ($tables.ContainsKey($op.newName)) {
                Add-Err $i "A table with the alias `"$($op.newName)`" is already in this report's data source."; continue
            }
            $tables[$op.newName] = $true
            [void]$removedTables.Remove($op.newName)
            # Deliberately NO fields added to the allowlist: they come from the server, so the plan
            # cannot know them. Re-read the report after this apply before binding to them.
        }
        elseif ($action -eq 'setTableLocation') {
            if (-not $op.target) { Add-Err $i '"setTableLocation" requires "target".'; continue }
            if (-not (Test-TableExists $i $op.target $action)) { continue }
            if (-not $op.tableName) { Add-Err $i '"setTableLocation" requires "tableName".' }
        }
        elseif ($script:AddActions -contains $action) {
            if (-not $op.newName) { Add-Err $i "`"$action`" requires `"newName`"." }
            elseif ($objects.ContainsKey($op.newName)) {
                Add-Err $i "An object named `"$($op.newName)`" already exists."
            }
            # addSubreport's name lands in the SubreportName name-space, which the check above does
            # not look at. Two sub-reports with one name would leave setSubreportLink unable to say
            # which it meant.
            elseif ($action -eq 'addSubreport' -and $subreportsByName.ContainsKey($op.newName)) {
                Add-Err $i "A sub-report named `"$($op.newName)`" is already embedded in this report."
            } else {
                $ln = Get-VibeyNumber $op.leftTwips
                $tn = Get-VibeyNumber $op.topTwips
                $wn = Get-VibeyNumber $op.widthTwips
                $hn = Get-VibeyNumber $op.heightTwips
                if (-not $ln.Ok -or -not $tn.Ok -or -not $wn.Ok -or -not $hn.Ok) {
                    Add-Err $i "`"$action`" requires numeric leftTwips, topTwips, widthTwips and heightTwips."
                    continue
                }
                $l = [int]$ln.Value; $t = [int]$tn.Value
                $w = [int]$wn.Value; $h = [int]$hn.Value
                if ($l -lt 0 -or $t -lt 0 -or $w -lt 0 -or $h -lt 0) { Add-Err $i 'Geometry cannot be negative.' }
                elseif ($action -eq 'addLine' -and $w -ne 0 -and $h -ne 0) {
                    Add-Err $i 'A line must be horizontal or vertical: set widthTwips or heightTwips to 0.'
                }
                else {
                    $secH = $sectionHeights[$op.section]
                    if ($l + $w -gt $printableWidth) {
                        Add-Err $i "`"$($op.newName)`" would end at $($l + $w), past the printable width of $printableWidth."
                    }
                    elseif ($t + $h -gt $secH) {
                        Add-Err $i "`"$($op.newName)`" would end at $($t + $h), past the height of section `"$($op.section)`" ($secH)."
                    }
                    elseif ($action -eq 'addText' -and $null -eq $op.text) { Add-Err $i '"addText" requires "text".' }
                    elseif ($action -eq 'addField' -and -not $op.fieldRef) { Add-Err $i '"addField" requires "fieldRef".' }
                    # addField's security boundary, and unlike addGroup it fails CLOSED on an empty
                    # list: it binds new data into the report, so "unknown" is not good enough.
                    elseif ($action -eq 'addField' -and -not $availableFieldRefs.ContainsKey($op.fieldRef)) {
                        Add-Err $i "`"$($op.fieldRef)`" is not a field in this report's data source. Use one of the formulaForm values from the report schema's availableFields."
                    }
                    elseif ($action -eq 'addSpecialField' -and -not $op.specialType) {
                        Add-Err $i '"addSpecialField" requires "specialType".'
                    }
                    elseif ($action -eq 'addSpecialField' -and ($script:SpecialTypes -notcontains [string]$op.specialType)) {
                        Add-Err $i "`"$($op.specialType)`" is not a supported specialType. Use: $($script:SpecialTypes -join ', ')."
                    }
                    # Pure string checks only - the applier checks the file really exists.
                    elseif ($action -eq 'addSubreport' -and -not $op.reportPath) {
                        Add-Err $i '"addSubreport" requires "reportPath".'
                    }
                    elseif ($action -eq 'addSubreport' -and -not ([string]$op.reportPath).EndsWith('.rpt', [StringComparison]::OrdinalIgnoreCase)) {
                        Add-Err $i "`"reportPath`" must end in `".rpt`"; got `"$($op.reportPath)`"."
                    }
                    # A relative path would resolve against the WORKER's directory, which the caller
                    # never sees, so a "not found" would name a path they never wrote.
                    elseif ($action -eq 'addSubreport' -and -not [IO.Path]::IsPathRooted([string]$op.reportPath)) {
                        Add-Err $i "`"reportPath`" must be an absolute path; got `"$($op.reportPath)`", which would be resolved against the worker's own working directory."
                    }
                    else {
                        $kind = switch ($action) {
                            'addText' { 'Text' } 'addLine' { 'Line' } 'addBox' { 'Box' }
                            'addSubreport' { 'Subreport' }
                            default { 'Field' }   # addField and addSpecialField both place a Field
                        }
                        # Only addField binds to a table; carrying its fieldRef is what makes
                        # "addField from T, then removeTable T" reject in the same plan. A special
                        # field is computed by Crystal and belongs to no table.
                        $ds = if ($action -eq 'addField') { $op.fieldRef } else { $null }
                        $sim = @{ Section = $op.section; Left = $l; Top = $t; Width = $w; Height = $h; Kind = $kind
                                  DataSource = $ds; SubreportName = $null; AddedInPlan = $true }
                        $objects[$op.newName] = $sim
                        [void]$removed.Remove($op.newName)
                        if ($action -eq 'addSubreport') { $sim.SubreportName = $op.newName; $subreportsByName[$op.newName] = $sim }
                    }
                }
            }
        }
        else {
            # F6: VibeyActions is the allowlist and this chain is the enforcement. An action
            # added to the list without a matching branch here must not pass validation with
            # zero checks - it must fail loud, not silently.
            Add-Err $i "`"$action`" is allowlisted but has no validation rule."
        }
    }

    return @{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

# The only input the AI can get wrong in a hundred ways ("red", "rgb(255,0,0)", "#F00", "1F2A37"
# with no hash, trailing whitespace, ...), so callers state the exact expected form.
function Test-VibeyColor([string]$Color) {
    if ($null -eq $Color) { return $false }
    return $Color -match '^#[0-9A-Fa-f]{6}$'
}

Export-ModuleMember -Function Test-VibeyPlan, Get-VibeyGroupSectionName -Variable VibeyActions
