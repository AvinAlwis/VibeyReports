# VibeyCrystal.psm1

$script:Utf8NoBom = New-Object System.Text.UTF8Encoding $false

function Open-VibeyDocument {
    <#
        MEASURED: "New-Object CrystalDecisions.ReportAppServer.ClientDoc.ReportClientDocument"
        HANGS - it opens a TCP connection to port 1566 on the local machine that never
        completes. Loading through ReportDocument and taking .ReportClientDocument gives the
        same object model in about 2ms with no network activity. Do not "simplify" this.
    #>
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { throw "Report not found: $Path" }
    [Reflection.Assembly]::LoadWithPartialName('CrystalDecisions.CrystalReports.Engine') | Out-Null
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
        foreach ($sec in $area.Sections) {
            $list += [pscustomobject]@{
                Name = $sec.Name; Kind = (ConvertFrom-VibeyAreaKind $area.Kind); Section = $sec
            }
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

function Get-VibeyObjectInfo {
    param([Parameter(Mandatory)]$ReportObject)
    $o = $ReportObject
    $info = [ordered]@{
        name = $o.Name; kind = (ConvertFrom-VibeyObjectKind $o.Kind)
        leftTwips = [int]$o.Left; topTwips = [int]$o.Top
        widthTwips = [int]$o.Width; heightTwips = [int]$o.Height
        text = $null; dataSource = $null
        fontName = $null; fontSizePt = $null; bold = $null
    }
    # MEASURED: fonts are reached through FontColor.Font. On SampleReport.rpt's own objects,
    # none carry an explicitly-set font (they inherit the report's default), so Name/Size/Bold
    # come back as null there -- that is a legitimate "not explicitly set" reading, not a
    # broken accessor, which is why this is left as null rather than defaulted to a guess.
    $fc = $null
    try { $fc = $o.FontColor } catch { }
    if ($fc -and $fc.Font) {
        if ($null -ne $fc.Font.Name)  { $info.fontName   = $fc.Font.Name }
        if ($null -ne $fc.Font.Size)  { $info.fontSizePt = [double]$fc.Font.Size }
        if ($null -ne $fc.Font.Bold)  { $info.bold       = [bool]$fc.Font.Bold }
    }
    try { if ($o.Text)       { $info.text       = $o.Text } }       catch { }
    try { if ($o.DataSource) { $info.dataSource = $o.DataSource } } catch { }
    return $info
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
        foreach ($entry in (Get-VibeySectionList -Doc $doc)) {
            $objects = @()
            foreach ($ro in $entry.Section.ReportObjects) { $objects += (Get-VibeyObjectInfo -ReportObject $ro) }
            $sections += [ordered]@{
                name = $entry.Name; kind = $entry.Kind
                heightTwips = [int]$entry.Section.Height; objects = $objects
            }
        }
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
        }
    } finally { $rd.Close() }
}

function Invoke-VibeyRead {
    param([Parameter(Mandatory)]$Request)
    if (-not $Request.reportPath) { return @{ ok = $false; error = '"read" requires "reportPath".' } }
    return @{ ok = $true; schema = (Get-VibeySchema -Path $Request.reportPath) }
}

function Invoke-VibeyApply { param([Parameter(Mandatory)]$Request) @{ ok = $false; error = 'apply not implemented' } }

Export-ModuleMember -Function Open-VibeyDocument, Get-VibeySectionList, Get-VibeySchema, Get-VibeyObjectInfo, Invoke-VibeyRead, Invoke-VibeyApply
