# VibeyCrystal.psm1
#
# PARITY. The applier and reader here are a port of src\VibeyReports.CrystalWorker (LayoutApplier.cs,
# ReportReader.cs), which is the reference. The measurements recorded in comments below were taken
# against the installed Crystal XI R2 (11.5) assemblies; docs\sdk-notes.md carries the longer story.

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false

# Exact strong names. See Import-VibeyCrystal.
$script:CrystalEngine = 'CrystalDecisions.CrystalReports.Engine, Version=11.5.3700.0, Culture=neutral, PublicKeyToken=692fbea5521e1304'
$script:CrystalShared = 'CrystalDecisions.Shared, Version=11.5.3700.0, Culture=neutral, PublicKeyToken=692fbea5521e1304'

# The password for the two operations that contact the database. Never a plan field - see
# Get-VibeyDbPassword.
$script:PasswordVariable = 'VIBEY_DB_PASSWORD'

function Import-VibeyCrystal {
    <#
        MEASURED, and it broke this skill outright: loading the engine by PARTIAL name
        ([Reflection.Assembly]::LoadWithPartialName) takes the HIGHEST version in the GAC. On a
        machine that also has the Crystal Reports 13 runtime that is 13.0.4000.0 - which is not
        this SDK, and whose install here is incomplete - so ReportDocument's type initializer threw
        "Could not load file or assembly 'CrystalDecisions.ReportAppServer.CommLayer,
        Version=13.0.4000.0'" and every command failed before opening a report.

        Loading the 11.5 engine by its FULL strong name pulls in its own 11.5 dependencies (the
        ReportAppServer assemblies are 11.5.3300.0) and nothing from 13.x. The C# worker has always
        pinned this version through its project references; this is the same pin.
    #>
    if (-not ([AppDomain]::CurrentDomain.GetAssemblies() | Where-Object { $_.FullName -eq $script:CrystalEngine })) {
        try {
            [void][Reflection.Assembly]::Load($script:CrystalEngine)
            [void][Reflection.Assembly]::Load($script:CrystalShared)
        } catch {
            throw ("Crystal Reports XI R2 (engine 11.5.3700.0) is not installed or not in the GAC: " +
                   "$($_.Exception.Message) This skill needs that exact version; a newer Crystal runtime " +
                   "(13.x) is a different SDK and is deliberately not used.")
        }
    }
}

function Open-VibeyDocument {
    <#
        MEASURED: "New-Object CrystalDecisions.ReportAppServer.ClientDoc.ReportClientDocument"
        HANGS - it opens a TCP connection to port 1566 on the local machine that never
        completes. Loading through ReportDocument and taking .ReportClientDocument gives the
        same object model in about 2ms with no network activity. Do not "simplify" this.
    #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Report not found: $Path" }
    Import-VibeyCrystal
    $rd = New-Object CrystalDecisions.CrystalReports.Engine.ReportDocument
    $rd.Load($Path)
    return $rd
}

function Get-VibeySectionList {
    <#
        MEASURED: ReportDefinition has no "Sections" collection. Sections hang off Areas, and
        the area carries the band kind. Walk Areas -> Sections.
    #>
    param([Parameter(Mandatory)]$Doc)
    $list = @()
    foreach ($area in $Doc.ReportDefController.ReportDefinition.Areas) {
        $first = $true
        foreach ($sec in $area.Sections) {
            $list += [pscustomobject]@{
                Name = $sec.Name; Kind = (ConvertFrom-VibeyAreaKind $area.Kind); Section = $sec
                # A group area can hold several sections (Group Header a, b, c); the first is the
                # one a caller means by "the group header".
                FirstInArea = $first
            }
            $first = $false
        }
    }
    return $list
}

function ConvertFrom-VibeyAreaKind {
    <#
        MEASURED against the installed CrystalDecisions.ReportAppServer.ReportDefModel 11.5.3300.0
        assembly's CrAreaSectionKindEnum (reflection), then cross-checked against
        SampleReport.rpt's five areas in document order: ReportHeader(1), PageHeader(2),
        Detail(4), PageFooter(7), ReportFooter(8).

        The brief's guess (1..7 contiguous: ReportHeader..ReportFooter) is WRONG from
        PageFooter onward. The real enum has a gap: GroupFooter=5, then PageFooter=7 (6 is
        unused/reserved), ReportFooter=8. Using the brief's numbers would have silently
        mis-labelled PageFooterSection1 as "GroupFooter" and ReportFooterSection1 as
        "PageFooter".
    #>
    param($Kind)
    switch ([int]$Kind) {
        1 { 'ReportHeader' } 2 { 'PageHeader' } 3 { 'GroupHeader' } 4 { 'Details' }
        5 { 'GroupFooter' } 7 { 'PageFooter' } 8 { 'ReportFooter' } default { 'Unknown' }
    }
}

function ConvertFrom-VibeyObjectKind {
    <#
        MEASURED against the same assembly's CrReportObjectKindEnum (reflection), then
        cross-checked against SampleReport.rpt's objects: PrintDate1/CardCode1/CardName1/
        PageNumber1 all report kind=1 (Field); Text1/Text2 in PageHeaderSection1 -- despite
        being named "Text1"/"Text2" -- report kind=12, not 2. Crystal's own default naming for
        a FieldHeading uses a "Text" prefix, which is why the fixture has zero objects of
        kind=2 (Text) even though two objects are named "Text*".

        The brief's guess for FieldHeading (8) is WRONG; the real value is 12. 8 is actually
        Crosstab.
    #>
    param($Kind)
    switch ([int]$Kind) {
        1 { 'Field' } 2 { 'Text' } 3 { 'Line' } 4 { 'Box' }
        5 { 'Subreport' } 6 { 'Picture' } 7 { 'Chart' } 8 { 'Crosstab' }
        9 { 'BlobField' } 10 { 'Map' } 11 { 'OlapGrid' } 12 { 'FieldHeading' }
        default { 'Other' }
    }
}

# --- enum vocabularies ------------------------------------------------------------------------
# Every integer below was reflected off the installed 11.5 assemblies, not guessed: this module's
# own history has two wrong guesses in it (area kinds, special field types), and
# CrFieldValueTypeEnum in particular is 0-based (Int8s = 0, String = 11), which a guess would miss.

function ConvertFrom-VibeyAlignment {
    # CrAlignmentEnum: Default 0, Left 1, HorizontalCenter 2, Right 3, Justified 4, Decimal 5.
    param($Value)
    switch ([int]$Value) { 1 { 'Left' } 2 { 'Centre' } 3 { 'Right' } 4 { 'Justified' } default { 'Default' } }
}

function ConvertFrom-VibeyLineStyle {
    # CrLineStyleEnum: NoLine 0, Single 1, Double 2, Dashed 3, Dotted 4. A value with no name here is
    # reported as its number rather than flattened into "none", which would hide a border that exists.
    param($Value)
    switch ([int]$Value) { 0 { 'none' } 1 { 'single' } 2 { 'double' } 3 { 'dashed' } 4 { 'dotted' } default { [string][int]$Value } }
}

