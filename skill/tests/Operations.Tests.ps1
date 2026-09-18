# End-to-end tests for the operations ported from the C# worker: setBorder, setLineThickness,
# moveToSection, addGroup, addSort, addSubreport, setSubreportLink, addTable, removeTable,
# setTableLocation - plus the Line/Box endpoint sync and the number-format strings.
#
# Every assertion reads the SAVED file back in a fresh process. The apply response is not enough:
# Crystal changes some things only at save (a border grows its section; numeric formatting on a
# String field is discarded), and an in-memory read would report what was asked for, not what
# was kept.

$opsEntry    = Join-Path $PSScriptRoot '..\vibey-reports\scripts\vibey.ps1'
$opsFixtures = (Resolve-Path (Join-Path $PSScriptRoot '..\..\tests\fixtures')).Path
$opsSample   = Join-Path $opsFixtures 'SampleReport.rpt'
$opsHost     = Join-Path $opsFixtures 'PMSV10_IndPerfOverview.rpt'
$opsScratch  = Join-Path ([IO.Path]::GetTempPath()) 'vibey-tests'
if (-not (Test-Path $opsScratch)) { New-Item -ItemType Directory -Path $opsScratch | Out-Null }

function Invoke-OpsVibey($Request) {
    ($Request | ConvertTo-Json -Depth 24 -Compress |
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $opsEntry) -join '' | ConvertFrom-Json
}
function Invoke-OpsPlan([string]$Source, $Ops, [string]$OutName) {
    $out = Join-Path $opsScratch $OutName
    if (Test-Path $out) { Remove-Item $out -Force }
    $r = Invoke-OpsVibey @{ command='apply'; reportPath=$Source; outputPath=$out; overwrite=$true
                            plan = @{ planVersion=1; operations=@($Ops) } }
    $saved = if ($r.ok) { (Invoke-OpsVibey @{ command='read'; reportPath=$out }).schema } else { $null }
    return @{ Result = $r; Output = $out; Saved = $saved }
}
function Get-OpsObject($Schema, [string]$Name) {
    $Schema.sections | ForEach-Object { $_.objects } | Where-Object { $_.name -eq $Name } | Select-Object -First 1
}
function Get-OpsSection($Schema, [string]$Name) {
    $Schema.sections | Where-Object { $_.name -eq $Name } | Select-Object -First 1
}

It 'opens a report even with the Crystal 13 runtime installed alongside' {
    # Regression: loading the engine by partial name picked up 13.0.4000.0 and every command failed.
    $r = Invoke-OpsVibey @{ command='read'; reportPath=$opsSample }
    Should-Be $r.ok $true "ok (error: $($r.error))"
}

It 'reads the data-source fields, groups and sorts' {
    $s = (Invoke-OpsVibey @{ command='read'; reportPath=$opsSample }).schema
    Should-Be (@($s.availableFields).Count) 2 'availableFields count'
    Should-Be ($s.availableFields[0].formulaForm) '{Command.CardCode}' 'first formulaForm'
    Should-Be ($s.availableFields[0].tableAlias) 'Command' 'tableAlias'
    Should-Be ($s.availableFields[0].valueType) 'String' 'valueType'
    Should-Be (@($s.groups).Count) 0 'groups'
    Should-Be (@($s.sorts).Count) 0 'sorts'
}

It 'reports a sub-report by its subreportName, not only its object name' {
    $s = (Invoke-OpsVibey @{ command='read'; reportPath=$opsHost }).schema
    $sub = Get-OpsObject $s 'Subreport2'
    Should-Be $sub.kind 'Subreport' 'kind'
    Should-Be $sub.subreportName 'stage wise eval' 'subreportName'
}

It 'reads an unset fill as null, not as white' {
    # Regression: the "no colour" sentinel was compared to the literal 0xFFFFFFFF, which Windows
    # PowerShell 5.1 parses as -1, so it never matched.
    $s = (Invoke-OpsVibey @{ command='read'; reportPath=$opsHost }).schema
    Should-Be ((Get-OpsSection $s 'PageHeaderSection1').backgroundColorHex) $null 'unset section background'
    Should-Be ((Get-OpsSection $s 'DetailSection1').backgroundColorHex) '#FFFFFF' 'explicitly white section'
}

It 'sets four different border sides and a colour' {
    $a = Invoke-OpsPlan $opsSample @{ action='setBorder'; target='CardName1'; left='single'; right='dashed'
                                      top='dotted'; bottom='double'; color='#1F2A37' } 'border.rpt'
    Should-Be $a.Result.ok $true "ok ($($a.Result.error))"
    $b = (Get-OpsObject $a.Saved 'CardName1').border
    Should-Be $b.left 'single' 'left'; Should-Be $b.right 'dashed' 'right'
    Should-Be $b.top 'dotted' 'top';   Should-Be $b.bottom 'double' 'bottom'
    Should-Be $b.colorHex '#1F2A37' 'colour'
}

It 'sets only the bottom border and leaves the other sides alone' {
    $a = Invoke-OpsPlan $opsSample @{ action='setBorder'; target='CardCode1'; bottom='single' } 'border1.rpt'
    $b = (Get-OpsObject $a.Saved 'CardCode1').border
    Should-Be $b.bottom 'single' 'bottom'
    Should-Be $b.left 'none' 'left untouched'
}

