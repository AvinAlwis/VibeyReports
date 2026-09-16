<#
    Renders an RN_SP = '1' report the way Report Navigator does, and reports the page
    count. The only way to see what the viewer will actually produce.

        push-render.ps1 -Rpt <path> -Cycle <id> -Emps '000018,000020' [-Text] [-Out <path>]

    WHY THIS EXISTS
    ---------------
    Neither `render` nor the Crystal Designer can preview one of these reports: the .rpt
    declares parameters with no saved values, so Export throws "Missing parameter values."
    VibeyReports deliberately never supplies parameter values.

    Report Navigator does not supply them either. Under RN_SP = '1' it PUSHES data:
    SPViewer.BindReport runs each stored procedure itself and hands the resulting
    DataTable to SetDataSource, on the main report and on every sub-report. This script
    does exactly that, so what comes out is what the viewer shows - including page count,
    which is how the blank-first-page defect was found and fixed.

    TWO TRAPS, both of which cost time the first time round:
      * Table.Location carries the result-set suffix ("sp_x;1") that SqlClient reads as a
        numbered-procedure reference. It has to be stripped.
      * Table.Name is the ALIAS, not the procedure. Location is the procedure.

    -Text exports plain text instead of PDF, which is the quickest way to read rendered
    VALUES back (it is plain UTF-8 despite appearances). Use it to check a number is
    formatted as intended; use the default PDF for page counts.
#>
param(
    [Parameter(Mandatory = $true)][string]$Rpt,
    [Parameter(Mandatory = $true)][int]$Cycle,
    [Parameter(Mandatory = $true)][string]$Emps,
    [switch]$Text,
    [string]$Out
)

$ErrorActionPreference = 'Stop'

# The Crystal assemblies are x86 and will not load in a 64-bit host.
if ([Environment]::Is64BitProcess) {
    $x86 = Join-Path $env:WINDIR 'SysWOW64\WindowsPowerShell\v1.0\powershell.exe'
    $argv = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath,
              '-Rpt', $Rpt, '-Cycle', $Cycle, '-Emps', $Emps)
    if ($Text) { $argv += '-Text' }
    if ($Out)  { $argv += @('-Out', $Out) }
    & $x86 @argv
    exit $LASTEXITCODE
}

$gac = 'C:\Windows\assembly\GAC_MSIL'
Add-Type -Path "$gac\CrystalDecisions.CrystalReports.Engine\11.5.3700.0__692fbea5521e1304\CrystalDecisions.CrystalReports.Engine.dll"
Add-Type -Path "$gac\CrystalDecisions.Shared\11.5.3700.0__692fbea5521e1304\CrystalDecisions.Shared.dll"

if (-not $Out) {
    $Out = Join-Path $env:TEMP ([IO.Path]::GetFileNameWithoutExtension($Rpt) + $(if ($Text) { '.txt' } else { '.pdf' }))
}

# Credentials come from the pms-sql skill's connections.json, which lives OUTSIDE the
# repo. Nothing here writes a connection string anywhere.
$cfg = (Get-Content "$env:USERPROFILE\.claude\skills\pms-sql\connections.json" -Raw | ConvertFrom-Json)
$t   = if ($cfg.targets) { $cfg.targets.dev } else { $cfg.dev }
$csb = New-Object System.Data.SqlClient.SqlConnectionStringBuilder
$csb['Data Source']     = $t.server
$csb['Initial Catalog'] = $t.database
$csb['User ID']         = $t.user
$csb['Password']        = $t.password
$csb['TrustServerCertificate'] = $true

function Get-Table([string]$proc) {
    $name = $proc -replace ';\d+$', ''          # "sp_x;1" -> "sp_x"
    $c = New-Object System.Data.SqlClient.SqlConnection $csb.ConnectionString
    $c.Open()
    $cmd = $c.CreateCommand()
    $cmd.CommandType = [System.Data.CommandType]::StoredProcedure
    $cmd.CommandText = $name
    # The shared five. OraBuildReport binds every registered parameter to every
    # procedure it runs, so all of them take all five.
    [void]$cmd.Parameters.AddWithValue('@performance_cycle_id',  $Cycle)
    [void]$cmd.Parameters.AddWithValue('@business_unit_level',   '-')
    [void]$cmd.Parameters.AddWithValue('@business_unit',         '-')
    [void]$cmd.Parameters.AddWithValue('@emp_number_list',       $Emps)
    [void]$cmd.Parameters.AddWithValue('@first_level_appraiser', '-')
    $da = New-Object System.Data.SqlClient.SqlDataAdapter $cmd
    $dt = New-Object System.Data.DataTable
    [void]$da.Fill($dt)
    $c.Close()
    return ,$dt
}

$doc = New-Object CrystalDecisions.CrystalReports.Engine.ReportDocument
$doc.Load($Rpt)

$mainSp = $doc.Database.Tables[0].Location
$main   = Get-Table $mainSp
Write-Output ("main {0}: {1} rows x {2} cols" -f $mainSp, $main.Rows.Count, $main.Columns.Count)
$doc.Database.Tables[0].SetDataSource($main)

foreach ($sub in $doc.Subreports) {
    $spName = $sub.Database.Tables[0].Location
    $st = Get-Table $spName
    Write-Output ("  sub '{0}' <- {1}: {2} rows x {3} cols" -f $sub.Name, $spName, $st.Rows.Count, $st.Columns.Count)
    # An empty DataTable with no COLUMNS is rejected by SetDataSource ("The data source
    # object is invalid"); a sub-report with no data simply renders blank instead.
    if ($st.Columns.Count -gt 0) { $sub.Database.Tables[0].SetDataSource($st) }
    else { Write-Output '    (no columns returned - not pushed)' }
}

if (Test-Path $Out) { Remove-Item $Out -Force }
$doc.ExportToDisk($(if ($Text) { 'Text' } else { 'PortableDocFormat' }), $Out)
$doc.Close()

if ($Text) {
    Write-Output ("TEXT: {0}" -f $Out)
} else {
    $bytes = [IO.File]::ReadAllBytes($Out)
    # "/Type /Page" but not "/Type /Pages" - the page-tree node would double-count.
    $pages = ([regex]::Matches([Text.Encoding]::ASCII.GetString($bytes), '/Type\s*/Page[^s]')).Count
    Write-Output ("PDF PAGES: {0}   ({1} bytes)  {2}" -f $pages, $bytes.Length, $Out)
}
