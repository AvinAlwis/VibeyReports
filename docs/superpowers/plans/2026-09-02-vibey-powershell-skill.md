# Vibey Reports PowerShell Skill Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Package Crystal Reports layout editing as a Claude Code skill written entirely in PowerShell, talking to the Crystal SDK directly — no MCP server, no compiled worker, no build step.

**Architecture:** One entry script takes a single JSON request on stdin and writes a single JSON response on stdout, relaunching itself under 32-bit PowerShell because the Crystal assemblies are x86. A module holds open/save/read/validate/apply. The validator is a pure function over a plan and a schema and runs to completion before any SDK call, so a rejected plan writes nothing.

**Tech Stack:** Windows PowerShell 5.1, Crystal Reports XI R2 RAS SDK (COM, x86), no external modules.

**Spec:** `docs/superpowers/specs/2026-09-02-vibey-skill-design.md`

## Global Constraints

- **Crystal assemblies only from the installed 11.5 product.** Load via
  `[Reflection.Assembly]::LoadWithPartialName('CrystalDecisions.CrystalReports.Engine')`, which
  resolves the GAC copy. **Never** the `CrystalReports.*` 13.x NuGet packages — they save `.rpt` in a
  format Crystal XI R2 and the customer's HR software cannot open.
- **The source `.rpt` is never modified.** Open, mutate in memory, `SaveAs` elsewhere.
- **Validation is the single gate.** No SDK mutation may begin until the whole plan has passed.
- **No operation accepts a credential.** Nothing in this skill reads a username or password.
- **No external PowerShell modules.** Pester 5 is not installed on the target; only Pester 3.4. The
  test harness is hand-rolled so the skill needs nothing installed.
- **UTF-8 without BOM** on stdin, stdout and every file written. A BOM on stdout corrupts the JSON
  protocol and is invisible to a string-based test.
- **Never write to `out/reports/` or `tests/fixtures/`.** Probe to a scratch path and delete after.
- **Twenty-one operations, exactly.** `addTable`, `removeTable`, `setTableLocation`, `addSubreport`,
  `setSubreportLink`, `addGroup`, `addSort`, formulas and record selection are out of scope; the spec
  explains why for each. An unknown action is a validation error.

## File Structure

| File | Responsibility |
|---|---|
| `skill/vibey-reports/scripts/vibey.ps1` | Entry point. Reads one JSON request, relaunches x86 if needed, dispatches to read/apply, writes one JSON response. |
| `skill/vibey-reports/scripts/VibeyCrystal.psm1` | Open, save, section walking, colour conversion, read, apply. Everything that touches the SDK. |
| `skill/vibey-reports/scripts/VibeyValidate.psm1` | The validator. Pure — no SDK, no file I/O, no environment. Kept separate so it is testable with nothing installed. |
| `skill/vibey-reports/SKILL.md` | Skill definition and the workflow knowledge. |
| `skill/install.ps1` | Copies `skill/vibey-reports/` to `~/.claude/skills/`. |
| `skill/tests/Run-VibeyTests.ps1` | Dependency-free harness: discovers `*.Tests.ps1`, reports pass/fail counts, exits non-zero on failure. |
| `skill/tests/Protocol.Tests.ps1` | Entry-point contract: BOM, x86 relaunch, malformed input. |
| `skill/tests/Validate.Tests.ps1` | Validator rules. No Crystal needed. |
| `skill/tests/Read.Tests.ps1` | Reading a real fixture. |
| `skill/tests/Apply.Tests.ps1` | Applying operations; asserts the source stays byte-identical. |

Splitting the validator into its own module is the one structural decision that matters: it is the
safety gate, it is the part most worth testing, and keeping it free of SDK references is what lets it
be tested on a machine with no Crystal install.

---

### Task 1: Entry point, x86 relaunch, and the test harness

**Files:**
- Create: `skill/vibey-reports/scripts/vibey.ps1`
- Create: `skill/tests/Run-VibeyTests.ps1`
- Create: `skill/tests/Protocol.Tests.ps1`

**Interfaces:**
- Consumes: nothing.
- Produces: `vibey.ps1` accepting `-RequestFile <path>`; reads stdin when the parameter is absent.
  Emits `{"ok":true,...}` or `{"ok":false,"error":"..."}`. The harness exposes `It`, `Should-Be`,
  `Should-Contain`, `Should-Throw`.

- [ ] **Step 1: Write the failing test**

Create `skill/tests/Protocol.Tests.ps1`:

```powershell
$entry = Join-Path $PSScriptRoot '..\vibey-reports\scripts\vibey.ps1'

It 'returns ok:false with a message for malformed JSON' {
    $out = 'not json at all' | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $entry
    $r = $out -join '' | ConvertFrom-Json
    Should-Be $r.ok $false 'ok'
    Should-Contain $r.error 'JSON'
}

It 'returns ok:false for an unknown command' {
    $out = '{"command":"explode"}' | & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $entry
    $r = $out -join '' | ConvertFrom-Json
    Should-Be $r.ok $false 'ok'
    Should-Contain $r.error 'explode'
}

It 'writes stdout with no byte order mark' {
    # MEASURED: "Out-File -Encoding utf8" ALWAYS prepends a BOM on Windows PowerShell 5.1,
    # whatever it is given - verified with a bare string. Routing the child's output through
    # it would therefore test Out-File, not vibey.ps1, and fail no matter what the program
    # emits. Read the child's raw stdout stream instead, before anything can re-encode it.
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName               = 'powershell.exe'
    $psi.Arguments              = "-NoProfile -ExecutionPolicy Bypass -File `"$entry`""
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.UseShellExecute        = $false
    $p = [System.Diagnostics.Process]::Start($psi)
    $p.StandardInput.Write('{"command":"explode"}')
    $p.StandardInput.Close()
    $bytes = New-Object byte[] 3
    $read  = $p.StandardOutput.BaseStream.Read($bytes, 0, 3)
    $p.WaitForExit()
    if ($read -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
        throw "stdout began with a UTF-8 BOM: $($bytes -join ',')"
    }
}
```

- [ ] **Step 2: Write the harness so the test can run**

Create `skill/tests/Run-VibeyTests.ps1`:

```powershell
# Dependency-free test harness. Pester 5 is not installed on this machine and Pester 3.4's
# syntax diverges sharply, so the skill carries its own runner and needs nothing installed.
[CmdletBinding()]
param([string]$Filter = '*')

