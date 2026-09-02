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
        if ($null -ne $fontName) { $info.fontName   = $fontName }
        if ($null -ne $fontSize) { $info.fontSizePt = [double]$fontSize }
        if ($null -ne $fontBold) { $info.bold       = [bool]$fontBold }
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
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
                }
                'resize' {
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    $clone.Width = [int]$op.widthTwips; $clone.Height = [int]$op.heightTwips
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
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

                            $para = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.ParagraphClass
                            $elem = New-Object CrystalDecisions.ReportAppServer.ReportDefModel.ParagraphTextElementClass
                            $elem.Text = [string]$op.text
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
                            $new.FontColor = New-VibeyDefaultFontColor
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
                            $new.FontColor = New-VibeyDefaultFontColor
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
                default {
                    throw "`"$($op.action)`" passed validation but has no applier arm yet (operation $n)."
                }
            }
        }
        Save-VibeyDocument -Doc $doc -OutputPath $Request.outputPath
    } finally { $rd.Close() }

    return @{ ok = $true; operationsApplied = @($Request.plan.operations).Count
              removedObjects = $removedObjects
              schema = (Get-VibeySchema -Path $Request.outputPath) }
}

Export-ModuleMember -Function Open-VibeyDocument, Get-VibeySectionList, Get-VibeySchema, Get-VibeyObjectInfo, Invoke-VibeyRead, Invoke-VibeyApply, Save-VibeyDocument, Find-VibeyObject, Find-VibeySection, Set-VibeyObject, Get-VibeyComProperty, Set-VibeyComProperty
