$entry   = Join-Path $PSScriptRoot '..\vibey-reports\scripts\vibey.ps1'
$fixture = (Resolve-Path (Join-Path $PSScriptRoot '..\..\tests\fixtures\SampleReport.rpt')).Path

function Read-Fixture {
    $req = @{ command = 'read'; reportPath = $fixture } | ConvertTo-Json -Compress
    ($req | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $entry) -join '' | ConvertFrom-Json
}

It 'reads a report and reports ok' {
    Should-Be (Read-Fixture).ok $true 'ok'
}

It 'reports A4 page geometry in twips' {
    $r = Read-Fixture
    Should-Be $r.schema.page.widthTwips  11906 'page width'
    Should-Be $r.schema.page.heightTwips 16838 'page height'
}

It 'reports the five standard sections with their kinds' {
    $r = Read-Fixture
    Should-Be $r.schema.sections.Count 5 'section count'
    Should-Be $r.schema.sections[0].kind 'ReportHeader' 'first section kind'
}

It 'reports objects with geometry' {
    $r = Read-Fixture
    $withObjects = @($r.schema.sections | Where-Object { $_.objects.Count -gt 0 })
    if ($withObjects.Count -eq 0) { throw 'no section reported any objects' }
    $o = $withObjects[0].objects[0]
    if ($null -eq $o.name -or $o.name -eq '') { throw 'object has no name' }
    if ($o.widthTwips -le 0) { throw "object $($o.name) has width $($o.widthTwips)" }
}