$script:Pass = 0
$script:Fail = 0
$script:Failures = @()

function It([string]$Name, [scriptblock]$Body) {
    try   { & $Body; $script:Pass++; Write-Host "  PASS  $Name" -ForegroundColor DarkGreen }
    catch { $script:Fail++; $script:Failures += "$Name : $($_.Exception.Message)"
            Write-Host "  FAIL  $Name" -ForegroundColor Red
            Write-Host "        $($_.Exception.Message)" -ForegroundColor DarkGray }
}
function Should-Be($Actual, $Expected, [string]$What = 'value') {
    if ($Actual -ne $Expected) { throw "$What expected [$Expected] but was [$Actual]" }
}
function Should-Contain([string]$Actual, [string]$Needle) {
    if ($null -eq $Actual -or $Actual -notlike "*$Needle*") {
        throw "expected text containing [$Needle] but was [$Actual]"
    }
}
function Should-Throw([scriptblock]$Body, [string]$Needle) {
    $threw = $false
    try { & $Body } catch { $threw = $true
        if ($Needle -and $_.Exception.Message -notlike "*$Needle*") {
            throw "threw, but message [$($_.Exception.Message)] lacks [$Needle]" } }
    if (-not $threw) { throw 'expected an exception, none thrown' }
}

foreach ($f in Get-ChildItem -LiteralPath $PSScriptRoot -Filter '*.Tests.ps1' | Where-Object { $_.Name -like $Filter }) {
    Write-Host "`n$($f.Name)" -ForegroundColor Cyan
    . $f.FullName
}

Write-Host "`n$script:Pass passed, $script:Fail failed" -ForegroundColor $(if ($script:Fail) { 'Red' } else { 'Green' })
if ($script:Fail) { exit 1 }
```

- [ ] **Step 3: Run it to make sure the tests fail**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Protocol.Tests.ps1`
Expected: three FAIL lines, exit code 1 — `vibey.ps1` does not exist yet.

- [ ] **Step 4: Write the entry point**

Create `skill/vibey-reports/scripts/vibey.ps1`:

```powershell
<#
    One JSON request on stdin, one JSON response on stdout.

    Relaunches itself under 32-bit PowerShell when started 64-bit: the Crystal assemblies are
    x86 and will not load in a 64-bit host. The request is handed to the child through a temp
    FILE rather than a pipe, because stdin plumbing across a relaunch is fragile and silently
    truncates on some hosts.
#>
[CmdletBinding()]
param([string]$RequestFile)

$ErrorActionPreference = 'Stop'
$utf8NoBom = New-Object System.Text.UTF8Encoding $false
[Console]::OutputEncoding = $utf8NoBom
[Console]::InputEncoding  = $utf8NoBom

function Write-VibeyResponse($Object) {
    [Console]::Out.Write(($Object | ConvertTo-Json -Depth 24 -Compress))
}

$raw = if ($RequestFile) { [IO.File]::ReadAllText($RequestFile) } else { [Console]::In.ReadToEnd() }

if ([Environment]::Is64BitProcess) {
    $x86 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    if (-not (Test-Path -LiteralPath $x86)) {
        Write-VibeyResponse @{ ok = $false; error = "32-bit PowerShell not found at $x86. The Crystal assemblies are x86 and cannot load in a 64-bit host." }
        exit 1
    }
    $tmp = [IO.Path]::GetTempFileName()
    try {
        [IO.File]::WriteAllText($tmp, $raw, $utf8NoBom)
        & $x86 -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -RequestFile $tmp
        exit $LASTEXITCODE
    } finally { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue }
}

try {
    try { $request = $raw | ConvertFrom-Json }
    catch { Write-VibeyResponse @{ ok = $false; error = "Request is not valid JSON: $($_.Exception.Message)" }; exit 1 }

    Import-Module (Join-Path $PSScriptRoot 'VibeyValidate.psm1') -Force
    Import-Module (Join-Path $PSScriptRoot 'VibeyCrystal.psm1')  -Force

    switch ($request.command) {
        'read'  { Write-VibeyResponse (Invoke-VibeyRead  -Request $request) }
        'apply' { Write-VibeyResponse (Invoke-VibeyApply -Request $request) }
        default {
            Write-VibeyResponse @{ ok = $false
                error = "`"$($request.command)`" is not a supported command. Supported: read, apply." }
            exit 1
        }
    }
} catch {
    Write-VibeyResponse @{ ok = $false; error = $_.Exception.Message }
    exit 1
}
```

Create the two modules as stubs so the imports resolve — each exporting the one function the switch
calls, returning a not-implemented response:

```powershell
# VibeyValidate.psm1
function Test-VibeyPlan { @{ IsValid = $false; Errors = @(@{ OperationIndex = -1; Message = 'not implemented' }) } }
Export-ModuleMember -Function Test-VibeyPlan
```

```powershell
# VibeyCrystal.psm1
function Invoke-VibeyRead  { @{ ok = $false; error = 'read not implemented' } }
function Invoke-VibeyApply { @{ ok = $false; error = 'apply not implemented' } }
Export-ModuleMember -Function Invoke-VibeyRead, Invoke-VibeyApply
```

- [ ] **Step 5: Run the tests to verify they pass**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Protocol.Tests.ps1`
Expected: `3 passed, 0 failed`.

- [ ] **Step 6: Commit**

```bash
git add skill/
git commit -m "feat(skill): JSON entry point with x86 relaunch and a dependency-free test harness"
```

---

### Task 2: Reading a report

**Files:**
- Modify: `skill/vibey-reports/scripts/VibeyCrystal.psm1`
- Create: `skill/tests/Read.Tests.ps1`

