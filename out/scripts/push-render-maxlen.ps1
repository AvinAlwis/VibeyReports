<#
    Renders the Goal Alignment & Cascading report with every field at the longest value the
    database allows, to see how the layout copes. Nothing is written to the database.

        push-render-maxlen.ps1 -Rpt <PMSV10_GoalAlignCascade.rpt> [-Out <path.pdf>] [-NoSpaces]

    HOW
    ---
    The real sp_perf_goal_align_sheet is run once, for cycle 10386, only to get its exact result
    SCHEMA (column names, order and types). Its rows are discarded and replaced with synthetic
    ones, then pushed through SetDataSource exactly as push-render.ps1 and Report Navigator do.

    LENGTHS - measured from INFORMATION_SCHEMA on 2026-09-17
    ---------------------------------------------------------
      emp_display_number  nvarchar(25)     HS_HR_EMPLOYEE
      employee_name       nvarchar(600)    HS_HR_EMPLOYEE.EMP_DISPLAY_NAME (also reporting_manager)
      designation         nvarchar(120)    HS_HR_DESIGNATION.DSG_NAME
      department          nvarchar(70)     HS_HR_COMPANY_HIERARCHY.HIE_NAME
      cycle_name          nvarchar(200)    perf_performance_cycle
      cycle_status        nvarchar(20)     perf_performance_cycle
      evaluation_period   'yyyy-mm-dd - yyyy-mm-dd' - always 23
      goal_name           nvarchar(500)    perf_cp_goal.goal_title
      category            nvarchar(20)     perf_cp_goal.goal_category
      cascading hierarchy 6 x nvarchar(300) perf_objective.obj_title joined by ' -> ' = 1820.
                          6 is the deepest objective chain on this database; the procedure
                          itself allows up to 100.
      goal_id             bigint max;  total_goals / aligned / not_aligned  int max
      alignment_status    'Not Aligned'    the longer of the two values

    Text is made of real words so Crystal can wrap it the way it would wrap real data.
    -NoSpaces uses one unbroken run of 'W' instead - the widest glyph and no break points - which
    is the pathological case.
#>
param(
    [Parameter(Mandatory = $true)][string]$Rpt,
    [string]$Out,
    [switch]$NoSpaces
)

$ErrorActionPreference = 'Stop'

if ([Environment]::Is64BitProcess) {
    $x86 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath, '-Rpt', $Rpt)
    if ($Out) { $argv += @('-Out', $Out) }
    if ($NoSpaces) { $argv += '-NoSpaces' }
    & $x86 @argv
    exit $LASTEXITCODE
}

$gac = 'C:\Windows\assembly\GAC_MSIL'
Add-Type -Path "$gac\CrystalDecisions.CrystalReports.Engine\11.5.3700.0__692fbea5521e1304\CrystalDecisions.CrystalReports.Engine.dll"
Add-Type -Path "$gac\CrystalDecisions.Shared\11.5.3700.0__692fbea5521e1304\CrystalDecisions.Shared.dll"

if (-not $Out) { $Out = Join-Path $env:TEMP 'GoalAlign-maxlen.pdf' }

# Credentials come from the pms-sql skill's connections.json, outside the repo.
$cfg = (Get-Content "$env:USERPROFILE\.claude\skills\pms-sql\connections.json" -Raw | ConvertFrom-Json)
$t   = if ($cfg.targets) { $cfg.targets.dev } else { $cfg.dev }
$csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$csb['Data Source'] = $t.server; $csb['Initial Catalog'] = $t.database
$csb['User ID'] = $t.user;       $csb['Password'] = $t.password
$csb['TrustServerCertificate'] = $true

