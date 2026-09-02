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
