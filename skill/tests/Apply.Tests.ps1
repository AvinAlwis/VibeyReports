$entry   = Join-Path $PSScriptRoot '..\vibey-reports\scripts\vibey.ps1'
$fixture = (Resolve-Path (Join-Path $PSScriptRoot '..\..\tests\fixtures\SampleReport.rpt')).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) 'vibey-tests'
if (-not (Test-Path $scratch)) { New-Item -ItemType Directory -Path $scratch | Out-Null }

function Invoke-Vibey($Request) {
    ($Request | ConvertTo-Json -Depth 24 -Compress |
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $entry) -join '' | ConvertFrom-Json
}
function Apply-Ops($Ops, [string]$OutName) {
    $out = Join-Path $scratch $OutName
    if (Test-Path $out) { Remove-Item $out -Force }
    $r = Invoke-Vibey @{ command='apply'; reportPath=$fixture; outputPath=$out; overwrite=$true
                         plan = @{ planVersion=1; operations=@($Ops) } }
    return @{ Result = $r; Output = $out }
}
function First-FontableObject($Schema) {
    foreach ($s in $Schema.sections) { foreach ($o in $s.objects) { if ($o.kind -in 'Field','Text','FieldHeading') { return $o } } }
    throw 'fixture has no fontable object'
}

It 'moves an object and the change survives a save and reopen' {
    $before = (Invoke-Vibey @{ command='read'; reportPath=$fixture }).schema
    $t = First-FontableObject $before
    $a = Apply-Ops @{ action='move'; target=$t.name; leftTwips=($t.leftTwips + 120); topTwips=$t.topTwips } 'move.rpt'
    Should-Be $a.Result.ok $true 'ok'
    $after = (Invoke-Vibey @{ command='read'; reportPath=$a.Output }).schema
    $moved = $after.sections | ForEach-Object { $_.objects } | Where-Object { $_.name -eq $t.name }
    Should-Be $moved.leftTwips ($t.leftTwips + 120) 'left'
}

