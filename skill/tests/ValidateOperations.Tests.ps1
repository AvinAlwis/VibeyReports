# Unit tests for the validation rules ported with the newer operations. Pure - no Crystal.

Import-Module (Join-Path $PSScriptRoot '..\vibey-reports\scripts\VibeyValidate.psm1') -Force

function VoSchema {
    @{ reportPath = 'x.rpt'
       page = @{ widthTwips = 11906; heightTwips = 16838
                 marginLeftTwips = 360; marginRightTwips = 360
                 marginTopTwips = 360; marginBottomTwips = 360 }
       sections = @(
         @{ name = 'Section1'; kind = 'ReportHeader'; heightTwips = 2000; objects = @(
              @{ name = 'Title'; kind = 'Text'; leftTwips = 100; topTwips = 100; widthTwips = 2000; heightTwips = 300 },
              @{ name = 'Rule';  kind = 'Line'; leftTwips = 0;   topTwips = 500; widthTwips = 5000; heightTwips = 0 },
              @{ name = 'Sub1';  kind = 'Subreport'; leftTwips = 0; topTwips = 1000; widthTwips = 500; heightTwips = 300
                 subreportName = 'Detail' }) },
         @{ name = 'Section2'; kind = 'Details'; heightTwips = 500; objects = @(
              @{ name = 'Frame'; kind = 'Box'; leftTwips = 0; topTwips = 0; widthTwips = 200; heightTwips = 200 },
              @{ name = 'Code'; kind = 'Field'; leftTwips = 300; topTwips = 0; widthTwips = 500; heightTwips = 200
                 dataSource = '{sp_x;1.code}' }) })
       availableFields = @(
         @{ name = 'code'; formulaForm = '{sp_x;1.code}'; tableAlias = 'sp_x;1'; valueType = 'String' },
         @{ name = 'name'; formulaForm = '{sp_x;1.name}'; tableAlias = 'sp_x;1'; valueType = 'String' })
       groups = @(); sorts = @() }
}
function VoPlan { @{ planVersion = 1; operations = @($args | ForEach-Object { $_ }) } }
function VoRejected($Result) { ($Result.Errors | ForEach-Object { $_.OperationIndex } | Sort-Object -Unique) -join ',' }

It 'predicts the section names Crystal gives a new group' {
    Should-Be (Get-VibeyGroupSectionName -FieldRef '{sp_perf_ind_perf_sheet;1.emp_display_number}' -Band Header) 'empdisplaynumberHeaderSection1' 'header'
    Should-Be (Get-VibeyGroupSectionName -FieldRef '{Command.CardCode}' -Band Footer) 'CardCodeFooterSection1' 'footer'
}

It 'lets a plan place text into a group header it creates' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan `
        @{ action='addGroup'; fieldRef='{sp_x;1.code}' } `
        @{ action='addText'; section='codeHeaderSection1'; newName='H'; text='x'; leftTwips=0; topTwips=0; widthTwips=100; heightTwips=200 })
    Should-Be $r.IsValid $true 'IsValid'
}

It 'rejects a second group, a sort on a grouped field, an unknown field and a topN order' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan `
        @{ action='addGroup'; fieldRef='{sp_x;1.code}' } `
        @{ action='addGroup'; fieldRef='{sp_x;1.code}' } `
        @{ action='addSort'; fieldRef='{sp_x;1.code}'; direction='ascending' } `
        @{ action='addGroup'; fieldRef='{sp_x;1.nope}' } `
        @{ action='addSort'; fieldRef='{sp_x;1.name}'; direction='topN' })
    Should-Be (VoRejected $r) '1,2,3,4' 'rejected operations'
}

It 'refuses removeTable while a field is bound to it, and allows it after removeObject' {
    $bad = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan @{ action='removeTable'; target='sp_x;1' })
    Should-Be $bad.IsValid $false 'bound'
    Should-Contain $bad.Errors[0].Message 'Code'
    $good = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan @{ action='removeObject'; target='Code' } @{ action='removeTable'; target='sp_x;1' })
    Should-Be $good.IsValid $true 'after removeObject'
}

It 'drops a removed table from the field allowlist' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan `
        @{ action='removeObject'; target='Code' } `
        @{ action='removeTable'; target='sp_x;1' } `
        @{ action='addField'; section='Section2'; newName='F'; fieldRef='{sp_x;1.name}'; leftTwips=0; topTwips=0; widthTwips=100; heightTwips=100 })
    Should-Be (VoRejected $r) '2' 'addField rejected'
}