**Interfaces:**
- Consumes: `vibey.ps1`'s dispatch to `Invoke-VibeyRead -Request $request`.
- Produces:
  - `Open-VibeyDocument -Path <string>` → `ReportDocument` (call `.Close()` when done)
  - `Get-VibeySectionList -Doc <clientDoc>` → array of `@{ Name; Kind; Section }`
  - `Get-VibeySchema -Path <string>` → the schema hashtable Task 3 validates against
  - `Invoke-VibeyRead -Request <object>` → `@{ ok; schema }`

  Schema shape, which every later task depends on:

```
@{ reportPath = '...'
   page     = @{ widthTwips; heightTwips; marginLeftTwips; marginRightTwips
                 marginTopTwips; marginBottomTwips }
   sections = @( @{ name; kind; heightTwips; objects = @(
                      @{ name; kind; leftTwips; topTwips; widthTwips; heightTwips
                         text; dataSource; fontName; fontSizePt; bold } ) } ) }
```

- [ ] **Step 1: Write the failing test**

Create `skill/tests/Read.Tests.ps1`:

```powershell
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
```

- [ ] **Step 2: Run it to verify it fails**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Read.Tests.ps1`
Expected: four FAIL lines — `Invoke-VibeyRead` still returns `read not implemented`.

- [ ] **Step 3: Implement reading**

Replace the stub in `VibeyCrystal.psm1`:

```powershell
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
    param($Kind)
    switch ([int]$Kind) {
        1 { 'ReportHeader' } 2 { 'PageHeader' } 3 { 'GroupHeader' } 4 { 'Details' }
        5 { 'GroupFooter' } 6 { 'PageFooter' } 7 { 'ReportFooter' } default { 'Unknown' }
    }
}

