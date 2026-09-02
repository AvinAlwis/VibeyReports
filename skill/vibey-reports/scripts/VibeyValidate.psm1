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
#>

$script:VibeyActions = @(
    'move','resize','resizeSection','setAlignment',
    'addText','addLine','addBox','addField','addSpecialField','removeObject',
    'setFont','setFontSize','setBold',
    'setTextColor','setFillColor','setLineColor','setSectionBackground',
    'setSectionBreak','setNumberFormat','setCanGrow','setSuppress')

$script:FontableKinds = @('Text','Field','FieldHeading')
$script:CanGrowKinds  = @('Text','Field','FieldHeading')
$script:Alignments    = @('Left','Right','Centre','Center','Justified')
$script:SpecialTypes  = @('pageNumber','pageNOfM','totalPageCount','printDate','printTime','reportTitle','recordNumber')
$script:AddActions    = @('addText','addLine','addBox','addField','addSpecialField')

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

    # Simulation state, mutated as the plan is walked.
    $sectionHeights = @{}
    $objects        = @{}
    $removed        = @{}
    foreach ($s in $Schema.sections) {
        $sectionHeights[$s.name] = [int]$s.heightTwips
        foreach ($o in $s.objects) {
            $objects[$o.name] = @{ Section = $s.name; Left = [int]$o.leftTwips; Top = [int]$o.topTwips
                                   Width = [int]$o.widthTwips; Height = [int]$o.heightTwips; Kind = $o.kind }
        }
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

        # --- resolve the target, for the operations that take one -------------------------
        $needsTarget = (@('move','resize','setAlignment','removeObject','setFont','setFontSize',
                         'setBold','setTextColor','setFillColor','setLineColor','setNumberFormat',
                         'setCanGrow') -contains $action) -or
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
        }

        # --- resolve the section, for the operations that take one ------------------------
        $needsSection = ($script:AddActions -contains $action) -or
                        ($action -in @('resizeSection','setSectionBackground','setSectionBreak')) -or
                        ($action -eq 'setSuppress' -and -not $op.target)
        if ($needsSection) {
            if (-not $op.section) { Add-Err $i "`"$action`" requires `"section`"."; continue }
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
                $sectionHeights[$op.section] = $h
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
        elseif ($script:AddActions -contains $action) {
            if (-not $op.newName) { Add-Err $i "`"$action`" requires `"newName`"." }
            elseif ($objects.ContainsKey($op.newName)) {
                Add-Err $i "An object named `"$($op.newName)`" already exists."
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
                    elseif ($action -eq 'addSpecialField' -and -not $op.specialType) {
                        Add-Err $i '"addSpecialField" requires "specialType".'
                    }
                    elseif ($action -eq 'addSpecialField' -and ($script:SpecialTypes -notcontains [string]$op.specialType)) {
                        Add-Err $i "`"$($op.specialType)`" is not a supported specialType. Use: $($script:SpecialTypes -join ', ')."
                    }
                    else {
                        $kind = if ($action -eq 'addText') { 'Text' }
                                elseif ($action -eq 'addLine') { 'Line' }
                                elseif ($action -eq 'addBox') { 'Box' }
                                else { 'Field' }
                        $objects[$op.newName] = @{ Section = $op.section; Left = $l; Top = $t; Width = $w; Height = $h; Kind = $kind }
                        [void]$removed.Remove($op.newName)
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

Export-ModuleMember -Function Test-VibeyPlan -Variable VibeyActions