function Get-Table([string]$proc, [int]$cycle, [string]$emps) {
    $c = New-Object System.Data.SqlClient.SqlConnection $csb.ConnectionString
    $c.Open()
    $cmd = $c.CreateCommand()
    $cmd.CommandType = [System.Data.CommandType]::StoredProcedure
    $cmd.CommandText = $proc -replace ';\d+$', ''
    [void]$cmd.Parameters.AddWithValue('@performance_cycle_id',  $cycle)
    [void]$cmd.Parameters.AddWithValue('@business_unit_level',   '-')
    [void]$cmd.Parameters.AddWithValue('@business_unit',         '-')
    [void]$cmd.Parameters.AddWithValue('@emp_number_list',       $emps)
    [void]$cmd.Parameters.AddWithValue('@first_level_appraiser', '-')
    $dt = New-Object System.Data.DataTable
    [void](New-Object System.Data.SqlClient.SqlDataAdapter $cmd).Fill($dt)
    $c.Close()
    return ,$dt
}

$words = ('Strategic alignment ensures every individual contribution supports measurable ' +
          'organisational outcomes across regional business units and cross functional teams ').Split(' ') |
         Where-Object { $_ }

# Exactly $len characters, starting with $tag so each field is identifiable on the page.
function Fill([string]$tag, [int]$len) {
    if ($NoSpaces) { return ($tag + ('W' * $len)).Substring(0, $len) }
    $sb = New-Object Text.StringBuilder $tag
    $i = 0
    while ($sb.Length -lt $len) { [void]$sb.Append(' ').Append($words[$i % $words.Count]); $i++ }
    return $sb.ToString().Substring(0, $len)
}

$hierarchy = ((1..6) | ForEach-Object { Fill "[L$_]" 300 }) -join ' -> '

$doc = New-Object CrystalDecisions.CrystalReports.Engine.ReportDocument
$doc.Load($Rpt)

$mainSp = $doc.Database.Tables[0].Location
$dt = Get-Table $mainSp 10386 '000018'
$dt.Clear()

$bigMax = [long]::MaxValue
$intMax = [int]::MaxValue
foreach ($emp in 1..2) {
    foreach ($g in 1..3) {
        $r = $dt.NewRow()
        $r['emp_number']            = "E$emp"
        $r['emp_display_number']    = Fill "EMP$emp" 25
        $r['employee_name']         = Fill "[Employee $emp 600]" 600
        $r['designation']           = Fill '[Designation 120]' 120
        $r['department']            = Fill '[Department 70]' 70
        $r['cycle_name']            = Fill '[Cycle 200]' 200
        $r['performance_cycle_id']  = $bigMax
        $r['evaluation_period']     = '2026-09-01 - 2026-12-31'
        $r['cycle_status']          = Fill '[Status20]' 20
        $r['reporting_manager']     = Fill '[Manager 600]' 600
        $r['total_goals']           = $intMax
        $r['aligned']               = $intMax
        $r['not_aligned']           = $intMax
        $r['goal_id']               = $bigMax - $g
        $r['goal_name']             = Fill "[Goal $g title 500]" 500
        $r['category']              = Fill '[Category20]' 20
        $r['cascading_objective_hierarchy'] = $hierarchy
        $r['alignment_status']      = 'Not Aligned'
        $dt.Rows.Add($r)
    }
}
Write-Output ("main {0}: {1} synthetic rows x {2} cols (schema from the live procedure)" -f $mainSp, $dt.Rows.Count, $dt.Columns.Count)
$doc.Database.Tables[0].SetDataSource($dt)

# The logo sub-report takes the real logo.
foreach ($sub in $doc.Subreports) {
    $st = Get-Table $sub.Database.Tables[0].Location 10386 '000018'
    if ($st.Columns.Count -gt 0) { $sub.Database.Tables[0].SetDataSource($st) }
}

if (Test-Path $Out) { Remove-Item $Out -Force }
$doc.ExportToDisk('PortableDocFormat', $Out)
$doc.Close()

$bytes = [IO.File]::ReadAllBytes($Out)
$pages = ([regex]::Matches([Text.Encoding]::ASCII.GetString($bytes), '/Type\s*/Page[^s]')).Count
Write-Output ("PDF PAGES: {0}   ({1} bytes)  {2}" -f $pages, $bytes.Length, $Out)