It 'keeps a moved and resized box the size it was given (Right/Bottom follow)' {
    $a = Invoke-OpsPlan $opsSample @(
        @{ action='resizeSection'; section='DetailSection1'; heightTwips=1600 },
        @{ action='addBox'; section='DetailSection1'; newName='bx'; leftTwips=100; topTwips=400; widthTwips=2000; heightTwips=600 },
        @{ action='move'; target='bx'; leftTwips=300; topTwips=500 },
        @{ action='resize'; target='bx'; widthTwips=2500; heightTwips=700 }) 'movebox.rpt'
    Should-Be $a.Result.ok $true "ok ($($a.Result.error))"
    $o = Get-OpsObject $a.Saved 'bx'
    Should-Be $o.leftTwips 300 'left'; Should-Be $o.topTwips 500 'top'
    Should-Be $o.widthTwips 2500 'width'; Should-Be $o.heightTwips 700 'height'
}

It 'sets line thickness on a box and a line' {
    $a = Invoke-OpsPlan $opsSample @(
        @{ action='resizeSection'; section='DetailSection1'; heightTwips=1600 },
        @{ action='addBox'; section='DetailSection1'; newName='bt'; leftTwips=0; topTwips=300; widthTwips=1000; heightTwips=400 },
        @{ action='setLineThickness'; target='bt'; lineThicknessTwips=20 },
        @{ action='addLine'; section='DetailSection1'; newName='lt'; leftTwips=0; topTwips=1000; widthTwips=1000; heightTwips=0 },
        @{ action='setLineThickness'; target='lt'; lineThicknessTwips=10 }) 'thickness.rpt'
    Should-Be (Get-OpsObject $a.Saved 'bt').lineThicknessTwips 20 'box'
    Should-Be (Get-OpsObject $a.Saved 'lt').lineThicknessTwips 10 'line'
}

It 'moves an object into another section at the position given' {
    $a = Invoke-OpsPlan $opsSample @(
        @{ action='resizeSection'; section='ReportFooterSection1'; heightTwips=400 },
        @{ action='moveToSection'; target='Text2'; section='ReportFooterSection1'; leftTwips=100; topTwips=50 }) 'movesec.rpt'
    Should-Be $a.Result.ok $true "ok ($($a.Result.error))"
    $o = Get-OpsObject $a.Saved 'Text2'
    Should-Be ((Get-OpsSection $a.Saved 'ReportFooterSection1').objects | Where-Object { $_.name -eq 'Text2' } | Measure-Object).Count 1 'now in the footer'
    Should-Be ((Get-OpsSection $a.Saved 'PageHeaderSection1').objects | Where-Object { $_.name -eq 'Text2' } | Measure-Object).Count 0 'gone from the header'
    Should-Be $o.leftTwips 100 'left'; Should-Be $o.topTwips 50 'top'
}

It 'adds a group whose sections carry the predicted names, with its direction' {
    $a = Invoke-OpsPlan $opsSample @(
        @{ action='addGroup'; fieldRef='{Command.CardCode}'; direction='descending' },
        @{ action='addText'; section='CardCodeHeaderSection1'; newName='gtxt'; leftTwips=0; topTwips=0
           widthTwips=2000; heightTwips=220; text='Group' }) 'group.rpt'
    Should-Be $a.Result.ok $true "ok ($($a.Result.error))"
    $g = @($a.Saved.groups)
    Should-Be $g.Count 1 'groups'
    Should-Be $g[0].fieldRef '{Command.CardCode}' 'fieldRef'
    Should-Be $g[0].direction 'descending' 'direction'
    Should-Be $g[0].headerSection 'CardCodeHeaderSection1' 'header section'
    Should-Be $g[0].footerSection 'CardCodeFooterSection1' 'footer section'
    # Text placed into an empty new section starts as Arial 10, not the RAS default.
    $t = Get-OpsObject $a.Saved 'gtxt'
    Should-Be $t.fontName 'Arial' 'font'; Should-Be $t.fontSizePt 10 'size'
}

It 'adds a sort and orders two groups outermost first' {
    $a = Invoke-OpsPlan $opsSample @(
        @{ action='addGroup'; fieldRef='{Command.CardCode}' },
        @{ action='addGroup'; fieldRef='{Command.CardName}'; groupIndex=0 }) 'twogroups.rpt'
    $g = @($a.Saved.groups)
    Should-Be $g[0].fieldRef '{Command.CardName}' 'outer group'
    Should-Be $g[1].fieldRef '{Command.CardCode}' 'inner group'

    $b = Invoke-OpsPlan $opsSample @{ action='addSort'; fieldRef='{Command.CardName}'; direction='descending' } 'sort.rpt'
    $s = @($b.Saved.sorts)
    Should-Be $s.Count 1 'sorts'
    Should-Be $s[0].direction 'descending' 'sort direction'
}