function ConvertTo-VibeyLineStyle {
    param([string]$Style)
    switch ($Style.Trim().ToLowerInvariant()) {
        'none' { 0 } 'single' { 1 } 'double' { 2 } 'dashed' { 3 } 'dotted' { 4 }
        default { throw "`"$Style`" is not a border style; the validator should have rejected it." }
    }
}

function ConvertFrom-VibeyValueType {
    # CrFieldValueTypeEnum (0-based): Int8s 0 .. Int32u 5, Number 6, Currency 7, Boolean 8, Date 9,
    # Time 10, String 11, Blob 14, DateTime 15. Everything else - memo, decimal, int64, pictures - is
    # "Other", matching the C# reader.
    param($Value)
    switch ([int]$Value) {
        { $_ -ge 0 -and $_ -le 6 } { 'Number'; break }
        7 { 'Currency' } 8 { 'Boolean' } 9 { 'Date' } 10 { 'Time' } 11 { 'String' }
        14 { 'Blob' } 15 { 'DateTime' }
        default { 'Other' }
    }
}

function ConvertFrom-VibeySortDirection {
    # CrSortDirectionEnum: Ascending 0, Descending 1, TopN 4, BottomN 5, TopNPercentage 6,
    # BottomNPercentage 7. The TopN family cannot be SET here, but a report edited elsewhere may
    # carry one, so it is reported by name rather than flattened into "ascending".
    param($Value)
    switch ([int]$Value) {
        0 { 'ascending' } 1 { 'descending' } 4 { 'topN' } 5 { 'bottomN' }
        6 { 'topNPercentage' } 7 { 'bottomNPercentage' } default { [string][int]$Value }
    }
}

function ConvertTo-VibeySortDirection {
    param([string]$Direction)
    switch ($Direction.ToLowerInvariant()) {
        'ascending'  { [CrystalDecisions.ReportAppServer.DataDefModel.CrSortDirectionEnum]0 }
        'descending' { [CrystalDecisions.ReportAppServer.DataDefModel.CrSortDirectionEnum]1 }
        default { throw "Unsupported direction `"$Direction`"; the validator should have rejected it." }
    }
}

function Get-VibeyComProperty {
    <#
        MEASURED: PowerShell's normal dot-notation property GET/SET fails silently (no
        exception, just returns/leaves $null) on ISCRFont ("CrystalDecisions.ReportAppServer.
        ReportDefModel.Font") reached through FontColor.Font -- even for a value it just wrote
        moments earlier in the very same variable. Reflection's InvokeMember against the COM
        object's runtime type works correctly for both get and set on this object every time it
        was checked (Name/Size/Bold), including across a Clone -> Modify -> SaveAs -> reopen
        round trip. Plain Left/Top/Width/Height on the report object itself are unaffected --
        this quirk is specific to this nested Font COM object, so only its accessors route
        through here rather than converting every property access in the module.
    #>
    param([Parameter(Mandatory)]$ComObject, [Parameter(Mandatory)][string]$Name)
    return $ComObject.GetType().InvokeMember($Name, [Reflection.BindingFlags]::GetProperty, $null, $ComObject, @())
}

function Set-VibeyComProperty {
    param([Parameter(Mandatory)]$ComObject, [Parameter(Mandatory)][string]$Name, $Value)
    $ComObject.GetType().InvokeMember($Name, [Reflection.BindingFlags]::SetProperty, $null, $ComObject, @($Value)) | Out-Null
}

# A property read that must never fail the whole report read. Returns $null when the property is
# absent on this object kind or the read throws.
function Get-VibeyOptional($ComObject, [string]$Name) {
    if ($null -eq $ComObject) { return $null }
    try { return Get-VibeyComProperty -ComObject $ComObject -Name $Name } catch { return $null }
}

function Get-VibeySubreportLinks {
    <#
        Reads back exactly what setSubreportLink writes, via GetSubreportLinks. A sub-report with no
        links yet may throw here rather than return an empty collection, so it is wrapped: that
        sub-report reads back with an empty list instead of failing the whole report read.

        MEASURED: in PowerShell the collection must be used AS RETURNED. Casting it to
        ISCRSubreportLinks (what the C# reader does) fails with "Cannot convert the System.__ComObject
        value" - silently here, inside the try, so a sub-report WITH links read back as having none.
        Plain .Count and the indexer work.
    #>
    param([Parameter(Mandatory)]$Doc, [string]$SubreportName)
    $result = @()
    if (-not $SubreportName) { return $result }
    try {
        $links = $Doc.SubreportController.GetSubreportLinks($SubreportName)
        if ($null -eq $links) { return $result }
        for ($k = 0; $k -lt $links.Count; $k++) {
            $link = $links[$k]
            $result += [ordered]@{
                mainReportFieldName = [string]$link.MainReportFieldName
                subreportFieldName  = [string]$link.SubreportFieldName
                linkedParameterName = [string]$link.LinkedParameterName
            }
        }
    } catch { }
    return $result
}

function Get-VibeyObjectInfo {
    param([Parameter(Mandatory)]$ReportObject, $Doc)
    $o = $ReportObject
    $info = [ordered]@{
        name = $o.Name; kind = (ConvertFrom-VibeyObjectKind $o.Kind)
        leftTwips = [int]$o.Left; topTwips = [int]$o.Top
        widthTwips = [int]$o.Width; heightTwips = [int]$o.Height
        text = $null; dataSource = $null
        fontName = $null; fontSizePt = $null; bold = $null; italic = $null; underline = $null
        alignment = $null; canGrow = $null; suppressed = $null
        decimalPlaces = $null; thousandsSeparator = $null; numberFormat = $null
        textColorHex = $null; fillColorHex = $null; lineColorHex = $null; lineThicknessTwips = $null
        border = $null
        subreportName = $null; subreportLinks = $null
    }
    # Fonts are reached through FontColor.Font, and read via Get-VibeyComProperty (see its
    # comment) rather than dot notation. On SampleReport.rpt's own objects, none carry an
    # explicitly-set font (they inherit the report's default), so Name/Size/Bold legitimately
    # come back null there -- not a broken accessor -- which is why this is left as null rather
    # than defaulted to a guess.
    $fc = $null
    try { $fc = $o.FontColor } catch { }
    if ($fc -and $fc.Font) {
        $font = $fc.Font
        $fontName = Get-VibeyComProperty -ComObject $font -Name 'Name'
        $fontSize = Get-VibeyComProperty -ComObject $font -Name 'Size'
        $fontBold = Get-VibeyComProperty -ComObject $font -Name 'Bold'
        $fontItal = Get-VibeyOptional $font 'Italic'
        $fontUndr = Get-VibeyOptional $font 'Underline'
        if ($null -ne $fontName) { $info.fontName   = $fontName }
        if ($null -ne $fontSize) { $info.fontSizePt = [double]$fontSize }
        if ($null -ne $fontBold) { $info.bold       = [bool]$fontBold }
        if ($null -ne $fontItal) { $info.italic     = [bool]$fontItal }
        if ($null -ne $fontUndr) { $info.underline  = [bool]$fontUndr }
        # FontColor.Color is one level of nested-COM indirection, the same shape as
        # FontColor.Font's Name/Size/Bold, so it is read through Get-VibeyComProperty rather
        # than trusted to dot notation, for the same reason those three are.
        $colorVal = Get-VibeyComProperty -ComObject $fc -Name 'Color'
        if ($null -ne $colorVal) { $info.textColorHex = ConvertFrom-VibeyColorRef -ColorRef $colorVal }
    }

    # What setAlignment, setCanGrow and setSuppress's object form write. ISCRObjectFormat is carried
    # by every kind of report object, so these are read for all of them.
    $fmt = Get-VibeyOptional $o 'Format'
    if ($fmt) {
        $al = Get-VibeyOptional $fmt 'HorizontalAlignment'
        $cg = Get-VibeyOptional $fmt 'EnableCanGrow'
        $sp = Get-VibeyOptional $fmt 'EnableSuppress'
        if ($null -ne $al) { $info.alignment  = ConvertFrom-VibeyAlignment $al }
        if ($null -ne $cg) { $info.canGrow    = [bool]$cg }
        if ($null -ne $sp) { $info.suppressed = [bool]$sp }
    }

    # What setBorder writes. The border hangs off the report object itself, so it is read for every
    # kind; a border that cannot be read leaves the property null rather than failing the read.
    $border = Get-VibeyOptional $o 'Border'
    if ($border) {
        try {
            $info.border = [ordered]@{
                left     = ConvertFrom-VibeyLineStyle (Get-VibeyComProperty -ComObject $border -Name 'LeftLineStyle')
                right    = ConvertFrom-VibeyLineStyle (Get-VibeyComProperty -ComObject $border -Name 'RightLineStyle')
                top      = ConvertFrom-VibeyLineStyle (Get-VibeyComProperty -ComObject $border -Name 'TopLineStyle')
                bottom   = ConvertFrom-VibeyLineStyle (Get-VibeyComProperty -ComObject $border -Name 'BottomLineStyle')
                colorHex = ConvertFrom-VibeyColorRef -ColorRef (Get-VibeyComProperty -ComObject $border -Name 'BorderColor')
            }
        } catch { $info.border = $null }
    }

    # FillColor/LineColor/LineThickness are plain top-level properties on the object itself (the
    # same level Left/Top/Width/Height sit at), not nested through another COM object, so plain dot
    # notation is used -- only Box/Line objects carry them, hence the try/catch.
    try { if ($null -ne $o.FillColor) { $info.fillColorHex = ConvertFrom-VibeyColorRef -ColorRef $o.FillColor } } catch { }
    try { if ($null -ne $o.LineColor) { $info.lineColorHex = ConvertFrom-VibeyColorRef -ColorRef $o.LineColor } } catch { }
    # Without this a rebuild flattens every rule to Crystal's default weight of 15 twips.
    if ($info.kind -in 'Box','Line') {
        try { if ($null -ne $o.LineThickness) { $info.lineThicknessTwips = [int]$o.LineThickness } } catch { }
    }
    try { if ($o.Text)       { $info.text       = $o.Text } }       catch { }
    try { if ($o.DataSource) { $info.dataSource = $o.DataSource } } catch { }

    # Number format is reached through FieldFormat.NumericFormat -- nested COM objects the same
    # way FontColor.Font is (see Get-VibeyComProperty's comment), so read via reflection rather
    # than dot notation. Only a Field carries FieldFormat/NumericFormat.
    if ($info.kind -eq 'Field') {
        try {
            $ff = $o.FieldFormat
            if ($ff -and $ff.NumericFormat) {
                $nf = $ff.NumericFormat
                $dp = Get-VibeyComProperty -ComObject $nf -Name 'NDecimalPlaces'
                $ts = Get-VibeyComProperty -ComObject $nf -Name 'ThousandsSeparator'
                $sz = Get-VibeyOptional $nf 'EnableSuppressIfZero'
                $sd = Get-VibeyOptional $ff.CommonFormat 'EnableSystemDefault'
                if ($null -ne $dp) { $info.decimalPlaces = [int]$dp }
                if ($null -ne $ts) { $info.thousandsSeparator = [bool]$ts }
                # The nested form the C# reader returns. systemDefault matters: while it is true,
                # Crystal formats from the locale and the other three do not describe what renders.
                $info.numberFormat = [ordered]@{
                    decimalPlaces      = $info.decimalPlaces
                    thousandsSeparator = $info.thousandsSeparator
                    suppressIfZero     = if ($null -ne $sz) { [bool]$sz } else { $null }
                    systemDefault      = if ($null -ne $sd) { [bool]$sd } else { $null }
                }
            }
        } catch { }
    }

    if ($info.kind -eq 'Subreport') {
        # MEASURED: the placed OBJECT's Name ("Subreport1") is Crystal's own container name. The
        # name addSubreport supplied becomes SubreportName instead - a separate identifier, and the
        # only one setSubreportLink can use. Reported so a sub-report embedded by an earlier plan
        # can still be linked.
        $sn = Get-VibeyOptional $o 'SubreportName'
        $info.subreportName  = [string]$sn
        $info.subreportLinks = @(if ($Doc) { Get-VibeySubreportLinks -Doc $Doc -SubreportName $sn })
    }
    return $info
}

function Get-VibeyAvailableFields {
    <#
        The report's data-source fields - addField's allowlist, and the table name-space
        addTable/removeTable/setTableLocation act on. Reading must never require a live database, so
        any failure here leaves the list EMPTY for the report rather than failing the read; the
        validator treats an empty list as "unknown", not "no tables".
    #>
    param([Parameter(Mandatory)]$Doc)
    $fields = @()
    try {
        foreach ($t in $Doc.DatabaseController.Database.Tables) {
            foreach ($f in $t.DataFields) {
                $fields += [ordered]@{
                    name        = [string]$f.Name
                    formulaForm = [string]$f.FormulaForm
                    tableAlias  = [string]$f.TableAlias
                    valueType   = ConvertFrom-VibeyValueType $f.Type
                    headingText = [string]$f.HeadingText
                }
            }
        }
    } catch { $fields = @() }
    return $fields
}

function Get-VibeyGroupsAndSorts {
    <#
        Reads back what addGroup and addSort write. A group's direction is NOT a property of the
        group: measured, the group options carry none, and adding a group creates a SORT on its field
        instead. So the sorts are read first and each group takes its direction from the sort on its
        own field.

        Crystal does not link a group to its sections, so the header/footer names are mapped by
        position: headers appear in group order, footers in REVERSE order (group 0 is the outermost,
        so its footer prints last). A name is only claimed when the counts line up exactly - a wrong
        name is worse than none, since it is the name a caller would place objects into.
    #>
    param([Parameter(Mandatory)]$Doc, [string[]]$HeaderSections, [string[]]$FooterSections)
    $groups = @(); $sorts = @()
    try {
        $dd = $Doc.DataDefController.DataDefinition
        foreach ($s in $dd.Sorts) {
            $sorts += [ordered]@{
                fieldRef  = if ($s.SortField) { [string]$s.SortField.FormulaForm } else { '' }
                direction = ConvertFrom-VibeySortDirection $s.Direction
            }
        }
        $all = @($dd.Groups)
        $headers = @($HeaderSections); $footers = @($FooterSections)
        for ($g = 0; $g -lt $all.Count; $g++) {
            $grp = $all[$g]
            $ref = if ($grp.ConditionField) { [string]$grp.ConditionField.FormulaForm } else { '' }
            $dir = ''
            foreach ($s in $sorts) { if ($ref -and $s.fieldRef -eq $ref) { $dir = $s.direction; break } }
            $groups += [ordered]@{
                fieldRef      = $ref
                direction     = $dir
                headerSection = if ($headers.Count -eq $all.Count) { $headers[$g] } else { $null }
                footerSection = if ($footers.Count -eq $all.Count) { $footers[$all.Count - 1 - $g] } else { $null }
            }
        }
    } catch { $groups = @(); $sorts = @() }
    return @{ Groups = $groups; Sorts = $sorts }
}

function Get-VibeySchema {
    <#
        MEASURED: PrintOutputController has no GetPageMargins() method (the brief's guess).
        The real method is GetPrintOptions(), whose ISCRPrintOptions carries PageContentWidth/
        PageContentHeight (the PRINTABLE area, margins already excluded) and a PageMargins
        object with Left/Right/Top/Bottom int properties (not leftMargin/rightMargin/...).

        There is no direct paper-size-in-twips property anywhere on this object (only a
        PaperSize enum, which cannot express a custom size), so the full paper size is
        reconstructed by adding the margins back onto the content size -- the same approach
        docs/sdk-notes.md's C# reference (ReportReader.cs) already uses and the same one that
        is measured to reproduce the fixture's known A4 size:
        PageContentWidth(11186) + Left(360) + Right(360) = 11906
        PageContentHeight(16118) + Top(360) + Bottom(360) = 16838
    #>
    param([Parameter(Mandatory)][string]$Path)
    $rd = Open-VibeyDocument -Path $Path
    try {
        $doc = $rd.ReportClientDocument
        $printOptions = $doc.PrintOutputController.GetPrintOptions()
        $margins = $printOptions.PageMargins
        $sections = @()
        $groupHeaders = @(); $groupFooters = @()
        foreach ($entry in (Get-VibeySectionList -Doc $doc)) {
            if ($entry.FirstInArea -and $entry.Kind -eq 'GroupHeader') { $groupHeaders += $entry.Name }
            if ($entry.FirstInArea -and $entry.Kind -eq 'GroupFooter') { $groupFooters += $entry.Name }
            $objects = @()
            foreach ($ro in $entry.Section.ReportObjects) { $objects += (Get-VibeyObjectInfo -ReportObject $ro -Doc $doc) }
            # Every property setSectionBreak and setSuppress's section form, plus
            # setSectionBackground, can write. ISCRSectionFormat is a nested COM object one level
            # in from the section (same shape as FontColor.Font), so every read here goes through
            # Get-VibeyComProperty rather than dot notation. A write-only property leaves the
            # caller with no way to prove a change persisted -- see the Task 6 fix report.
            $suppressed = $null; $newPageBefore = $null; $newPageAfter = $null
            $suppressIfBlank = $null; $backgroundColorHex = $null
            if ($entry.Section.Format) {
                $fmt = $entry.Section.Format
                $sv = Get-VibeyComProperty -ComObject $fmt -Name 'EnableSuppress'
                $nb = Get-VibeyComProperty -ComObject $fmt -Name 'EnableNewPageBefore'
                $na = Get-VibeyComProperty -ComObject $fmt -Name 'EnableNewPageAfter'
                $sb = Get-VibeyComProperty -ComObject $fmt -Name 'EnableSuppressIfBlank'
                $bg = Get-VibeyComProperty -ComObject $fmt -Name 'BackgroundColor'
                if ($null -ne $sv) { $suppressed        = [bool]$sv }
                if ($null -ne $nb) { $newPageBefore      = [bool]$nb }
                if ($null -ne $na) { $newPageAfter       = [bool]$na }
                if ($null -ne $sb) { $suppressIfBlank    = [bool]$sb }
                if ($null -ne $bg) { $backgroundColorHex = ConvertFrom-VibeyColorRef -ColorRef $bg }
            }
            $sections += [ordered]@{
                name = $entry.Name; kind = $entry.Kind
                heightTwips = [int]$entry.Section.Height; objects = $objects
                suppressed = $suppressed; newPageBefore = $newPageBefore; newPageAfter = $newPageAfter
                suppressIfBlank = $suppressIfBlank; backgroundColorHex = $backgroundColorHex
            }
        }
        $gs = Get-VibeyGroupsAndSorts -Doc $doc -HeaderSections $groupHeaders -FooterSections $groupFooters
        return [ordered]@{
            reportPath = $Path
            page = [ordered]@{
                widthTwips  = [int]$printOptions.PageContentWidth  + [int]$margins.Left + [int]$margins.Right
                heightTwips = [int]$printOptions.PageContentHeight + [int]$margins.Top  + [int]$margins.Bottom
                marginLeftTwips   = [int]$margins.Left
                marginRightTwips  = [int]$margins.Right
                marginTopTwips    = [int]$margins.Top
                marginBottomTwips = [int]$margins.Bottom
            }
            sections = $sections
            availableFields = @(Get-VibeyAvailableFields -Doc $doc)
            groups = @($gs.Groups)
            sorts  = @($gs.Sorts)
        }
    } finally { $rd.Close() }
}

function Invoke-VibeyRead {
    param([Parameter(Mandatory)]$Request)
    if (-not $Request.reportPath) { return @{ ok = $false; error = '"read" requires "reportPath".' } }
    return @{ ok = $true; schema = (Get-VibeySchema -Path $Request.reportPath) }
}

function Save-VibeyDocument {
    <#
        MEASURED: SaveAs takes (name, directory, options) - THREE arguments. It is not
        SaveAs(path, overwrite); that overload does not exist and fails with
        "Cannot find an overload for SaveAs and the argument count: 2".

        Writes to a temporary name in the destination directory and moves on success, so a
        save that fails partway cannot leave a corrupt file where the caller asked for output.

        FIX (round 1): the move itself can still fail (destination locked by the Designer, an
        AV scan holding the handle, a permissions problem) - that left the temp file behind
        permanently, under a name nobody would recognise as garbage. The move is now wrapped so
        a failed move deletes the temp file before rethrowing; the caller still sees the error.
    #>
    param([Parameter(Mandatory)]$Doc, [Parameter(Mandatory)][string]$OutputPath)
    $dir = [IO.Path]::GetDirectoryName($OutputPath)
    if (-not $dir) { $dir = (Get-Location).Path }
    if (-not (Test-Path -LiteralPath $dir)) { throw "Output directory does not exist: $dir" }
    $tmpName = 'vibey-' + [Guid]::NewGuid().ToString('N') + '.rpt'
    $tmpPath = Join-Path $dir $tmpName
    $Doc.SaveAs($tmpName, $dir, 0)          # 0 = crReportOptionDefault
    try {
        Move-Item -LiteralPath $tmpPath -Destination $OutputPath -Force
    } catch {
        Remove-Item -LiteralPath $tmpPath -Force -ErrorAction SilentlyContinue
        throw
    }
}

function Find-VibeyObject {
    param([Parameter(Mandatory)]$Doc, [Parameter(Mandatory)][string]$Name)
    foreach ($entry in (Get-VibeySectionList -Doc $Doc)) {
        foreach ($ro in $entry.Section.ReportObjects) {
            if ($ro.Name -eq $Name) { return @{ Object = $ro; Section = $entry.Section } }
        }
    }
    throw "Object `"$Name`" was not found; the validator should have rejected it."
}

function Find-VibeySection {
    param([Parameter(Mandatory)]$Doc, [Parameter(Mandatory)][string]$Name)
    foreach ($entry in (Get-VibeySectionList -Doc $Doc)) {
        if ($entry.Name -eq $Name) { return $entry.Section }
    }
    throw "Section `"$Name`" was not found; the validator should have rejected it."
}

function Set-VibeyObject {
    <#
        MEASURED: RAS objects cannot be mutated in place - setting a property on the live
        object does not persist. Clone, change the clone, then Modify(old, new).
    #>
    param([Parameter(Mandatory)]$Doc, [Parameter(Mandatory)]$Original, [Parameter(Mandatory)]$Modified)
    $Doc.ReportDefController.ReportObjectController.Modify($Original, $Modified)
}

function Sync-VibeyEndpoints {
    <#
        MEASURED: for a Line or a Box, Right/Bottom ARE the geometry - Width/Height are derived from
        them. Moving or resizing one by Left/Top/Width/Height alone leaves Right/Bottom behind, so the
        object silently changes size on save. The C# applier has always synced them; this module did
        not, and its tests only ever moved text, so it went unnoticed.
    #>
    param([Parameter(Mandatory)]$Object)
    $k = ConvertFrom-VibeyObjectKind $Object.Kind
    if ($k -in 'Line','Box') {
        $Object.Right  = [int]$Object.Left + [int]$Object.Width
        $Object.Bottom = [int]$Object.Top  + [int]$Object.Height
    }
}

function ConvertTo-VibeyColorRef {
    <#
        MEASURED (cross-checked against the C# reference's ColorRef.FromHex): Crystal stores
        colour as COLORREF, 0x00BBGGRR - the byte order is REVERSED from HTML hex. Feeding
        0xRRGGBB straight through swaps red and blue, which renders as a plausible wrong colour
        rather than an error.
    #>
    param([Parameter(Mandatory)][string]$Hex)
    $h = $Hex.TrimStart('#')
    if ($h.Length -ne 6) { throw "Colour must be six hex digits, was `"$Hex`"." }
    $r = [Convert]::ToInt32($h.Substring(0,2),16)
    $g = [Convert]::ToInt32($h.Substring(2,2),16)
    $b = [Convert]::ToInt32($h.Substring(4,2),16)
    return (($b -shl 16) -bor ($g -shl 8) -bor $r)
}

function ConvertFrom-VibeyColorRef {
    <#
        The reverse of ConvertTo-VibeyColorRef, for reading colours back out of a report:
        Crystal's UInt32 COLORREF (0x00BBGGRR) -> "#RRGGBB".

        MEASURED sentinel (per the C# reference, src\VibeyReports.Contracts\ColorRef.cs, whose
        header documents how it was established): 0xFFFFFFFF is Crystal's "no colour set" value
        on FillColor and section BackgroundColor when nothing was ever explicitly set, and is
        mapped to $null here rather than the nonsensical "#FFFFFF", which would collide with a
        real, explicitly set white. LineColor/FontColor.Color were not observed to carry this
        sentinel (they default to black/0), but the mapping is applied uniformly to all colour
        properties since Crystal exposes no separate "is this colour set" flag on any of them.

        COM hands this back as a signed Int32 (0xFFFFFFFF reads as -1), so it is normalised to
        an unsigned 32-bit value via a bitmask before the sentinel comparison and channel
        extraction, rather than compared/shifted as a signed number.
    #>
    param([Parameter(Mandatory)]$ColorRef)
    $u = [uint32]([int64]$ColorRef -band 0xFFFFFFFFL)
    # [uint32]::MaxValue, NOT the literal 0xFFFFFFFF: Windows PowerShell 5.1 parses that literal as
    # an Int32, i.e. -1, so "$u -eq 0xFFFFFFFF" was never true and every unset fill and section
    # background read back as a real "#FFFFFF". Found by diffing this reader against the C# one.
    if ($u -eq [uint32]::MaxValue) { return $null }
    $r = $u -band 0xFF
    $g = ($u -shr 8) -band 0xFF
    $b = ($u -shr 16) -band 0xFF
    return ('#{0:X2}{1:X2}{2:X2}' -f $r, $g, $b)
}

function New-VibeyDefaultFontColor {
    <#
        MEASURED: a freshly constructed FieldObjectClass comes back with FontColor == $null
        (a freshly constructed TextObjectClass does not -- it already carries a non-null
        FontColor/Font pair). A null FontColor makes the object unfontable: the existing
        setFont/setFontSize/setBold arms dereference $clone.FontColor.Font, which throws on
        $null. So a field this task adds (addField, addSpecialField) needs a FontColor
        supplied up front, or a setFontSize in the very same plan (both Task-5 tests do
        exactly this) would fail on an object this operation itself just created.
        Size is written through Set-VibeyComProperty rather than dot notation, for the same
        reason setFontSize already does -- see that function's comment for the measured
        silent-no-op trap on this COM type.
    #>
    $fontColor = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.FontColorClass
    $font = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.FontClass
    $font.Name = 'Arial'
    Set-VibeyComProperty -ComObject $font -Name 'Size' -Value ([decimal]10)
    $fontColor.Font = $font
    return $fontColor
}

function Get-VibeyDefaultFontColor {
    <#
        The font an added Text or Field starts with: a COPY of the first Text or Field already in the
        section, so an addition matches its neighbours; Arial 10 when the section has none (a new
        group header, say). Ported from the C# DefaultFontColorFor.

        MEASURED difference before this existed: text added into a brand-new group header came out
        as "MS Shell Dlg" 14pt bold here and Arial 10pt from the C# applier.

        A fresh FontColorClass/FontClass pair is built on every call - never shared - because the text
        object and its paragraph run each need their own. The font's properties are written through
        Set-VibeyComProperty for the reason given at Get-VibeyComProperty.
    #>
    param([Parameter(Mandatory)]$Doc, [Parameter(Mandatory)][string]$SectionName)
    $section = Find-VibeySection -Doc $Doc -Name $SectionName
    $source = $null
    foreach ($ro in $section.ReportObjects) {
        if ((ConvertFrom-VibeyObjectKind $ro.Kind) -notin 'Field','Text','FieldHeading') { continue }
        $fc = $null
        try { $fc = $ro.FontColor } catch { }
        if ($fc -and $fc.Font) { $source = $fc.Font; break }
    }
    if ($null -eq $source) { return New-VibeyDefaultFontColor }

    $fontColor = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.FontColorClass
    $font = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.FontClass
    foreach ($prop in 'Name','Size','Bold','Italic','Underline','Strikethrough','Weight','Charset') {
        $v = Get-VibeyOptional $source $prop
        if ($null -ne $v) { try { Set-VibeyComProperty -ComObject $font -Name $prop -Value $v } catch { } }
    }
    $fontColor.Font = $font
    return $fontColor
}

# --- data-source tables -----------------------------------------------------------------------
#
# The boundary these sit on: report METADATA may be written, the database is only ever READ. addTable
# CLONES the connection of a table already in the report and setTableLocation keeps the table's own,
# so no server, database or user name is ever expressed in a plan.
#
# The one exception: addTable and setTableLocation make Crystal contact the server, and a report keeps
# its connection's user name but NEVER its password (measured). So those two take the password from
# the VIBEY_DB_PASSWORD environment variable, set on a CLONE right before the call. It is not a plan
# field, is never read back out of a connection, and is scrubbed from every message these can throw,
# because Crystal's own error text is composed by the database driver. removeTable needs none of it.

function Get-VibeyDbPassword {
    param([Parameter(Mandatory)][string]$Action)
    $value = [Environment]::GetEnvironmentVariable($script:PasswordVariable)
    if ([string]::IsNullOrEmpty($value)) {
        throw ("`"$Action`" needs a database password, and the environment variable $script:PasswordVariable is not set. " +
               "Crystal contacts the database server to verify the table before this operation can complete, and " +
               "a report stores its connection's user name but never its password, so the password has to be " +
               "supplied out of band. Set $script:PasswordVariable to the password of the database user the " +
               "report's saved connection logs on as, in the environment of the process that runs this skill, " +
               "then retry. It is deliberately NOT a field of the plan - plans are JSON written to disk and must " +
               "never record a credential. Every other operation, removeTable included, needs no password.")
    }
    return $value
}

function Protect-VibeySecret([string]$Text, [string]$Secret) {
    if ([string]::IsNullOrEmpty($Text) -or [string]::IsNullOrEmpty($Secret)) { return $Text }
    return $Text.Replace($Secret, '***')
}

function Find-VibeyTable {
    # Case-insensitive, the way the validator matches aliases.
    param([Parameter(Mandatory)]$Doc, [Parameter(Mandatory)][string]$Alias)
    foreach ($t in $Doc.DatabaseController.Database.Tables) {
        if ([string]::Equals([string]$t.Alias, $Alias, [StringComparison]::OrdinalIgnoreCase)) { return $t }
    }
    return $null
}

function Get-VibeyRequiredTable {
    # The validator's own existence check is skipped when the field list is unknown, so absence is
    # reported here, naming the aliases that DO exist.
    param([Parameter(Mandatory)]$Doc, [Parameter(Mandatory)][string]$Alias, [Parameter(Mandatory)][string]$Action)
    $t = Find-VibeyTable -Doc $Doc -Alias $Alias
    if ($null -eq $t) {
        $known = @(foreach ($x in $Doc.DatabaseController.Database.Tables) { [string]$x.Alias })
        $list = if ($known.Count) { $known -join ', ' } else { '(this report has no data-source tables)' }
        throw "`"$Action`": no table with the alias `"$Alias`" is in this report's data source. Aliases present: $list."
    }
    return $t
}

function Get-VibeyQualifiedName {
    <#
        Keeps the source table's catalog/schema prefix and swaps in the new object name. Measured
        shape on the PMS reports: Name "sp_x;1", QualifiedName "hrmmain_philippinesdev.PeoplesHR.sp_x;1"
        - the qualified name ENDS WITH the name, ";1" included. Replacing that suffix, rather than
        splitting on the last dot, is what keeps the ";1" intact.
    #>
    param($Source, [string]$TableName)
    $qualified = [string]$Source.QualifiedName
    $name = [string]$Source.Name
    if ($name.Length -gt 0 -and $qualified.EndsWith($name, [StringComparison]::OrdinalIgnoreCase)) {
        return $qualified.Substring(0, $qualified.Length - $name.Length) + $TableName
    }
    $dot = $qualified.LastIndexOf('.')
    if ($dot -ge 0) { return $qualified.Substring(0, $dot + 1) + $TableName }
    return $TableName
}

function Get-VibeyUserDescription($Table) {
    # A user name is not a credential - it is already saved in the .rpt. The password is never read.
    $user = $null
    try { $user = [string]$Table.ConnectionInfo.UserName } catch { }
    if ([string]::IsNullOrEmpty($user)) { return 'no user name saved, so the connection expects integrated security' }
    return "user `"$user`""
}

function Test-VibeyIsProcedure($Table) {
    # Mirror the shape the report already uses rather than picking one. Every table in the PMS
    # reports is a stored procedure (ClassName "CrystalReports.Procedure").
    if ($Table -is [CrystalDecisions.ReportAppServer.DataDefModel.ISCRProcedure]) { return $true }
    try { return ([string]$Table.ClassName) -like '*Procedure*' } catch { return $false }
}

function Resolve-VibeySubreportName {
    <#
        Maps setSubreportLink's target onto the exact name SubreportController is keyed by. Passing a
        name the controller does not know fails in COM with "This value is write-only." (measured) -
        useless - so it is checked here first, and the casing is canonicalised, since the validator
        matches case-insensitively and COM does not.
    #>
    param([Parameter(Mandatory)]$Doc, [Parameter(Mandatory)][string]$Requested)
    $known = @()
    foreach ($n in $Doc.SubreportController.GetSubreportNames()) {
        $name = [string]$n
        $known += $name
        if ([string]::Equals($name, $Requested, [StringComparison]::OrdinalIgnoreCase)) { return $name }
    }
    $list = if ($known.Count) { "Embedded sub-reports: $($known -join ', ')." } else { 'The report embeds no sub-reports.' }
    throw "`"setSubreportLink`": `"$Requested`" is not a sub-report in this report. $list Use a Subreport object's `"subreportName`" from read, not its object `"name`"."
}

function Invoke-VibeyApply {
    param([Parameter(Mandatory)]$Request)

    foreach ($required in 'reportPath','outputPath') {
        if (-not $Request.$required) { return @{ ok = $false; error = "`"apply`" requires `"$required`"." } }
    }
    if ((Test-Path -LiteralPath $Request.outputPath) -and -not $Request.overwrite) {
        return @{ ok = $false; error = "Output file already exists and `"overwrite`" was not set: $($Request.outputPath)" }
    }
    if ([IO.Path]::GetFullPath($Request.reportPath) -eq [IO.Path]::GetFullPath($Request.outputPath)) {
        return @{ ok = $false; error = 'outputPath must differ from reportPath; the source report is never modified.' }
    }

    # THE GATE. Nothing below this line runs unless the whole plan passes.
    $schema = Get-VibeySchema -Path $Request.reportPath
    $check  = Test-VibeyPlan -Plan $Request.plan -Schema $schema
    if (-not $check.IsValid) {
        return @{ ok = $false; error = 'Layout plan failed validation.'
                  validationErrors = @($check.Errors | ForEach-Object { @{ operationIndex = $_.OperationIndex; message = $_.Message } }) }
    }

    $rd = Open-VibeyDocument -Path $Request.reportPath
    $removedObjects = @()
    $removedTables  = @()
    try {
        $doc = $rd.ReportClientDocument
        $n = 0
        foreach ($op in $Request.plan.operations) {
            $n++
            switch ([string]$op.action) {
                'resizeSection' {
                    <#
                        MEASURED: ReportSectionController has no Modify method (the brief's
                        guess) -- printing its members shows only Add(section, area, index),
                        Remove(section), AutoFitSections(variant) and SetProperty(section,
                        CrReportSectionPropertyEnum, variant). There is no clone/mutate/Modify
                        dance for a section; SetProperty mutates the live section in place, and
                        that mutation is what survives SaveAs. CrReportSectionPropertyEnum was
                        reflected from CrystalDecisions.ReportAppServer.Controllers (a different
                        namespace than the ReportDefModel enums used elsewhere in this file):
                        crReportSectionPropertyName=0, ...Format=1, ...Height=2.
                    #>
                    $sec = Find-VibeySection -Doc $doc -Name $op.section
                    $doc.ReportDefController.ReportSectionController.SetProperty($sec, 2, [int]$op.heightTwips)
                }
                'move' {
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    $clone.Left = [int]$op.leftTwips; $clone.Top = [int]$op.topTwips
                    Sync-VibeyEndpoints -Object $clone
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
                }
                'resize' {
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    $clone.Width = [int]$op.widthTwips; $clone.Height = [int]$op.heightTwips
                    Sync-VibeyEndpoints -Object $clone
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
                }
                'moveToSection' {
                    <#
                        Changes which SECTION an object belongs to. Crystal has no reparent call, so
                        this clones, removes the original and adds the clone to the destination - the
                        one route that keeps a text object's embedded field runs, which rebuilding with
                        addText cannot reproduce.

                        Remove runs BEFORE Add so the report never holds two objects of one name. If
                        Add then throws, nothing is saved and the source is untouched.

                        CAVEAT (measured, docs\sdk-notes.md): the SOURCE section can no longer shrink
                        below where the object was. Rebuild with add* instead when the source section
                        has to be resized afterwards.

                        A Line/Box also gets EndSectionName pointed at its new section - addLine/addBox
                        always set it to the section they are placed in, and a clone carries the old one.
                    #>
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $dest = Find-VibeySection -Doc $doc -Name $op.section
                    $clone = $f.Object.Clone($true)
                    if ($null -ne $op.leftTwips) { $clone.Left = [int]$op.leftTwips }
                    if ($null -ne $op.topTwips)  { $clone.Top  = [int]$op.topTwips }
                    Sync-VibeyEndpoints -Object $clone
                    if ((ConvertFrom-VibeyObjectKind $clone.Kind) -in 'Line','Box') { $clone.EndSectionName = $dest.Name }
                    $ctl = $doc.ReportDefController.ReportObjectController
                    [void]$ctl.Remove($f.Object)
                    [void]$ctl.Add($clone, $dest, -1)
                }
                'setAlignment' {
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    $clone.Format.HorizontalAlignment = switch ([string]$op.alignment) {
                        'Left' { 1 } 'Centre' { 2 } 'Center' { 2 } 'Right' { 3 } 'Justified' { 4 }
                    }
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
                }
                { $_ -in 'setFont','setFontSize','setBold' } {
                    <#
                        MEASURED: the font is ISCRFont with a DECIMAL Size, reached through
                        FontColor.Font -- confirmed. NOT measured (and wrong) was the brief's
                        assumption that plain dot-notation ($font.Size = ...) sets and reads it.
                        Dot-notation SET on this object silently no-ops (no exception, value
                        just never sticks, even read back one line later in the same variable);
                        dot-notation GET likewise always returns $null, even for a value that
                        WAS actually written (verified via a second, InvokeMember-based read).
                        reflection's InvokeMember against the object's runtime type works for
                        both directions and survives Modify -> SaveAs -> reopen. See
                        Set-VibeyComProperty. No reassignment of FontColor.Font back onto the
                        clone is needed -- mutating the Font object in place is retained by the
                        same clone (reference identity confirmed with
                        [object]::ReferenceEquals across a re-fetch).
                    #>
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    $font = $clone.FontColor.Font
                    if ($op.action -eq 'setFont')     { Set-VibeyComProperty -ComObject $font -Name 'Name' -Value ([string]$op.fontName) }
                    if ($op.action -eq 'setFontSize') { Set-VibeyComProperty -ComObject $font -Name 'Size' -Value ([decimal]$op.fontSizePt) }
                    if ($op.action -eq 'setBold')     { Set-VibeyComProperty -ComObject $font -Name 'Bold' -Value ([bool]$op.bold) }
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
                }
                { $_ -in 'addText','addLine','addBox','addField','addSpecialField' } {
                    <#
                        MEASURED: ReportObjectController has no CreateReportObject method at
                        all -- the brief's guessed `CreateReportObject($kindEnum)` call and its
                        kind integers do not exist on the live object. Printing
                        `$doc.ReportDefController.ReportObjectController | Get-Member` shows
                        only Add(ISCRReportObject, ISCRSection, int), AddByName, Remove, Modify,
                        GetAllReportObjects, GetReportObjectsByKind, ImportPicture. Objects are
                        instead constructed directly as the concrete ReportDefModel classes
                        (TextObjectClass, LineObjectClass, BoxObjectClass, FieldObjectClass) and
                        handed straight to Add(obj, section, -1) -- the same shape the C#
                        LayoutApplier.cs reference uses.
                    #>
                    $sec = Find-VibeySection -Doc $doc -Name $op.section
                    switch ([string]$op.action) {
                        'addText' {
                            <#
                                MEASURED: TextObjectClass.Text is a COM ReadOnly property --
                                assigning it directly (the brief's `$new.Text = ...`) throws
                                "'Text' is a ReadOnly property" immediately. The real text lives
                                in a Paragraph -> ParagraphElement tree instead.

                                MEASURED, the expensive way (a genuine ~30s hang, killed and
                                diagnosed rather than waited out, per the task's guidance):
                                PowerShell's plain dot-notation `.Add($element)` /
                                `.Add($paragraph)` against the ParagraphElements / Paragraphs COM
                                collections never returns -- no exception, no timeout. A
                                Type.InvokeMember-based Add (the same idiom
                                Get/Set-VibeyComProperty use for the font trap) fails FAST
                                instead, with "Specified cast is not valid." -- a real, useful
                                clue. The fix that actually works: cast the element/paragraph to
                                its ISCRParagraphElement/ISCRParagraph interface BEFORE the plain
                                dot-notation .Add() call. Once PowerShell is holding a reference
                                typed as the interface, the identical .Add() call that hung
                                returns immediately. Round-tripped through save/reopen and read
                                back correctly.
                            #>
                            $new = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.TextObjectClass
                            $new.Name = [string]$op.newName
                            $new.Left = [int]$op.leftTwips ; $new.Top    = [int]$op.topTwips
                            $new.Width = [int]$op.widthTwips; $new.Height = [int]$op.heightTwips

                            # MEASURED (C# reference): a text object's persisted font is carried by its
                            # paragraph RUN, not by the object's own FontColor - set only the outer one and
                            # Add() discards it in favour of the run's unset default ("MS Shell Dlg"). So
                            # both get a font, each its own instance.
                            $new.FontColor = Get-VibeyDefaultFontColor -Doc $doc -SectionName $op.section

                            $para = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.ParagraphClass
                            $elem = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.ParagraphTextElementClass
                            $elem.Text = [string]$op.text
                            # MEASURED: plain assignment is the only form that works here. InvokeMember
                            # throws 'Specified cast is not valid', and casting the element to
                            # ISCRParagraphTextElement first hangs Crystal outright.
                            $elem.FontColor = Get-VibeyDefaultFontColor -Doc $doc -SectionName $op.section
                            $castElem = [CrystalDecisions.ReportAppServer.ReportDefModel.ISCRParagraphElement]$elem
                            $para.ParagraphElements.Add($castElem)
                            $castPara = [CrystalDecisions.ReportAppServer.ReportDefModel.ISCRParagraph]$para
                            $new.Paragraphs.Add($castPara)

                            $doc.ReportDefController.ReportObjectController.Add($new, $sec, -1)
                        }
                        'addLine' {
                            <#
                                MEASURED: a bare Line/Box (Left/Top/Width/Height only) adds
                                without error but reads back Width/Height = 0 -- Right/Bottom are
                                the object's real geometry and Width/Height are derived from
                                them, so both pairs must be set. Omitting EndSectionName makes
                                Add throw COMException "Report section not found."
                            #>
                            $new = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.LineObjectClass
                            $new.Name = [string]$op.newName
                            $new.Left = [int]$op.leftTwips ; $new.Top    = [int]$op.topTwips
                            $new.Width = [int]$op.widthTwips; $new.Height = [int]$op.heightTwips
                            $new.Right  = [int]$op.leftTwips + [int]$op.widthTwips
                            $new.Bottom = [int]$op.topTwips  + [int]$op.heightTwips
                            $new.LineThickness = 15
                            $new.LineStyle = 1   # crLineStyleSingle (reflected CrLineStyleEnum)
                            $new.EndSectionName = $sec.Name
                            $doc.ReportDefController.ReportObjectController.Add($new, $sec, -1)
                        }
                        'addBox' {
                            $new = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.BoxObjectClass
                            $new.Name = [string]$op.newName
                            $new.Left = [int]$op.leftTwips ; $new.Top    = [int]$op.topTwips
                            $new.Width = [int]$op.widthTwips; $new.Height = [int]$op.heightTwips
                            $new.Right  = [int]$op.leftTwips + [int]$op.widthTwips
                            $new.Bottom = [int]$op.topTwips  + [int]$op.heightTwips
                            $new.LineThickness = 15
                            $new.LineStyle = 1   # crLineStyleSingle
                            $new.EndSectionName = $sec.Name
                            $doc.ReportDefController.ReportObjectController.Add($new, $sec, -1)
                        }
                        'addField' {
                            <#
                                MEASURED (and per the task brief): a freshly constructed
                                FieldObjectClass throws COMException "The field value type is
                                not valid." unless FieldValueType is set explicitly.
                                DataDefController.FindFieldByFormulaForm resolves the field's
                                ISCRField -- including its .Type (CrFieldValueTypeEnum) -- with
                                no database contact.
                            #>
                            $f = $doc.DataDefController.FindFieldByFormulaForm([string]$op.fieldRef)
                            if (-not $f) { throw "Field `"$($op.fieldRef)`" was not found in the report's data source." }
                            $new = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.FieldObjectClass
                            $new.Name = [string]$op.newName
                            $new.Left = [int]$op.leftTwips ; $new.Top    = [int]$op.topTwips
                            $new.Width = [int]$op.widthTwips; $new.Height = [int]$op.heightTwips
                            $new.DataSource = [string]$op.fieldRef
                            $new.FieldValueType = $f.Type
                            $new.FontColor = Get-VibeyDefaultFontColor -Doc $doc -SectionName $op.section
                            $doc.ReportDefController.ReportObjectController.Add($new, $sec, -1)
                        }
                        'addSpecialField' {
                            <#
                                MEASURED: the DataDefModel coclass is "SpecialFieldClass", not
                                "SpecialField" as the brief guessed -- New-Object with the bare
                                name fails to resolve a type. CrSpecialFieldTypeEnum's real
                                integers are ALL one lower than the brief's guessed table
                                (pageNumber=9 not 10, pageNOfM=19 not 20, totalPageCount=11 not
                                12, printDate=2 not 3, printTime=3 not 4, reportTitle=12 not 13,
                                recordNumber=8 not 9) -- reflected directly off the installed
                                CrSpecialFieldTypeEnum and cross-checked by reading back the
                                FormulaForm each value produces. Using the brief's numbers would
                                not have thrown; it would have silently bound the WRONG special
                                field (e.g. requesting pageNOfM's guessed 20 actually resolves to
                                crSpecialFieldTypeReportPath). A bare SpecialFieldClass with only
                                SpecialType set derives FormulaForm/Type/Length on its own, with
                                no report or database contact.
                            #>
                            $special = New-Object CrystalDecisions.ReportAppServer.DataDefModel.SpecialFieldClass
                            $special.SpecialType = switch ([string]$op.specialType) {
                                'pageNumber'     { 9 }  'pageNOfM'   { 19 } 'totalPageCount' { 11 }
                                'printDate'      { 2 }  'printTime'  { 3 }  'reportTitle'    { 12 }
                                'recordNumber'   { 8 }
                            }
                            $new = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.FieldObjectClass
                            $new.Name = [string]$op.newName
                            $new.Left = [int]$op.leftTwips ; $new.Top    = [int]$op.topTwips
                            $new.Width = [int]$op.widthTwips; $new.Height = [int]$op.heightTwips
                            $new.DataSource = $special.FormulaForm
                            $new.FieldValueType = $special.Type
                            $new.FontColor = Get-VibeyDefaultFontColor -Doc $doc -SectionName $op.section
                            $doc.ReportDefController.ReportObjectController.Add($new, $sec, -1)
                        }
                    }
                }
                'removeObject' {
                    <#
                        MEASURED: unlike move/resize/setFont's clone-mutate-Modify dance,
                        Remove takes the live object directly -- no clone needed.
                    #>
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $doc.ReportDefController.ReportObjectController.Remove($f.Object)
                    $removedObjects += [string]$op.target
                }
                { $_ -in 'setTextColor','setFillColor','setLineColor' } {
                    <#
                        FillColor/LineColor are plain top-level properties on the clone (same
                        idiom as Left/Top/Width/Height elsewhere in this file) and dot-notation
                        works fine there. FontColor.Color is a nested COM object property one
                        level in, the same shape as the measured FontColor.Font quirk, so it is
                        written through Set-VibeyComProperty rather than trusted to dot notation.
                    #>
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    $colorRef = ConvertTo-VibeyColorRef -Hex ([string]$op.color)
                    switch ([string]$op.action) {
                        'setTextColor' { Set-VibeyComProperty -ComObject $clone.FontColor -Name 'Color' -Value $colorRef }
                        'setFillColor' { $clone.FillColor = $colorRef }
                        'setLineColor' { $clone.LineColor = $colorRef }
                    }
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
                }
                'setLineThickness' {
                    # A top-level property on Box and Line, like LineColor, so dot notation holds.
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    $clone.LineThickness = [int]$op.lineThicknessTwips
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
                }
                'setBorder' {
                    <#
                        The border hangs off the report object and is mutated in place on the clone,
                        then pushed back with Modify - the same shape the number format and the font
                        take. It is a nested COM object, so it is written through
                        Set-VibeyComProperty.

                        Each side is written only when supplied, so adding a bottom rule cannot clear
                        an existing left divider. The validator guarantees at least one side or the
                        colour, and has already refused a double line on a Line/Box and an off-axis
                        side on a Line - both measured to misbehave rather than fail cleanly.
                    #>
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    $border = $clone.Border
                    if ($null -eq $border) { throw "Object `"$($op.target)`" has no border object, so `"setBorder`" cannot be applied to it." }
                    if ($op.left)   { Set-VibeyComProperty -ComObject $border -Name 'LeftLineStyle'   -Value (ConvertTo-VibeyLineStyle $op.left) }
                    if ($op.right)  { Set-VibeyComProperty -ComObject $border -Name 'RightLineStyle'  -Value (ConvertTo-VibeyLineStyle $op.right) }
                    if ($op.top)    { Set-VibeyComProperty -ComObject $border -Name 'TopLineStyle'    -Value (ConvertTo-VibeyLineStyle $op.top) }
                    if ($op.bottom) { Set-VibeyComProperty -ComObject $border -Name 'BottomLineStyle' -Value (ConvertTo-VibeyLineStyle $op.bottom) }
                    if ($null -ne $op.color) { Set-VibeyComProperty -ComObject $border -Name 'BorderColor' -Value (ConvertTo-VibeyColorRef -Hex ([string]$op.color)) }
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
                }
                { $_ -in 'setSectionBackground','setSectionBreak','setSuppress' -and $op.section } {
                    <#
                        MEASURED: ReportSectionController has no Modify method (confirmed in
                        Task 4 -- resizeSection above is the working reference). Section-level
                        format changes go through the same SetProperty(section,
                        CrReportSectionPropertyEnum, value) call resizeSection uses, but with the
                        Format member (=1) instead of Height (=2): clone section.Format, mutate
                        the clone, push it back with SetProperty(section, 1, clone). Assigning to
                        section.Format in place does not persist, the same reason report objects
                        go through Clone/Modify rather than in-place mutation.

                        section.Format's own properties (BackgroundColor, EnableSuppress, ...)
                        are written through Set-VibeyComProperty rather than dot notation --
                        ISCRSectionFormat is a nested COM object one level in from the section,
                        the same shape as the measured FontColor.Font/FieldFormat quirks.
                    #>
                    $sec = Find-VibeySection -Doc $doc -Name $op.section
                    $clone = $sec.Format.Clone($true)
                    switch ([string]$op.action) {
                        'setSectionBackground' {
                            Set-VibeyComProperty -ComObject $clone -Name 'BackgroundColor' -Value (ConvertTo-VibeyColorRef -Hex ([string]$op.color))
                        }
                        'setSectionBreak' {
                            if ($null -ne $op.newPageBefore) { Set-VibeyComProperty -ComObject $clone -Name 'EnableNewPageBefore' -Value ([bool]$op.newPageBefore) }
                            if ($null -ne $op.newPageAfter)  { Set-VibeyComProperty -ComObject $clone -Name 'EnableNewPageAfter'  -Value ([bool]$op.newPageAfter) }
                        }
                        'setSuppress' {
                            Set-VibeyComProperty -ComObject $clone -Name 'EnableSuppress' -Value ([bool]$op.suppress)
                            if ($null -ne $op.suppressIfBlank) { Set-VibeyComProperty -ComObject $clone -Name 'EnableSuppressIfBlank' -Value ([bool]$op.suppressIfBlank) }
                        }
                    }
                    $doc.ReportDefController.ReportSectionController.SetProperty($sec, 1, $clone)
                }
                { $_ -in 'setCanGrow','setSuppress' -and $op.target } {
                    <#
                        ISCRObjectFormat (EnableCanGrow, EnableSuppress) is reached through
                        clone.Format -- a nested COM object one level in, same shape as the
                        measured FontColor.Font/FieldFormat quirks -- so written through
                        Set-VibeyComProperty rather than dot notation.
                    #>
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    if ($op.action -eq 'setCanGrow') {
                        Set-VibeyComProperty -ComObject $clone.Format -Name 'EnableCanGrow' -Value ([bool]$op.canGrow)
                    } else {
                        Set-VibeyComProperty -ComObject $clone.Format -Name 'EnableSuppress' -Value ([bool]$op.suppress)
                    }
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
                }
                'setNumberFormat' {
                    <#
                        MEASURED, and the reason a first attempt at this silently did nothing:
                        while FieldFormat.CommonFormat.EnableSystemDefault is true -- and it is
                        true on every field of every fixture -- Crystal formats the field from
                        locale defaults and DISCARDS NDecimalPlaces/ThousandsSeparator on save.
                        The apply still returns ok and nothing changes. Clearing the gate first
                        is what makes the explicit format persist.

                        EnableSuppressIfZero persists even with the gate left on, which is
                        exactly how a half-working operation hides behind a test that only checks
                        that one property -- so this always clears the gate regardless of which
                        of the three the caller supplied.

                        RoundingFormat rounds independently of NDecimalPlaces (measured:
                        RoundingFormat = 11 - decimalPlaces across the validator's 0-10 range) --
                        left out of step, the value is rounded to a different precision than it
                        is displayed at.

                        FieldFormat/CommonFormat/NumericFormat are all nested COM objects, the
                        same shape as the measured FontColor.Font quirk, so every write here goes
                        through Set-VibeyComProperty rather than dot notation. No reassignment of
                        FieldFormat back onto the clone is needed -- mutating the nested object in
                        place is retained by the clone, same as the font case.

                        MEASURED on the PMS reports, and the part that went wrong silently: clearing
                        the gate makes Crystal use three STRINGS literally, and on a field built by
                        addField all three start EMPTY -
                          ZeroValueString ""  -> a zero prints as nothing at all
                          DecimalSymbol   ""  -> 41.00 prints as "4100"
                          ThousandSymbol  ""  -> grouping, when asked for, has no separator
                        Each is filled in only when empty, so a deliberate value survives.
                        ZeroValueString stays empty when zeros are being suppressed on purpose, and
                        is written at the decimal places asked for ("0.00" for two). The separators
                        are the invariant "." and "," so every machine produces the same report.
                    #>
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    $fmt = $clone.FieldFormat
                    $nf  = $fmt.NumericFormat
                    Set-VibeyComProperty -ComObject $fmt.CommonFormat -Name 'EnableSystemDefault' -Value $false
                    if ($null -ne $op.decimalPlaces) {
                        Set-VibeyComProperty -ComObject $nf -Name 'NDecimalPlaces' -Value ([int]$op.decimalPlaces)
                        Set-VibeyComProperty -ComObject $nf -Name 'RoundingFormat' -Value (11 - [int]$op.decimalPlaces)
                    }
                    if ($null -ne $op.thousandsSeparator) { Set-VibeyComProperty -ComObject $nf -Name 'ThousandsSeparator'   -Value ([bool]$op.thousandsSeparator) }
                    if ($null -ne $op.suppressIfZero)     { Set-VibeyComProperty -ComObject $nf -Name 'EnableSuppressIfZero' -Value ([bool]$op.suppressIfZero) }

                    $inv = [Globalization.CultureInfo]::InvariantCulture
                    $suppressing = [bool](Get-VibeyComProperty -ComObject $nf -Name 'EnableSuppressIfZero')
                    if (-not $suppressing -and [string]::IsNullOrEmpty([string](Get-VibeyComProperty -ComObject $nf -Name 'ZeroValueString'))) {
                        $places = if ($null -ne $op.decimalPlaces) { [int]$op.decimalPlaces } else { [int](Get-VibeyComProperty -ComObject $nf -Name 'NDecimalPlaces') }
                        if ($places -lt 0) { $places = 0 }
                        Set-VibeyComProperty -ComObject $nf -Name 'ZeroValueString' -Value ((0).ToString("F$places", $inv))
                    }
                    if ([string]::IsNullOrEmpty([string](Get-VibeyComProperty -ComObject $nf -Name 'DecimalSymbol'))) {
                        Set-VibeyComProperty -ComObject $nf -Name 'DecimalSymbol' -Value $inv.NumberFormat.NumberDecimalSeparator
                    }
                    if ([string]::IsNullOrEmpty([string](Get-VibeyComProperty -ComObject $nf -Name 'ThousandSymbol'))) {
                        Set-VibeyComProperty -ComObject $nf -Name 'ThousandSymbol' -Value $inv.NumberFormat.NumberGroupSeparator
                    }
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
                }
                { $_ -in 'addGroup','addSort' } {
                    <#
                        The field is resolved through FindFieldByFormulaForm, which needs no database
                        - deliberately NOT a walk of DatabaseController tables, the one call measured
                        to BLOCK rather than fail when the database is unreachable.
                    #>
                    $field = $doc.DataDefController.FindFieldByFormulaForm([string]$op.fieldRef)
                    if (-not $field) {
                        throw "`"$($op.action)`" could not resolve the field `"$($op.fieldRef)`" in this report. Use one of the formulaForm values read lists under availableFields."
                    }
                    # MEASURED: assigning the field with dot notation fails - PowerShell cannot convert
                    # the __ComObject FindFieldByFormulaForm returns to ISCRField. InvokeMember hands it
                    # to the setter untouched, and works. Both Add calls RETURN an index, which is
                    # voided: an uncaptured value leaks into this function's output and corrupts the
                    # JSON response.
                    if ($op.action -eq 'addGroup') {
                        $group = New-Object CrystalDecisions.ReportAppServer.DataDefModel.GroupClass
                        Set-VibeyComProperty -ComObject $group -Name 'ConditionField' -Value $field
                        # -1 appends. The index is 0-based and outermost-first (measured: inserting at 0
                        # puts the new header ABOVE the existing one and its footer BELOW).
                        $index = if ($null -ne $op.groupIndex) { [int]$op.groupIndex } else { -1 }
                        try { [void]$doc.DataDefController.GroupController.Add($index, $group) }
                        catch { throw "Crystal rejected `"addGroup`" on `"$($op.fieldRef)`": $($_.Exception.Message)" }

                        # Measured: a group has NO direction of its own - adding one creates a sort on
                        # its field (ascending), or adopts an existing sort and keeps its direction. So a
                        # direction is applied to that sort, and only when asked for: an addGroup with no
                        # direction must not flip an order the report already had.
                        if ($op.direction) {
                            $target = $null
                            foreach ($s in $doc.DataDefController.DataDefinition.Sorts) {
                                if ($s.SortField -and [string]::Equals([string]$s.SortField.FormulaForm, [string]$op.fieldRef, [StringComparison]::OrdinalIgnoreCase)) { $target = $s; break }
                            }
                            if ($null -eq $target) {
                                throw "`"addGroup`" added a group on `"$($op.fieldRef)`" but Crystal created no sort for it, so the requested direction could not be applied."
                            }
                            [void]$doc.DataDefController.SortController.ModifySortDirection($target, (ConvertTo-VibeySortDirection $op.direction))
                        }
                    } else {
                        $sort = New-Object CrystalDecisions.ReportAppServer.DataDefModel.SortClass
                        Set-VibeyComProperty -ComObject $sort -Name 'SortField' -Value $field
                        Set-VibeyComProperty -ComObject $sort -Name 'Direction' -Value (ConvertTo-VibeySortDirection $op.direction)
                        $index = if ($null -ne $op.sortIndex) { [int]$op.sortIndex } else { -1 }
                        try { [void]$doc.DataDefController.SortController.Add($index, $sort) }
                        catch { throw "Crystal rejected `"addSort`" on `"$($op.fieldRef)`": $($_.Exception.Message)" }
                    }
                }
                'addSubreport' {
                    <#
                        Imports an existing .rpt as a sub-report. The validator only checks the path's
                        SHAPE (it must stay pure), so existence is checked here, naming the path.

                        The newName becomes the sub-report's SubreportName; Crystal names the placed
                        container object itself ("Subreport1").
                    #>
                    $sec = Find-VibeySection -Doc $doc -Name $op.section
                    if (-not (Test-Path -LiteralPath ([string]$op.reportPath))) {
                        throw "Crystal rejected `"addSubreport`" for `"$($op.newName)`": sub-report file not found: `"$($op.reportPath)`"."
                    }
                    try {
                        $doc.SubreportController.ImportSubreportEx([string]$op.newName, [string]$op.reportPath, $sec,
                            [int]$op.leftTwips, [int]$op.topTwips, [int]$op.widthTwips, [int]$op.heightTwips) | Out-Null
                    } catch {
                        throw "Crystal rejected `"addSubreport`" for `"$($op.newName)`" in section `"$($sec.Name)`": $($_.Exception.Message)"
                    }
                }
                'setSubreportLink' {
                    <#
                        SetSubreportLinks REPLACES the whole link collection, so this reads the current
                        links, appends one, and writes the lot back - otherwise a second link would
                        silently discard the first.

                        A sub-report with no links yet may throw (or return null) on the read. Starting
                        from an empty collection is only correct when there was genuinely nothing to
                        keep, so whenever that fallback is used the result is re-read and must hold
                        exactly one link. Anything else means existing links may have been lost, and
                        that has to be a failure rather than an ok.

                        NEVER hand SetSubreportLinks an EMPTY collection: measured, Crystal XI R2 spins
                        at ~85% CPU forever and never returns. A link is always appended first, and the
                        count is checked before the call as a guard. To REMOVE links, drop the
                        sub-report and import it again.

                        MEASURED: the collection is used AS RETURNED - plain .Count, .Add and the indexer
                        all work, and Add does not hang. Casting to ISCRSubreportLinks, as the C# applier
                        does, fails in PowerShell ("Cannot convert the System.__ComObject value").

                        linkedParameter is optional: measured, Crystal discards what is written and uses
                        its own "{?Pm-<mainReportField>}".
                    #>
                    $name = Resolve-VibeySubreportName -Doc $doc -Requested ([string]$op.target)
                    $readFailed = $false
                    $links = $null
                    try {
                        $links = $doc.SubreportController.GetSubreportLinks($name)
                    } catch { $readFailed = $true }
                    if ($null -eq $links) {
                        $readFailed = $true
                        $links = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.SubreportLinksClass
                    }

                    $link = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.SubreportLinkClass
                    $link.MainReportFieldName = [string]$op.mainReportField
                    $link.SubreportFieldName  = [string]$op.subreportField
                    if ($op.linkedParameter) { $link.LinkedParameterName = [string]$op.linkedParameter }
                    [void]$links.Add($link)

                    if ($links.Count -lt 1) { throw "`"setSubreportLink`": refusing to write an empty link collection, which hangs Crystal XI R2." }
                    try { [void]$doc.SubreportController.SetSubreportLinks($name, $links) }
                    catch { throw "Crystal rejected `"setSubreportLink`" for `"$($op.target)`": $($_.Exception.Message)" }

                    if ($readFailed) {
                        $after = $null
                        try { $after = $doc.SubreportController.GetSubreportLinks($name) }
                        catch {
                            throw ("`"setSubreportLink`" for `"$($op.target)`": this sub-report's existing links could not be read " +
                                   "before the link was added, and cannot be read back afterwards to confirm none were " +
                                   "discarded (SetSubreportLinks replaces the whole collection): $($_.Exception.Message)")
                        }
                        $count = if ($null -eq $after) { -1 } else { [int]$after.Count }
                        if ($count -ne 1) {
                            throw ("`"setSubreportLink`" for `"$($op.target)`": this sub-report's existing links could not be read " +
                                   "before the link was added, so it was set from an empty collection, but $count links are " +
                                   "present afterwards - pre-existing links may have been discarded. The report was not saved.")
                        }
                    }
                }
                'removeTable' {
                    <#
                        Needs no database. MEASURED: Crystal does NOT refuse a table that fields are
                        still bound to - it removes it and leaves them unresolvable. The validator is
                        the only thing preventing that. Crystal DOES refuse a table something else
                        still refers to (a formula, record selection, a group or sort); that is
                        reported with the reason, since this skill cannot edit those.
                    #>
                    $t = Get-VibeyRequiredTable -Doc $doc -Alias ([string]$op.target) -Action 'removeTable'
                    $canonical = [string]$t.Alias
                    try { [void]$doc.DatabaseController.RemoveTable($canonical) }
                    catch {
                        throw ("Crystal rejected `"removeTable`" for `"$($op.target)`": $($_.Exception.Message) Crystal refuses " +
                               "to remove a table that something else in the report still refers to - most often a " +
                               "formula, a record-selection formula, a group or a sort. Those are outside what this skill " +
                               "can edit, so such a table has to be detached in the Crystal Designer.")
                    }
                    $removedTables += $canonical
                }
                'addTable' {
                    <#
                        Adds a table or stored procedure, CLONING the connection of one already in the
                        report so no server, database or user name is ever expressed in a plan. The
                        password comes from VIBEY_DB_PASSWORD, read before any COM call so "not set"
                        can never be mistaken for a server rejection, and set only on the clone.

                        Its fields come from the server, so the plan cannot bind to them: read the saved
                        report first. This is the ONLY way this skill can pick up a stored procedure's
                        new columns - the report's field list is cached, and setTableLocation does not
                        refresh it (measured).
                    #>
                    $source = Get-VibeyRequiredTable -Doc $doc -Alias ([string]$op.target) -Action 'addTable'
                    if (Find-VibeyTable -Doc $doc -Alias ([string]$op.newName)) {
                        throw "`"addTable`": a table with the alias `"$($op.newName)`" is already in this report's data source."
                    }
                    if ($null -eq $source.ConnectionInfo) {
                        throw "`"addTable`": the table `"$($op.target)`" has no saved connection to clone, so there is nothing to attach the new table to."
                    }
                    $dbPassword = Get-VibeyDbPassword -Action 'addTable'
                    $table = if (Test-VibeyIsProcedure $source) { New-Object CrystalDecisions.ReportAppServer.DataDefModel.ProcedureClass }
                             else { New-Object CrystalDecisions.ReportAppServer.DataDefModel.TableClass }
                    $conn = $source.ConnectionInfo.Clone($true)
                    $conn.Password = $dbPassword
                    $table.ConnectionInfo = $conn
                    $table.Name = [string]$op.tableName
                    $table.Alias = [string]$op.newName
                    $table.QualifiedName = Get-VibeyQualifiedName -Source $source -TableName ([string]$op.tableName)
                    try { [void]$doc.DatabaseController.AddTable($table, $null) }
                    catch {
                        $msg = Protect-VibeySecret $_.Exception.Message $dbPassword
                        throw ("Crystal rejected `"addTable`" for `"$($op.newName)`" ($($op.tableName)): $msg Adding a table makes " +
                               "Crystal connect to the database to verify it. The connection cloned from `"$($op.target)`" " +
                               "carries the report's saved server, database and user name ($(Get-VibeyUserDescription $source)), " +
                               "and the password came from $script:PasswordVariable, which IS set - so the server was reached and " +
                               "refused the logon, or refused the object. Check the variable holds that user's current password " +
                               "and that the named object exists and is visible to it.")
                    }
                }
                'setTableLocation' {
                    <#
                        Repoints a table at a different object on the SAME connection; the alias is kept,
                        since every object binds through it.

                        MEASURED LIMITS (docs\sdk-notes.md): this does NOT refresh the table's field
                        list - use addTable for new columns - and on a report that has sub-reports it
                        can fail at save with "Failed to save database information".
                    #>
                    $current = Get-VibeyRequiredTable -Doc $doc -Alias ([string]$op.target) -Action 'setTableLocation'
                    if ($null -eq $current.ConnectionInfo) {
                        throw "`"setTableLocation`": the table `"$($op.target)`" has no saved connection, so there is nothing to repoint it on."
                    }
                    $dbPassword = Get-VibeyDbPassword -Action 'setTableLocation'
                    $replacement = $current.Clone($true)
                    $replacement.Name = [string]$op.tableName
                    $replacement.QualifiedName = Get-VibeyQualifiedName -Source $current -TableName ([string]$op.tableName)
                    # The password goes on a fresh clone, so the table currently in the document is
                    # never given one - if the call throws, nothing is saved either.
                    $conn = $current.ConnectionInfo.Clone($true)
                    $conn.Password = $dbPassword
                    $replacement.ConnectionInfo = $conn
                    try { [void]$doc.DatabaseController.SetTableLocation($current, $replacement) }
                    catch {
                        $msg = Protect-VibeySecret $_.Exception.Message $dbPassword
                        throw ("Crystal rejected `"setTableLocation`" for `"$($op.target)`" -> `"$($op.tableName)`": $msg " +
                               "Repointing a table makes Crystal connect to the database to verify the new object, using the " +
                               "report's saved connection ($(Get-VibeyUserDescription $current)) with the password from " +
                               "$script:PasswordVariable, which IS set.")
                    }
                }
                default {
                    throw "`"$($op.action)`" passed validation but has no applier arm yet (operation $n)."
                }
            }
        }
        Save-VibeyDocument -Doc $doc -OutputPath $Request.outputPath
    } finally { $rd.Close() }

    return @{ ok = $true; operationsApplied = @($Request.plan.operations).Count
              removedObjects = $removedObjects
              removedTables  = $removedTables
              schema = (Get-VibeySchema -Path $Request.outputPath) }
}

Export-ModuleMember -Function Import-VibeyCrystal, Open-VibeyDocument, Get-VibeySectionList, Get-VibeySchema, Get-VibeyObjectInfo, Invoke-VibeyRead, Invoke-VibeyApply, Save-VibeyDocument, Find-VibeyObject, Find-VibeySection, Set-VibeyObject, Get-VibeyComProperty, Set-VibeyComProperty, ConvertTo-VibeyColorRef, ConvertFrom-VibeyColorRef, New-VibeyDefaultFontColor
