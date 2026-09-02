Import-Module (Join-Path $PSScriptRoot '..\vibey-reports\scripts\VibeyValidate.psm1') -Force

function TestSchema {
    @{ reportPath = 'x.rpt'
       page = @{ widthTwips = 11906; heightTwips = 16838
                 marginLeftTwips = 360; marginRightTwips = 360
                 marginTopTwips = 360; marginBottomTwips = 360 }
       sections = @(
         @{ name = 'Section1'; kind = 'ReportHeader'; heightTwips = 2000; objects = @(
              @{ name = 'Title';  kind = 'Text';  leftTwips = 100; topTwips = 100; widthTwips = 2000; heightTwips = 300 },
              @{ name = 'Rule';   kind = 'Line';  leftTwips = 0;   topTwips = 500; widthTwips = 5000; heightTwips = 0 }) },
         @{ name = 'Section2'; kind = 'Details';      heightTwips = 500;  objects = @() }) }
}
function PlanOf {
    # NOTE: `PlanOf @{...}, @{...}` (comma-joined hashtables, no surrounding parens) makes
    # PowerShell's parser build ONE array literal and pass it as a single positional argument,
    # not two separate ones - so $args ends up as a one-element array wrapping a nested
    # two-element array, and `@($args)` does not flatten it. Flatten defensively here so both
    # call shapes (a single hashtable, or several comma-joined ones) land as one operation per
    # hashtable.
    $ops = @()
    foreach ($a in $args) {
        if ($a -is [array]) { $ops += $a } else { $ops += ,$a }
    }
    @{ planVersion = 1; operations = $ops }
}

It 'accepts a move that stays inside the section' {
    $r = Test-VibeyPlan -Plan (PlanOf @{ action='move'; target='Title'; leftTwips=200; topTwips=200 }) -Schema (TestSchema)
    Should-Be $r.IsValid $true 'IsValid'
}

It 'rejects an unknown action and names it' {
    $r = Test-VibeyPlan -Plan (PlanOf @{ action='explode'; target='Title' }) -Schema (TestSchema)
    Should-Be $r.IsValid $false 'IsValid'
    Should-Contain $r.Errors[0].Message 'explode'
}

It 'rejects a target that does not exist' {
    $r = Test-VibeyPlan -Plan (PlanOf @{ action='move'; target='Nope'; leftTwips=10; topTwips=10 }) -Schema (TestSchema)
    Should-Be $r.IsValid $false 'IsValid'
    Should-Contain $r.Errors[0].Message 'Nope'
}

It 'rejects an object taller than its section' {
    $r = Test-VibeyPlan -Plan (PlanOf @{ action='resize'; target='Title'; widthTwips=2000; heightTwips=9000 }) -Schema (TestSchema)
    Should-Be $r.IsValid $false 'IsValid'
}

It 'accepts growing the section first, then the object - the cumulative rule' {
    $r = Test-VibeyPlan -Plan (PlanOf `
            @{ action='resizeSection'; section='Section1'; heightTwips=10000 }, `
            @{ action='resize'; target='Title'; widthTwips=2000; heightTwips=9000 }) -Schema (TestSchema)
    Should-Be $r.IsValid $true 'IsValid'
}

It 'rejects a section taller than one printable page' {
    $r = Test-VibeyPlan -Plan (PlanOf @{ action='resizeSection'; section='Section1'; heightTwips=20000 }) -Schema (TestSchema)
    Should-Be $r.IsValid $false 'IsValid'
}

It 'rejects moving an object removed earlier in the same plan, and says so' {
    $r = Test-VibeyPlan -Plan (PlanOf `
            @{ action='removeObject'; target='Title' }, `
            @{ action='move'; target='Title'; leftTwips=10; topTwips=10 }) -Schema (TestSchema)
    Should-Be $r.IsValid $false 'IsValid'
    Should-Contain $r.Errors[0].Message 'removed'
}

It 'rejects a diagonal line' {
    $r = Test-VibeyPlan -Plan (PlanOf @{ action='addLine'; section='Section2'; newName='D'
                                         leftTwips=0; topTwips=0; widthTwips=500; heightTwips=500 }) -Schema (TestSchema)
    Should-Be $r.IsValid $false 'IsValid'
}

It 'rejects a new name that is already taken' {
    $r = Test-VibeyPlan -Plan (PlanOf @{ action='addText'; section='Section2'; newName='Title'; text='x'
                                         leftTwips=0; topTwips=0; widthTwips=500; heightTwips=200 }) -Schema (TestSchema)
    Should-Be $r.IsValid $false 'IsValid'
}

It 'rejects setBold on a Line, which carries no font' {
    $r = Test-VibeyPlan -Plan (PlanOf @{ action='setBold'; target='Rule'; bold=$true }) -Schema (TestSchema)
    Should-Be $r.IsValid $false 'IsValid'
}

It 'reports every failing operation, not just the first' {
    $r = Test-VibeyPlan -Plan (PlanOf `
            @{ action='move'; target='Nope';  leftTwips=10; topTwips=10 }, `
            @{ action='move'; target='Nope2'; leftTwips=10; topTwips=10 }) -Schema (TestSchema)
    Should-Be $r.Errors.Count 2 'error count'
}

It 'returns IsValid false rather than throwing when operations is null' {
    $r = Test-VibeyPlan -Plan @{ planVersion = 1; operations = $null } -Schema (TestSchema)
    Should-Be $r.IsValid $false 'IsValid'
}