It 'removes a table once its bound fields are removed in the same plan' {
    $a = Invoke-OpsPlan $opsSample @(
        @{ action='removeObject'; target='CardCode1' },
        @{ action='removeObject'; target='CardName1' },
        @{ action='removeTable'; target='Command' }) 'droptable.rpt'
    Should-Be $a.Result.ok $true "ok ($($a.Result.error))"
    Should-Be (@($a.Result.removedTables) -join ',') 'Command' 'removedTables'
    Should-Be (@($a.Saved.availableFields | Where-Object { $_.tableAlias -eq 'Command' }).Count) 0 'fields gone'
}

It 'refuses to remove a table while a field is still bound to it, naming the field' {
    $a = Invoke-OpsPlan $opsSample @{ action='removeTable'; target='Command' } 'nodrop.rpt'
    Should-Be $a.Result.ok $false 'ok'
    Should-Contain $a.Result.validationErrors[0].message 'CardCode1'
    if (Test-Path $a.Output) { throw 'an output file was written for a rejected plan' }
}

It 'embeds a sub-report and keeps two links, in order' {
    $a = Invoke-OpsPlan $opsHost @(
        @{ action='addSubreport'; section='DetailSection1'; newName='Linked'; reportPath=$opsSample
           leftTwips=150; topTwips=25; widthTwips=3000; heightTwips=300 },
        @{ action='setSubreportLink'; target='Linked'; mainReportField='{sp_perf_ind_perf_overview;1.emp_display_number}'; subreportField='{Command.CardCode}' },
        @{ action='setSubreportLink'; target='Linked'; mainReportField='{sp_perf_ind_perf_overview;1.employee_name}'; subreportField='{Command.CardName}' }) 'linked.rpt'
    Should-Be $a.Result.ok $true "ok ($($a.Result.error))"
    $sub = $a.Saved.sections | ForEach-Object { $_.objects } | Where-Object { $_.subreportName -eq 'Linked' }
    $links = @($sub.subreportLinks)
    Should-Be $links.Count 2 'links'
    Should-Be $links[0].subreportFieldName '{Command.CardCode}' 'first link'
    Should-Be $links[1].subreportFieldName '{Command.CardName}' 'second link'
}

It 'points at the subreportName when a link targets the object name' {
    $a = Invoke-OpsPlan $opsHost @{ action='setSubreportLink'; target='Subreport2'
                                    mainReportField='{sp_perf_ind_perf_overview;1.emp_display_number}'; subreportField='{x.y}' } 'badlink.rpt'
    Should-Be $a.Result.ok $false 'ok'
    Should-Contain $a.Result.validationErrors[0].message 'stage wise eval'
}

It 'fails addTable before contacting the server when VIBEY_DB_PASSWORD is unset' {
    $saved = $env:VIBEY_DB_PASSWORD
    try {
        $env:VIBEY_DB_PASSWORD = $null
        $a = Invoke-OpsPlan $opsSample @{ action='addTable'; target='Command'; tableName='Anything;1'; newName='second' } 'nopwd.rpt'
        Should-Be $a.Result.ok $false 'ok'
        Should-Contain $a.Result.error 'VIBEY_DB_PASSWORD'
        if (Test-Path $a.Output) { throw 'an output file was written' }
    } finally { $env:VIBEY_DB_PASSWORD = $saved }
}

It 'writes the zero, decimal and thousands strings a number format needs' {
    # Regression: once the system-default gate is off, Crystal uses these three strings literally,
    # and on a field built by addField they start EMPTY - a zero printed as nothing and 41.00 as
    # "4100". Read straight off the saved object: the schema does not carry them.
    # A field this tool creates, because that is where the strings start empty. An existing field
    # (PageNumber1 in the fixture) already carries '<Default Format>', which is deliberately left alone.
    $a = Invoke-OpsPlan $opsSample @(
        @{ action='addSpecialField'; section='PageFooterSection1'; newName='pnx'; specialType='pageNumber'; leftTwips=2000; topTwips=100; widthTwips=1500; heightTwips=221 },
        @{ action='setNumberFormat'; target='pnx'; decimalPlaces=3; thousandsSeparator=$false }) 'numstr.rpt'
    Should-Be $a.Result.ok $true "ok ($($a.Result.error))"
    $probe = @"
Import-Module '$((Resolve-Path (Join-Path $PSScriptRoot '..\vibey-reports\scripts\VibeyCrystal.psm1')).Path)'
`$rd = Open-VibeyDocument '$($a.Output)'
`$nf = (Find-VibeyObject -Doc `$rd.ReportClientDocument -Name 'pnx').Object.FieldFormat.NumericFormat
'{0}|{1}|{2}|{3}' -f (Get-VibeyComProperty `$nf 'ZeroValueString'), (Get-VibeyComProperty `$nf 'DecimalSymbol'), (Get-VibeyComProperty `$nf 'ThousandSymbol'), (Get-VibeyComProperty `$nf 'ThousandsSeparator')
`$rd.Close()
"@
    $x86 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    $line = (& $x86 -NoProfile -ExecutionPolicy Bypass -Command $probe) | Select-Object -Last 1
    Should-Be $line '0.000|.|,|False' 'zero|decimal|thousand|separator on'
}