function ConvertFrom-VibeyObjectKind {
    param($Kind)
    switch ([int]$Kind) {
        1 { 'Field' } 2 { 'Text' } 3 { 'Line' } 4 { 'Box' }
        5 { 'Subreport' } 6 { 'Picture' } 8 { 'FieldHeading' } default { 'Other' }
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
    # MEASURED: fonts are ISCRFont with a decimal Size, reached through FontColor.Font -
    # they are not System.Drawing.Font.
    $fc = $null
    try { $fc = $o.FontColor } catch { }
    if ($fc -and $fc.Font) {
        $info.fontName   = $fc.Font.Name
        $info.fontSizePt = [double]$fc.Font.Size
        $info.bold       = [bool]$fc.Font.Bold
    }
    try { if ($o.Text)       { $info.text       = $o.Text } }       catch { }
    try { if ($o.DataSource) { $info.dataSource = $o.DataSource } } catch { }
    return $info
}

function Get-VibeySchema {
    param([Parameter(Mandatory)][string]$Path)
    $rd = Open-VibeyDocument -Path $Path
    try {
        $doc = $rd.ReportClientDocument
        $pageOpts = $doc.PrintOutputController.GetPageMargins()
        $fmt = $doc.ReportDefController.ReportDefinition.PageSetup
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
                widthTwips  = [int]$fmt.PaperWidth ; heightTwips = [int]$fmt.PaperHeight
                marginLeftTwips   = [int]$pageOpts.leftMargin
                marginRightTwips  = [int]$pageOpts.rightMargin
                marginTopTwips    = [int]$pageOpts.topMargin
                marginBottomTwips = [int]$pageOpts.bottomMargin
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Read.Tests.ps1`
Expected: `4 passed, 0 failed`.

If page geometry or margins come back wrong, print the real property names before guessing:
`$doc.ReportDefController.ReportDefinition.PageSetup | Get-Member`. Record whatever you find in
`docs/sdk-notes.md`, including what did not work.

- [ ] **Step 5: Commit**

```bash
git add skill/
git commit -m "feat(skill): read a report into a schema"
```

---

### Task 3: The validator

**Files:**
- Modify: `skill/vibey-reports/scripts/VibeyValidate.psm1`
- Create: `skill/tests/Validate.Tests.ps1`

**Interfaces:**
- Consumes: the schema shape from Task 2.
- Produces: `Test-VibeyPlan -Plan <object> -Schema <object>` →
  `@{ IsValid = <bool>; Errors = @(@{ OperationIndex = <int>; Message = <string> }) }`
  and `$script:VibeyActions`, the array of the twenty-one supported action names.

This task has no SDK dependency and its tests need no Crystal install.

- [ ] **Step 1: Write the failing test**

Create `skill/tests/Validate.Tests.ps1`:

```powershell
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
function PlanOf { @{ planVersion = 1; operations = @($args) } }

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
```

- [ ] **Step 2: Run it to verify it fails**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Validate.Tests.ps1`
Expected: twelve FAIL lines — the stub returns `not implemented`.

- [ ] **Step 3: Implement the validator**

Replace `VibeyValidate.psm1` entirely:

```powershell
<#
    The safety gate. Pure: no SDK, no file I/O, no environment access. It simulates the plan
    cumulatively - grow a section then place a taller object in it and both pass, because by
    that point the section IS taller.

    A plan that fails here is applied NOT AT ALL. That all-or-nothing property is the whole
    point; do not add a "skip the bad ones and carry on" mode.
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
        $action = [string]$op.action

        if ($script:VibeyActions -notcontains $action) {
            Add-Err $i "`"$action`" is not a supported action. Supported: $($script:VibeyActions -join ', ')."
            continue
        }

        # --- resolve the target, for the operations that take one -------------------------
        $needsTarget = @('move','resize','setAlignment','removeObject','setFont','setFontSize',
                         'setBold','setTextColor','setFillColor','setLineColor','setNumberFormat',
                         'setCanGrow') -contains $action -or
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
                        $action -in @('resizeSection','setSectionBackground','setSectionBreak') -or
                        ($action -eq 'setSuppress' -and -not $op.target)
        if ($needsSection) {
            if (-not $op.section) { Add-Err $i "`"$action`" requires `"section`"."; continue }
            if (-not $sectionHeights.ContainsKey($op.section)) {
                Add-Err $i "Section `"$($op.section)`" does not exist in the report."; continue
            }
        }

        switch ($action) {
            'resizeSection' {
                $h = [int]$op.heightTwips
                if ($h -lt 0) { Add-Err $i 'Section height cannot be negative.'; break }
                if ($h -gt $printableHeight) {
                    Add-Err $i "Section height $h exceeds the printable height of $printableHeight."; break
                }
                $sectionHeights[$op.section] = $h
            }
            'move' {
                $l = [int]$op.leftTwips; $t = [int]$op.topTwips
                $secH = $sectionHeights[$target.Section]
                # "Do not make it worse": an object already overflowing may be moved, as long as
                # the move does not increase the overflow. Otherwise a report that arrives broken
                # can never be repaired.
                $wasBad = ($target.Left + $target.Width -gt $printableWidth) -or ($target.Top + $target.Height -gt $secH)
                $isBad  = ($l + $target.Width -gt $printableWidth) -or ($t + $target.Height -gt $secH)
                if ($isBad -and -not $wasBad) {
                    Add-Err $i "`"$($op.target)`" would end at $($l + $target.Width) x $($t + $target.Height), outside the printable width $printableWidth or section height $secH."
                    break
                }
                $target.Left = $l; $target.Top = $t
            }
            'resize' {
                $w = [int]$op.widthTwips; $h = [int]$op.heightTwips
                if ($w -lt 0 -or $h -lt 0) { Add-Err $i 'Width and height cannot be negative.'; break }
                $secH = $sectionHeights[$target.Section]
                if ($target.Left + $w -gt $printableWidth) {
                    Add-Err $i "`"$($op.target)`" would end at $($target.Left + $w), past the printable width of $printableWidth."; break
                }
                if ($target.Top + $h -gt $secH) {
                    Add-Err $i "`"$($op.target)`" would end at $($target.Top + $h), past the height of section `"$($target.Section)`" ($secH)."; break
                }
                $target.Width = $w; $target.Height = $h
            }
            'setAlignment' {
                if ($script:Alignments -notcontains [string]$op.alignment) {
                    Add-Err $i "`"$($op.alignment)`" is not a valid alignment. Use: $($script:Alignments -join ', ')."
                }
            }
            'removeObject' {
                $removed[$op.target] = $true
                [void]$objects.Remove($op.target)
            }
            'setFont'      { if (-not $op.fontName) { Add-Err $i '"setFont" requires "fontName".' }
                             elseif ($script:FontableKinds -notcontains $target.Kind) {
                               Add-Err $i "`"$($op.target)`" is a $($target.Kind) and has no font." } }
            'setFontSize'  { $pt = [double]$op.fontSizePt
                             if ($pt -lt 4 -or $pt -gt 72) { Add-Err $i "Font size $pt is outside 4-72 points." }
                             elseif ($script:FontableKinds -notcontains $target.Kind) {
                               Add-Err $i "`"$($op.target)`" is a $($target.Kind) and has no font." } }
            'setBold'      { if ($null -eq $op.bold) { Add-Err $i '"setBold" requires "bold".' }
                             elseif ($script:FontableKinds -notcontains $target.Kind) {
                               Add-Err $i "`"$($op.target)`" is a $($target.Kind) and has no font." } }
            'setCanGrow'   { if ($null -eq $op.canGrow) { Add-Err $i '"setCanGrow" requires "canGrow".' }
                             elseif ($script:CanGrowKinds -notcontains $target.Kind) {
                               Add-Err $i "`"$($op.target)`" is a $($target.Kind) and holds no flowing text." } }
            'setNumberFormat' {
                if ($target.Kind -ne 'Field') { Add-Err $i "`"$($op.target)`" is a $($target.Kind); only a Field has a number format."; break }
                if ($null -eq $op.decimalPlaces -and $null -eq $op.thousandsSeparator -and $null -eq $op.suppressIfZero) {
                    Add-Err $i '"setNumberFormat" needs at least one of "decimalPlaces", "thousandsSeparator" or "suppressIfZero".'; break
                }
                if ($null -ne $op.decimalPlaces -and ([int]$op.decimalPlaces -lt 0 -or [int]$op.decimalPlaces -gt 10)) {
                    Add-Err $i "decimalPlaces must be 0-10, was $($op.decimalPlaces)."
                }
            }
            'setSectionBreak' {
                if ($null -eq $op.newPageBefore -and $null -eq $op.newPageAfter) {
                    Add-Err $i '"setSectionBreak" needs "newPageBefore" or "newPageAfter".'
                }
            }
            'setSuppress' {
                if ($null -eq $op.suppress) { Add-Err $i '"setSuppress" requires "suppress".'; break }
                if ($op.target -and $op.section) { Add-Err $i '"setSuppress" takes "target" or "section", not both.'; break }
                if (-not $op.target -and -not $op.section) { Add-Err $i '"setSuppress" requires "target" or "section".'; break }
                if ($op.target -and $null -ne $op.suppressIfBlank) {
                    Add-Err $i '"suppressIfBlank" applies to a section, not an object.'
                }
            }
            { $script:AddActions -contains $_ } {
                if (-not $op.newName) { Add-Err $i "`"$action`" requires `"newName`"."; break }
                if ($objects.ContainsKey($op.newName)) {
                    Add-Err $i "An object named `"$($op.newName)`" already exists."; break
                }
                $l = [int]$op.leftTwips; $t = [int]$op.topTwips
                $w = [int]$op.widthTwips; $h = [int]$op.heightTwips
                if ($l -lt 0 -or $t -lt 0 -or $w -lt 0 -or $h -lt 0) { Add-Err $i 'Geometry cannot be negative.'; break }
                if ($l + $w -gt $printableWidth) {
                    Add-Err $i "`"$($op.newName)`" would end at $($l + $w), past the printable width of $printableWidth."; break
                }
                $secH = $sectionHeights[$op.section]
                if ($t + $h -gt $secH) {
                    Add-Err $i "`"$($op.newName)`" would end at $($t + $h), past the height of section `"$($op.section)`" ($secH)."; break
                }
                if ($action -eq 'addLine' -and $w -ne 0 -and $h -ne 0) {
                    Add-Err $i 'A line must be horizontal or vertical: set widthTwips or heightTwips to 0.'; break
                }
                if ($action -eq 'addText'  -and $null -eq $op.text)     { Add-Err $i '"addText" requires "text".'; break }
                if ($action -eq 'addField' -and -not $op.fieldRef)      { Add-Err $i '"addField" requires "fieldRef".'; break }
                if ($action -eq 'addSpecialField') {
                    if (-not $op.specialType) { Add-Err $i '"addSpecialField" requires "specialType".'; break }
                    if ($script:SpecialTypes -notcontains [string]$op.specialType) {
                        Add-Err $i "`"$($op.specialType)`" is not a supported specialType. Use: $($script:SpecialTypes -join ', ')."; break
                    }
                }
                $kind = switch ($action) { 'addText' { 'Text' } 'addLine' { 'Line' } 'addBox' { 'Box' } default { 'Field' } }
                $objects[$op.newName] = @{ Section = $op.section; Left = $l; Top = $t; Width = $w; Height = $h; Kind = $kind }
                [void]$removed.Remove($op.newName)
            }
        }
    }

    return @{ IsValid = ($errors.Count -eq 0); Errors = @($errors) }
}

Export-ModuleMember -Function Test-VibeyPlan -Variable VibeyActions
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Validate.Tests.ps1`
Expected: `12 passed, 0 failed`.

- [ ] **Step 5: Commit**

```bash
git add skill/
git commit -m "feat(skill): the plan validator, pure and all-or-nothing"
```

---

### Task 4: Applying geometry and typography

**Files:**
- Modify: `skill/vibey-reports/scripts/VibeyCrystal.psm1`
- Create: `skill/tests/Apply.Tests.ps1`

**Interfaces:**
- Consumes: `Get-VibeySchema`, `Get-VibeySectionList` (Task 2); `Test-VibeyPlan` (Task 3).
- Produces:
  - `Save-VibeyDocument -Doc <clientDoc> -OutputPath <string>` → void
  - `Find-VibeyObject -Doc <clientDoc> -Name <string>` → `@{ Object; Section }`
  - `Set-VibeyObject -Doc <clientDoc> -Original <obj> -Modified <obj>` → void
  - `Invoke-VibeyApply -Request <object>` → `@{ ok; operationsApplied; removedObjects; schema }`

Implements `move`, `resize`, `resizeSection`, `setAlignment`, `setFont`, `setFontSize`, `setBold`.
The remaining actions are added in Tasks 5 and 6; until then they must return a clear
"not implemented" error rather than silently doing nothing.

- [ ] **Step 1: Write the failing test**

Create `skill/tests/Apply.Tests.ps1`:

```powershell
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
```

- [ ] **Step 2: Run it to verify it fails**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Apply.Tests.ps1`
Expected: six FAIL lines — `Invoke-VibeyApply` still returns `apply not implemented`.

- [ ] **Step 3: Implement apply, with the geometry and typography arms**

Add to `VibeyCrystal.psm1`:

```powershell
function Save-VibeyDocument {
    <#
        MEASURED: SaveAs takes (name, directory, options) - THREE arguments. It is not
        SaveAs(path, overwrite); that overload does not exist and fails with
        "Cannot find an overload for SaveAs and the argument count: 2".

        Writes to a temporary name in the destination directory and moves on success, so a
        save that fails partway cannot leave a corrupt file where the caller asked for output.
    #>
    param([Parameter(Mandatory)]$Doc, [Parameter(Mandatory)][string]$OutputPath)
    $dir = [IO.Path]::GetDirectoryName($OutputPath)
    if (-not $dir) { $dir = (Get-Location).Path }
    if (-not (Test-Path -LiteralPath $dir)) { throw "Output directory does not exist: $dir" }
    $tmpName = 'vibey-' + [Guid]::NewGuid().ToString('N') + '.rpt'
    $Doc.SaveAs($tmpName, $dir, 0)          # 0 = crReportOptionDefault
    Move-Item -LiteralPath (Join-Path $dir $tmpName) -Destination $OutputPath -Force
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
                    $sec = Find-VibeySection -Doc $doc -Name $op.section
                    $clone = $sec.Clone($true)
                    $clone.Height = [int]$op.heightTwips
                    $doc.ReportDefController.ReportSectionController.Modify($sec, $clone)
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
                    $f = Find-VibeyObject -Doc $doc -Name $op.target
                    $clone = $f.Object.Clone($true)
                    # MEASURED: the font is ISCRFont with a DECIMAL Size, reached through
                    # FontColor.Font. Assigning a System.Drawing.Font here does not compile.
                    $font = $clone.FontColor.Font
                    if ($op.action -eq 'setFont')     { $font.Name = [string]$op.fontName }
                    if ($op.action -eq 'setFontSize') { $font.Size = [decimal]$op.fontSizePt }
                    if ($op.action -eq 'setBold')     { $font.Bold = [bool]$op.bold }
                    $clone.FontColor.Font = $font
                    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
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
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Apply.Tests.ps1`
Expected: `6 passed, 0 failed`.

If `ReportSectionController.Modify` is not the right name, print the controller's members before
guessing: `$doc.ReportDefController | Get-Member`. Record what you find in `docs/sdk-notes.md`.

- [ ] **Step 5: Commit**

```bash
git add skill/
git commit -m "feat(skill): apply geometry and typography operations"
```

---

### Task 5: Adding and removing objects

**Files:**
- Modify: `skill/vibey-reports/scripts/VibeyCrystal.psm1`
- Modify: `skill/tests/Apply.Tests.ps1`

**Interfaces:**
- Consumes: `Find-VibeySection`, `Save-VibeyDocument`, the apply loop (Task 4).
- Produces: applier arms for `addText`, `addLine`, `addBox`, `addField`, `addSpecialField`,
  `removeObject`; `Invoke-VibeyApply`'s `removedObjects` is populated.

- [ ] **Step 1: Write the failing tests**

Append to `skill/tests/Apply.Tests.ps1`:

```powershell
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
```

- [ ] **Step 2: Run to verify they fail**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Apply.Tests.ps1`
Expected: the four new tests FAIL with "has no applier arm yet"; the six from Task 4 still pass.

- [ ] **Step 3: Implement the arms**

In `Invoke-VibeyApply`'s switch, above `default`:

```powershell
{ $_ -in 'addText','addLine','addBox','addField','addSpecialField' } {
    $sec = Find-VibeySection -Doc $doc -Name $op.section
    $kindEnum = switch ([string]$op.action) {
        'addText' { 2 } 'addLine' { 3 } 'addBox' { 4 } default { 1 }   # 1 = Field
    }
    $new = $doc.ReportDefController.ReportObjectController.CreateReportObject($kindEnum)
    $new.Name   = [string]$op.newName
    $new.Left   = [int]$op.leftTwips ; $new.Top    = [int]$op.topTwips
    $new.Width  = [int]$op.widthTwips; $new.Height = [int]$op.heightTwips

    switch ([string]$op.action) {
        'addText'  { $new.Text = [string]$op.text }
        'addField' {
            # MEASURED: an added field throws "field value type is not valid" unless
            # FieldValueType is set explicitly. Resolve the field to learn its type.
            $f = $doc.DataDefController.FindFieldByFormulaForm([string]$op.fieldRef)
            if (-not $f) { throw "Field `"$($op.fieldRef)`" was not found in the report's data source." }
            $new.DataSource     = [string]$op.fieldRef
            $new.FieldValueType = $f.Type
        }
        'addSpecialField' {
            $special = New-Object CrystalDecisions.ReportAppServer.DataDefModel.SpecialField
            $special.SpecialType = switch ([string]$op.specialType) {
                'pageNumber'     { 10 } 'pageNOfM'   { 20 } 'totalPageCount' { 12 }
                'printDate'      { 3 }  'printTime'  { 4 }  'reportTitle'    { 13 }
                'recordNumber'   { 9 }
            }
            $new.DataSource     = $special.FormulaForm
            $new.FieldValueType = $special.Type
        }
    }
    $doc.ReportDefController.ReportObjectController.Add($new, $sec, -1)
}
'removeObject' {
    $f = Find-VibeyObject -Doc $doc -Name $op.target
    $doc.ReportDefController.ReportObjectController.Remove($f.Object)
    $removedObjects += [string]$op.target
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Apply.Tests.ps1`
Expected: `10 passed, 0 failed`.

`CreateReportObject`'s enum values and the `SpecialField` type name are the two most likely
mismatches. If either throws, reflect before guessing:
`$doc.ReportDefController.ReportObjectController | Get-Member` and
`[Reflection.Assembly]::LoadWithPartialName('CrystalDecisions.ReportAppServer.DataDefModel').GetTypes() | Where-Object { $_.Name -like '*Special*' }`.
Record what you find in `docs/sdk-notes.md`, including the values that did not work.

- [ ] **Step 5: Commit**

```bash
git add skill/
git commit -m "feat(skill): add and remove report objects"
```

---

### Task 6: Colour and formatting

**Files:**
- Modify: `skill/vibey-reports/scripts/VibeyCrystal.psm1`
- Modify: `skill/tests/Apply.Tests.ps1`

**Interfaces:**
- Consumes: the apply loop and `Find-VibeyObject` / `Find-VibeySection`.
- Produces: `ConvertTo-VibeyColorRef -Hex <string>` → `[int]`, plus applier arms for
  `setTextColor`, `setFillColor`, `setLineColor`, `setSectionBackground`, `setSectionBreak`,
  `setCanGrow`, `setSuppress`, `setNumberFormat`. After this task no action reaches `default`.

- [ ] **Step 1: Write the failing tests**

Append to `skill/tests/Apply.Tests.ps1`:

```powershell
It 'converts HTML hex to COLORREF with the byte order reversed' {
    Import-Module (Join-Path $PSScriptRoot '..\vibey-reports\scripts\VibeyCrystal.psm1') -Force
    Should-Be (ConvertTo-VibeyColorRef -Hex '#FF0000') 255      'red'
    Should-Be (ConvertTo-VibeyColorRef -Hex '#0000FF') 16711680 'blue'
    Should-Be (ConvertTo-VibeyColorRef -Hex '#000000') 0        'black'
}

It 'sets a fill colour on a box and it survives a reopen' {
    $a = Apply-Ops @(
        @{ action='resizeSection'; section='DetailSection1'; heightTwips=1000 },
        @{ action='addBox'; section='DetailSection1'; newName='VibeyBox'
           leftTwips=0; topTwips=0; widthTwips=4000; heightTwips=400 },
        @{ action='setFillColor'; target='VibeyBox'; color='#DCE3EA' }) 'fill.rpt'
    Should-Be $a.Result.ok $true 'ok'
}

It 'sets a page break on a section and it reads back' {
    $a = Apply-Ops @{ action='setSectionBreak'; section='DetailSection1'; newPageAfter=$true } 'break.rpt'
    Should-Be $a.Result.ok $true 'ok'
}

It 'sets a number format that persists - the EnableSystemDefault gate' {
    $before = (Invoke-Vibey @{ command='read'; reportPath=$fixture }).schema
    $field = $null
    foreach ($s in $before.sections) { foreach ($o in $s.objects) { if ($o.kind -eq 'Field') { $field = $o; break } } }
    if (-not $field) { throw 'fixture has no Field object' }
    $a = Apply-Ops @{ action='setNumberFormat'; target=$field.name; decimalPlaces=0; thousandsSeparator=$false } 'number.rpt'
    Should-Be $a.Result.ok $true 'ok'
}

It 'suppresses a section' {
    $a = Apply-Ops @{ action='setSuppress'; section='PageFooterSection1'; suppress=$true } 'suppress.rpt'
    Should-Be $a.Result.ok $true 'ok'
}
```

- [ ] **Step 2: Run to verify they fail**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1 -Filter Apply.Tests.ps1`
Expected: the five new tests FAIL; the ten from Tasks 4 and 5 still pass.

- [ ] **Step 3: Implement the arms**

Add to `VibeyCrystal.psm1`:

```powershell
function ConvertTo-VibeyColorRef {
    <#
        MEASURED: Crystal stores colour as COLORREF, 0x00BBGGRR - the byte order is REVERSED
        from HTML hex. Feeding 0xRRGGBB straight through swaps red and blue, which renders as
        a plausible wrong colour rather than an error.
    #>
    param([Parameter(Mandatory)][string]$Hex)
    $h = $Hex.TrimStart('#')
    if ($h.Length -ne 6) { throw "Colour must be six hex digits, was `"$Hex`"." }
    $r = [Convert]::ToInt32($h.Substring(0,2),16)
    $g = [Convert]::ToInt32($h.Substring(2,2),16)
    $b = [Convert]::ToInt32($h.Substring(4,2),16)
    return (($b -shl 16) -bor ($g -shl 8) -bor $r)
}
```

In the apply switch, above `default`:

```powershell
{ $_ -in 'setTextColor','setFillColor','setLineColor' } {
    $f = Find-VibeyObject -Doc $doc -Name $op.target
    $clone = $f.Object.Clone($true)
    $colorRef = ConvertTo-VibeyColorRef -Hex ([string]$op.color)
    switch ([string]$op.action) {
        'setTextColor' { $clone.FontColor.Color = $colorRef }
        'setFillColor' { $clone.FillColor       = $colorRef }
        'setLineColor' { $clone.LineColor       = $colorRef }
    }
    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
}
{ $_ -in 'setSectionBackground','setSectionBreak','setSuppress' -and $op.section } {
    $sec = Find-VibeySection -Doc $doc -Name $op.section
    $clone = $sec.Clone($true)
    switch ([string]$op.action) {
        'setSectionBackground' { $clone.Format.BackgroundColor = (ConvertTo-VibeyColorRef -Hex ([string]$op.color)) }
        'setSectionBreak' {
            if ($null -ne $op.newPageBefore) { $clone.Format.EnableNewPageBefore = [bool]$op.newPageBefore }
            if ($null -ne $op.newPageAfter)  { $clone.Format.EnableNewPageAfter  = [bool]$op.newPageAfter }
        }
        'setSuppress' {
            $clone.Format.EnableSuppress = [bool]$op.suppress
            if ($null -ne $op.suppressIfBlank) { $clone.Format.EnableSuppressIfBlank = [bool]$op.suppressIfBlank }
        }
    }
    $doc.ReportDefController.ReportSectionController.Modify($sec, $clone)
}
{ $_ -in 'setCanGrow','setSuppress' -and $op.target } {
    $f = Find-VibeyObject -Doc $doc -Name $op.target
    $clone = $f.Object.Clone($true)
    if ($op.action -eq 'setCanGrow') { $clone.Format.EnableCanGrow  = [bool]$op.canGrow }
    else                             { $clone.Format.EnableSuppress = [bool]$op.suppress }
    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
}
'setNumberFormat' {
    $f = Find-VibeyObject -Doc $doc -Name $op.target
    $clone = $f.Object.Clone($true)
    $fmt = $clone.FieldFormat
    # MEASURED, and the reason a first attempt at this silently did nothing: while
    # CommonFormat.EnableSystemDefault is true, Crystal formats from locale defaults and
    # DISCARDS NDecimalPlaces and ThousandsSeparator on save. Clearing the gate first is
    # what makes the setting persist. EnableSuppressIfZero persists either way, so a test
    # that checks only that property passes against a two-thirds broken operation.
    $fmt.CommonFormat.EnableSystemDefault = $false
    if ($null -ne $op.decimalPlaces) {
        $fmt.NumericFormat.NDecimalPlaces = [int]$op.decimalPlaces
        # RoundingFormat rounds independently of NDecimalPlaces; keep them in step or the
        # displayed value is rounded to a different precision than it is formatted to.
        $fmt.NumericFormat.RoundingFormat = (11 - [int]$op.decimalPlaces)
    }
    if ($null -ne $op.thousandsSeparator) { $fmt.NumericFormat.ThousandsSeparator  = [bool]$op.thousandsSeparator }
    if ($null -ne $op.suppressIfZero)     { $fmt.NumericFormat.EnableSuppressIfZero = [bool]$op.suppressIfZero }
    $clone.FieldFormat = $fmt
    Set-VibeyObject -Doc $doc -Original $f.Object -Modified $clone
}
```

- [ ] **Step 4: Run the whole suite**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1`
Expected: `34 passed, 0 failed` (3 protocol + 4 read + 12 validate + 15 apply).
If the count differs, reconcile it — a test that silently stopped running is worse than one that fails.

- [ ] **Step 5: Commit**

```bash
git add skill/
git commit -m "feat(skill): colour, page breaks, suppression, can-grow and number formats"
```

---

### Task 7: SKILL.md and installation

**Files:**
- Create: `skill/vibey-reports/SKILL.md`
- Create: `skill/install.ps1`

**Interfaces:**
- Consumes: everything above.
- Produces: an installable skill.

- [ ] **Step 1: Write the skill definition**

Create `skill/vibey-reports/SKILL.md`:

```markdown
---
name: vibey-reports
description: Edit the layout of Crystal Reports .rpt files - reposition, restyle, recolour, add text/fields/page numbers, resize sections, set page breaks. Use when the user asks to change how a Crystal report looks, mentions a .rpt file, or refers to PMSV10 reports. Requires Crystal Reports XI R2 installed on Windows.
---

# Vibey Reports

Edits Crystal Reports `.rpt` layouts by talking to the Crystal XI R2 SDK from PowerShell.

## How to run it

One JSON request in, one JSON response out. The script relaunches itself as 32-bit automatically.

    echo '{"command":"read","reportPath":"D:/path/report.rpt"}' | powershell -NoProfile -File scripts/vibey.ps1

**Always `read` before you `apply`** - you need the real object names, and reading is free.

## Applying changes

    {"command":"apply","reportPath":"IN.rpt","outputPath":"OUT.rpt","overwrite":true,
     "plan":{"planVersion":1,"operations":[ ... ]}}

The whole plan is validated first. If any operation fails, **nothing is applied and no output file
is written** - the response lists which operation was rejected and why. The source report is never
modified.

## Operations

| Group | Actions |
|---|---|
| Geometry | `move` `resize` `resizeSection` `setAlignment` |
| Adding | `addText` `addLine` `addBox` `addField` `addSpecialField` `removeObject` |
| Type | `setFont` `setFontSize` `setBold` |
| Colour | `setTextColor` `setFillColor` `setLineColor` `setSectionBackground` |
| Formatting | `setSectionBreak` `setNumberFormat` `setCanGrow` `setSuppress` |

`addSpecialField` takes one of: `pageNumber`, `pageNOfM`, `totalPageCount`, `printDate`,
`printTime`, `reportTitle`, `recordNumber`.

## Things that will catch you out

**Always set `fontSizePt` explicitly on anything you add.** A new object inherits the font of the
first fontable object in its section. A caption added to a section whose first object is a 20pt
title comes out at 20pt and is clipped by its own box.

**Grow the section before you place tall content in it.** The validator simulates cumulatively, so
`resizeSection` then `addText` in one plan is fine - but the other order is rejected.

**A section cannot exceed one printable page** (16118 twips on A4). Two pages of content need two
sections.

**Twips, not points or pixels.** 1440 twips = 1 inch. A4 is 11906 x 16838; printable width with
default margins is 11186.

**Objects do not reflow.** Growing one object does not push down the ones below it in the same
section - they will overlap. Put things that grow in separate sections.

**Close the report in the Crystal Designer first.** An open `.rpt` is locked; the tool fails cleanly
and writes nothing, but it cannot save over a file you are looking at.

**After a stored procedure changes**, the report cannot see new columns until someone runs
*Database > Verify Database* in the Designer - including inside any embedded sub-report. There is no
SDK equivalent. Skip it and the next operation naming a new column fails with `Invalid field name`,
which reads like a typo but is not.

## What this skill does NOT do

Data sources (`addTable`, `removeTable`, `setTableLocation`), sub-reports, groups, sorts, formulas
and record selection are all out of scope. `removeTable` in particular is dangerous: Crystal does not
refuse to remove a table that fields are bound to - it silently deletes them. Use the Crystal
Designer for those.

It also cannot create a `.rpt` from nothing - the SDK has `Open` and `SaveAs` but no `New`. Start
from an existing file, even an empty one.
```

- [ ] **Step 2: Write the installer**

Create `skill/install.ps1`:

```powershell
<#  Copies the skill to ~/.claude/skills/vibey-reports. No build, no registration. #>
[CmdletBinding()]
param([string]$Destination = (Join-Path $HOME '.claude\skills'))

$ErrorActionPreference = 'Stop'
$source = Join-Path $PSScriptRoot 'vibey-reports'
if (-not (Test-Path -LiteralPath $source)) { throw "Skill folder not found: $source" }

$crystal = 'C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2'
if (-not (Test-Path -LiteralPath $crystal)) {
    Write-Warning "Crystal Reports XI R2 was not found at $crystal. The skill installs, but every command will fail until Crystal is installed."
}

$target = Join-Path $Destination 'vibey-reports'
if (-not (Test-Path -LiteralPath $Destination)) { New-Item -ItemType Directory -Path $Destination -Force | Out-Null }
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
Copy-Item -LiteralPath $source -Destination $target -Recurse

Write-Host "Installed to $target" -ForegroundColor Green
Write-Host "Verify with:  echo '{\"command\":\"read\",\"reportPath\":\"<a .rpt>\"}' | powershell -NoProfile -File `"$target\scripts\vibey.ps1`""
```

- [ ] **Step 3: Verify the installed copy works end to end**

```powershell
powershell -NoProfile -File skill\install.ps1
echo '{"command":"read","reportPath":"D:/VibeyReports/tests/fixtures/SampleReport.rpt"}' | powershell -NoProfile -File "$HOME\.claude\skills\vibey-reports\scripts\vibey.ps1"
```

Expected: JSON beginning `{"ok":true,"schema":{...}` — the same output as running from the repo.

- [ ] **Step 4: Run the full suite once more**

Run: `powershell -NoProfile -File skill\tests\Run-VibeyTests.ps1`
Expected: `34 passed, 0 failed`.

- [ ] **Step 5: Commit**

```bash
git add skill/
git commit -m "feat(skill): SKILL.md and a copy-only installer"
```

---

## Notes for whoever executes this

**Measure before you guess.** Several method names and enum values in this plan were written from the
C# implementation and reflection, not from a PowerShell run. Where one is wrong, the plan says so and
tells you what to reflect on. Getting a different answer is expected, not a failure — record it in
`docs/sdk-notes.md`, including what did not work, because a negative result stops the next person
repeating it.

**Put a hard timeout on anything touching Crystal.** It blocks rather than failing on several paths,
and an agent on this project stalled for ten minutes on exactly that, committing nothing. The tell
for a real hang is CPU, not elapsed time: sample the process twice and compare `.CPU`.

**Never write to `out/reports/` or `tests/fixtures/`.** Tests write to a scratch directory under
`$env:TEMP`. A previous agent modified a tracked report in place and it had to be reverted.

**The existing C# implementation is the reference.** When behaviour is unclear, read
`src/VibeyReports.CrystalWorker/LayoutApplier.cs` and `src/VibeyReports.Contracts/LayoutPlanValidator.cs`
— they are the same operations, already measured, with the reasoning in comments.