It 'leaves the source report byte-identical' {
    $hashBefore = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
    $before = (Invoke-Vibey @{ command='read'; reportPath=$fixture }).schema
    $t = First-FontableObject $before
    Apply-Ops @{ action='move'; target=$t.name; leftTwips=($t.leftTwips + 40); topTwips=$t.topTwips } 'src.rpt' | Out-Null
    Should-Be (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash $hashBefore 'source hash'
}

It 'writes no output file when the plan fails validation' {
    $out = Join-Path $scratch 'never.rpt'
    if (Test-Path $out) { Remove-Item $out -Force }
    $r = Invoke-Vibey @{ command='apply'; reportPath=$fixture; outputPath=$out; overwrite=$true
                         plan = @{ planVersion=1; operations=@(@{ action='move'; target='NoSuchObject'; leftTwips=1; topTwips=1 }) } }
    Should-Be $r.ok $false 'ok'
    if (Test-Path $out) { throw 'an output file was written for a rejected plan' }
}

It 'applies nothing at all when one operation in a plan is invalid' {
    $before = (Invoke-Vibey @{ command='read'; reportPath=$fixture }).schema
    $t = First-FontableObject $before
    $out = Join-Path $scratch 'atomic.rpt'
    if (Test-Path $out) { Remove-Item $out -Force }
    $r = Invoke-Vibey @{ command='apply'; reportPath=$fixture; outputPath=$out; overwrite=$true
                         plan = @{ planVersion=1; operations=@(
                            @{ action='move'; target=$t.name; leftTwips=999; topTwips=999 },
                            @{ action='move'; target='NoSuchObject'; leftTwips=1; topTwips=1 }) } }
    Should-Be $r.ok $false 'ok'
    if (Test-Path $out) { throw 'a partially applied report was written' }
}

It 'sets a font size and it survives a save and reopen' {
    $before = (Invoke-Vibey @{ command='read'; reportPath=$fixture }).schema
    $t = First-FontableObject $before
    $a = Apply-Ops @{ action='setFontSize'; target=$t.name; fontSizePt=14 } 'font.rpt'
    Should-Be $a.Result.ok $true 'ok'
    $after = (Invoke-Vibey @{ command='read'; reportPath=$a.Output }).schema
    $o = $after.sections | ForEach-Object { $_.objects } | Where-Object { $_.name -eq $t.name }
    Should-Be ([int]$o.fontSizePt) 14 'font size'
}

It 'resizes a section and reports the new height' {
    $a = Apply-Ops @{ action='resizeSection'; section='DetailSection1'; heightTwips=1200 } 'section.rpt'
    Should-Be $a.Result.ok $true 'ok'
    $after = (Invoke-Vibey @{ command='read'; reportPath=$a.Output }).schema
    $s = $after.sections | Where-Object { $_.name -eq 'DetailSection1' }
    Should-Be $s.heightTwips 1200 'section height'
}

It 'adds a text object that reads back with its text and geometry' {
    $a = Apply-Ops @(
        @{ action='resizeSection'; section='DetailSection1'; heightTwips=1000 },
        @{ action='addText'; section='DetailSection1'; newName='VibeyCaption'; text='Hello'
           leftTwips=100; topTwips=100; widthTwips=3000; heightTwips=240 },
        @{ action='setFontSize'; target='VibeyCaption'; fontSizePt=9 }) 'addtext.rpt'
    Should-Be $a.Result.ok $true 'ok'
    $after = (Invoke-Vibey @{ command='read'; reportPath=$a.Output }).schema
    $o = $after.sections | ForEach-Object { $_.objects } | Where-Object { $_.name -eq 'VibeyCaption' }
    Should-Be $o.text 'Hello' 'text'
    Should-Be $o.widthTwips 3000 'width'
}

It 'adds a horizontal line' {
    $a = Apply-Ops @(
        @{ action='resizeSection'; section='DetailSection1'; heightTwips=1000 },
        @{ action='addLine'; section='DetailSection1'; newName='VibeyRule'
           leftTwips=0; topTwips=500; widthTwips=5000; heightTwips=0 }) 'addline.rpt'
    Should-Be $a.Result.ok $true 'ok'
    $after = (Invoke-Vibey @{ command='read'; reportPath=$a.Output }).schema
    $o = $after.sections | ForEach-Object { $_.objects } | Where-Object { $_.name -eq 'VibeyRule' }
    Should-Be $o.kind 'Line' 'kind'
    # A bare Line adds without error but reads back width/height = 0 unless Right/Bottom are
    # also set (docs/sdk-notes.md, "A bare Line or Box adds cleanly but reads back width = 0");
    # this asserts the fix that closes that gap actually persists, not just that Add succeeded.
    Should-Be $o.leftTwips 0 'left'
    Should-Be $o.topTwips 500 'top'
    Should-Be $o.widthTwips 5000 'width'
    Should-Be $o.heightTwips 0 'height'
}

It 'adds a box with geometry that survives save and reopen' {
    $a = Apply-Ops @(
        @{ action='resizeSection'; section='DetailSection1'; heightTwips=1000 },
        @{ action='addBox'; section='DetailSection1'; newName='VibeyBox'
           leftTwips=100; topTwips=200; widthTwips=1500; heightTwips=600 }) 'addbox.rpt'
    Should-Be $a.Result.ok $true 'ok'
    $after = (Invoke-Vibey @{ command='read'; reportPath=$a.Output }).schema
    $o = $after.sections | ForEach-Object { $_.objects } | Where-Object { $_.name -eq 'VibeyBox' }
    Should-Be $o.kind 'Box' 'kind'
    Should-Be $o.leftTwips 100 'left'
    Should-Be $o.topTwips 200 'top'
    Should-Be $o.widthTwips 1500 'width'
    Should-Be $o.heightTwips 600 'height'
}

It 'adds a page number special field as a Field object' {
    $a = Apply-Ops @(
        @{ action='resizeSection'; section='PageFooterSection1'; heightTwips=900 },
        @{ action='addSpecialField'; section='PageFooterSection1'; newName='VibeyPage'
           specialType='pageNOfM'; leftTwips=100; topTwips=80; widthTwips=2000; heightTwips=220 },
        @{ action='setFontSize'; target='VibeyPage'; fontSizePt=8 }) 'special.rpt'
    Should-Be $a.Result.ok $true 'ok'
    $after = (Invoke-Vibey @{ command='read'; reportPath=$a.Output }).schema
    $o = $after.sections | ForEach-Object { $_.objects } | Where-Object { $_.name -eq 'VibeyPage' }
    Should-Be $o.kind 'Field' 'kind'
}

It 'binds every supported specialType to its correct FormulaForm, not just pageNOfM' {
    # The task-5 review flagged that only pageNOfM had been cross-checked against the SDK's
    # real FormulaForm string, and the brief's guessed CrSpecialFieldTypeEnum integers were
    # ALL wrong but still legal enum members -- so a wrong-but-legal mapping would silently
    # bind the wrong field and produce a report that renders perfectly with the wrong data in
    # it. This proves all seven, not one, so a future refactor that flips two entries in the
    # switch gets caught here instead of shipping quietly.
    $expected = [ordered]@{
        pageNumber     = 'PageNumber'
        pageNOfM       = 'PageNofM'
        totalPageCount = 'TotalPageCount'
        printDate      = 'PrintDate'
        printTime      = 'PrintTime'
        reportTitle    = 'ReportTitle'
        recordNumber   = 'RecordNumber'
    }
    $ops = @(@{ action='resizeSection'; section='DetailSection1'; heightTwips=2200 })
    $top = 0
    foreach ($type in $expected.Keys) {
        $ops += @{ action='addSpecialField'; section='DetailSection1'; newName="Vibey$type"
                   specialType=$type; leftTwips=0; topTwips=$top; widthTwips=2000; heightTwips=220 }
        $top += 300
    }
    $a = Apply-Ops $ops 'specialfields.rpt'
    Should-Be $a.Result.ok $true 'ok'
    $after = (Invoke-Vibey @{ command='read'; reportPath=$a.Output }).schema
    foreach ($type in $expected.Keys) {
        $o = $after.sections | ForEach-Object { $_.objects } | Where-Object { $_.name -eq "Vibey$type" }
        Should-Be $o.dataSource $expected[$type] "dataSource for specialType '$type'"
    }
}

It 'removes an object and reports what it removed' {
    $before = (Invoke-Vibey @{ command='read'; reportPath=$fixture }).schema
    $t = First-FontableObject $before
    $a = Apply-Ops @{ action='removeObject'; target=$t.name } 'remove.rpt'
    Should-Be $a.Result.ok $true 'ok'
    Should-Be $a.Result.removedObjects[0] $t.name 'removedObjects'
    $after = (Invoke-Vibey @{ command='read'; reportPath=$a.Output }).schema
    $still = $after.sections | ForEach-Object { $_.objects } | Where-Object { $_.name -eq $t.name }
    if ($still) { throw "object $($t.name) is still present after removeObject" }
}

It 'cleans up the temp file when the destination move fails' {
    $before = (Invoke-Vibey @{ command='read'; reportPath=$fixture }).schema
    $t = First-FontableObject $before
    $out = Join-Path $scratch 'locked.rpt'
    if (Test-Path $out) { Remove-Item $out -Force }
    # Create the destination and hold an exclusive handle on it so Move-Item's rename fails.
    $stream = [IO.File]::Open($out, [IO.FileMode]::Create, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try {
        $r = Invoke-Vibey @{ command='apply'; reportPath=$fixture; outputPath=$out; overwrite=$true
                             plan = @{ planVersion=1; operations=@(
                                @{ action='move'; target=$t.name; leftTwips=($t.leftTwips + 60); topTwips=$t.topTwips }) } }
        Should-Be $r.ok $false 'ok'
        $leaked = Get-ChildItem -LiteralPath $scratch -Filter 'vibey-*.rpt' -ErrorAction SilentlyContinue
        if ($leaked) { throw "temp file(s) leaked in the destination directory: $($leaked.Name -join ', ')" }
    } finally {
        $stream.Close()
        if (Test-Path $out) { Remove-Item $out -Force }
    }
}