It 'rejects addField on a field the report does not expose' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan `
        @{ action='addField'; section='Section2'; newName='F'; fieldRef='{other;1.x}'; leftTwips=0; topTwips=0; widthTwips=100; heightTwips=100 })
    Should-Be $r.IsValid $false 'IsValid'
}

It 'checks addTable and setTableLocation against the table aliases' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan `
        @{ action='addTable'; target='sp_x;1'; tableName='sp_y;1'; newName='y' } `
        @{ action='addTable'; target='nope'; tableName='sp_z;1'; newName='z' } `
        @{ action='addTable'; target='sp_x;1'; tableName='sp_y;1'; newName='y' } `
        @{ action='setTableLocation'; target='y'; tableName='sp_w;1' })
    Should-Be (VoRejected $r) '1,2' 'rejected operations'
}

It 'resolves setSubreportLink by subreportName and explains the object-name mistake' {
    $ok = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan @{ action='setSubreportLink'; target='Detail'; mainReportField='{sp_x;1.code}'; subreportField='{d.code}' })
    Should-Be $ok.IsValid $true 'by subreportName'
    $bad = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan @{ action='setSubreportLink'; target='Sub1'; mainReportField='{sp_x;1.code}'; subreportField='{d.code}' })
    Should-Contain $bad.Errors[0].Message '"Detail"'
}

It 'rejects object operations on a sub-report added in the same plan' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan `
        @{ action='addSubreport'; section='Section1'; newName='New'; reportPath='C:\x\y.rpt'; leftTwips=0; topTwips=0; widthTwips=100; heightTwips=100 } `
        @{ action='move'; target='New'; leftTwips=10; topTwips=10 })
    Should-Be (VoRejected $r) '1' 'move rejected'
}

It 'requires an absolute .rpt path for addSubreport' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan `
        @{ action='addSubreport'; section='Section1'; newName='A'; reportPath='relative.rpt'; leftTwips=0; topTwips=0; widthTwips=100; heightTwips=100 } `
        @{ action='addSubreport'; section='Section1'; newName='B'; reportPath='C:\x\y.txt'; leftTwips=0; topTwips=0; widthTwips=100; heightTwips=100 })
    Should-Be (VoRejected $r) '0,1' 'both rejected'
}

It 'enforces the border rules for drawn objects' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan `
        @{ action='setBorder'; target='Frame'; top='double' } `
        @{ action='setBorder'; target='Rule'; left='single' } `
        @{ action='setBorder'; target='Rule'; top='single' } `
        @{ action='setBorder'; target='Title' } `
        @{ action='setBorder'; target='Title'; bottom='wavy' } `
        @{ action='setBorder'; target='Title'; bottom='double'; color='#112233' })
    Should-Be (VoRejected $r) '0,1,3,4' 'rejected operations'
}

It 'bounds line thickness and restricts it to boxes and lines' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan `
        @{ action='setLineThickness'; target='Frame'; lineThicknessTwips=20 } `
        @{ action='setLineThickness'; target='Title'; lineThicknessTwips=20 } `
        @{ action='setLineThickness'; target='Rule'; lineThicknessTwips=101 })
    Should-Be (VoRejected $r) '1,2' 'rejected operations'
}

It 'checks moveToSection against the destination section' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan `
        @{ action='moveToSection'; target='Title'; section='Section2'; topTwips=100 } `
        @{ action='moveToSection'; target='Frame'; section='Section2' } `
        @{ action='moveToSection'; target='Rule'; section='Section2'; topTwips=600 })
    Should-Be (VoRejected $r) '1,2' 'rejected operations'
}

It 'refuses to shrink a section below an object in it' {
    $r = Test-VibeyPlan -Schema (VoSchema) -Plan (VoPlan @{ action='resizeSection'; section='Section1'; heightTwips=400 })
    Should-Be $r.IsValid $false 'IsValid'
    Should-Contain $r.Errors[0].Message 'would clip'
}
