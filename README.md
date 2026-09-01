# Vibey Reports

AI-assisted layout redesign for Crystal Reports XI R2 `.rpt` files, driven from Claude Code
over MCP. Reads a report's layout, applies a validated layout plan, saves a new `.rpt`, and
renders a preview image so Claude can review its own work.

## Why the odd shape

Crystal XI R2's in-process RAS SDK is .NET 2.0, x86 and COM-bound. It lives in
`VibeyReports.CrystalWorker` (net48, x86) and nowhere else. `VibeyReports.Mcp` (net10.0)
talks to it as a child process over JSON on stdin/stdout.

**The Crystal assemblies must come from
`C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2` (version 11.5).**
The `CrystalReports.*` 13.x NuGet packages will open XI R2 files but save them in a newer
format that XI R2 and PeoplesHR cannot open. They must not appear anywhere in this
repository's dependencies.

**Before changing anything in `VibeyReports.CrystalWorker`, read
[docs/sdk-notes.md](docs/sdk-notes.md).** The RAS SDK's real behaviour diverges from the
original implementation plan (`docs/superpowers/plans/2026-08-31-vibey-reports.md`) in nine
measured ways — object model shapes, font types, encoding requirements, and more. The notes
file is authoritative wherever it and the plan disagree; the full per-task detail lives in
`docs/supplements/`.

## Build and publish

```powershell
.\publish.ps1
```

Invoke it as `.\publish.ps1` or `& .\publish.ps1` from a PowerShell prompt. Avoid nesting a
`powershell.exe -File .\publish.ps1` child process inside an already-running PowerShell
session — `$PSScriptRoot` can come back empty there, which the script now guards against,
but a plain in-session invocation avoids the problem entirely.

This publishes the two executables to **separate directories**, deliberately:

```
dist\VibeyReports.Mcp.exe
dist\worker\VibeyReports.CrystalWorker.exe
```

Both projects reference `VibeyReports.Contracts.dll`. Publishing them into the same folder
lets whichever one publishes second overwrite files the other needs, which fails in a
confusing way. `WorkerLocator` probes `AppContext.BaseDirectory/worker/` specifically so this
split layout works with no environment variable at run time.

### Verifying the worker is genuinely x86

The Business Objects COM servers are 32-bit only, so this matters. After publishing:

```powershell
$exe = "D:\VibeyReports\dist\worker\VibeyReports.CrystalWorker.exe"
$bytes = [System.IO.File]::ReadAllBytes($exe)
$peOffset = [BitConverter]::ToInt32($bytes, 0x3C)
"0x{0:X4}" -f [BitConverter]::ToUInt16($bytes, $peOffset + 4)   # expect 0x014C (I386)
[System.Reflection.AssemblyName]::GetAssemblyName($exe).ProcessorArchitecture  # expect X86
```

## Registration with Claude Code

**Not yet applied.** Registering an MCP server means editing `C:\Users\avin.a\.claude.json`,
which is outside this repository and changes global Claude Code tooling for every project —
that is a decision for you to make explicitly, not something Task 11 does on its own.

To register `vibey-reports` once you're ready, add this entry to the root `mcpServers` object
in `~/.claude.json` (the `VIBEY_WORKER_PATH` override is set as belt-and-braces even though the
`dist\worker\` layout above already satisfies `WorkerLocator`'s automatic probe):

```json
"vibey-reports": {
  "command": "D:\\VibeyReports\\dist\\VibeyReports.Mcp.exe",
  "args": [],
  "env": {
    "VIBEY_WORKER_PATH": "D:\\VibeyReports\\dist\\worker\\VibeyReports.CrystalWorker.exe"
  }
}
```

Apply it with a script rather than by hand so the rest of the file is untouched:

```powershell
$path = "$env:USERPROFILE\.claude.json"
Copy-Item $path "$path.bak" -Force
$cfg = Get-Content $path -Raw | ConvertFrom-Json
$entry = [PSCustomObject]@{
    command = 'D:\VibeyReports\dist\VibeyReports.Mcp.exe'
    args    = @()
    env     = [PSCustomObject]@{ VIBEY_WORKER_PATH = 'D:\VibeyReports\dist\worker\VibeyReports.CrystalWorker.exe' }
}
$cfg.mcpServers | Add-Member -NotePropertyName 'vibey-reports' -NotePropertyValue $entry -Force
$cfg | ConvertTo-Json -Depth 30 | Set-Content $path -Encoding utf8
Write-Host "Registered. Backup at $path.bak"
```

After applying it, restart Claude Code and confirm `read_report`, `apply_layout` and
`preview_report` appear under `vibey-reports`. If the server fails to start, run the exe
directly — it should sit waiting on stdin rather than exiting immediately:

```bash
./dist/VibeyReports.Mcp.exe < /dev/null
```

(A clean, silent start with no stderr is itself evidence the `worker/` probe found the
worker; a failed probe writes to stderr and exits 1.)

## Tools

| Tool | Purpose |
|---|---|
| `read_report` | `.rpt` → layout JSON (twips, sections, objects, fonts) |
| `apply_layout` | layout plan → new `.rpt` |
| `preview_report` | `.rpt` → rendered PNG of page 1 |

## Supported operations

`move`, `resize`, `setFont`, `setFontSize`, `setBold`, `setAlignment`, `addText`, `addLine`,
`addBox`, `resizeSection`, `addField`, `removeObject`, `setTextColor`, `setFillColor`,
`setLineColor`, `setSectionBackground`.

Layout only. Database connections, SQL, formulas, parameters, record selection and grouping
cannot be changed; `LayoutPlanValidator` rejects any attempt. `addField`'s `fieldRef` is the
one operation with its own security boundary: it must exactly match a `formulaForm` already
exposed by the report's own data source (see `ReportSchema.AvailableFields`) — it cannot be
used to add new tables, formulas, or connections. `removeObject` permanently removes an object
from the generated report — including a bound field, which deletes that data from the
output — though the source `.rpt` is never modified. The four colour operations
(`setTextColor`, `setFillColor`, `setLineColor`, `setSectionBackground`) take a `color` of the
form `#RRGGBB`.

## Tests

```bash
dotnet test
```

`VibeyReports.CrystalWorker.Tests` and `VibeyReports.Mcp.Tests` need Crystal Reports XI R2
installed. `VibeyReports.Contracts.Tests` runs anywhere.

As of the final review fix wave: `VibeyReports.Contracts.Tests` 45, `VibeyReports.CrystalWorker.Tests` 60,
`VibeyReports.Mcp.Tests` 24 — all passing (0 failed).

## MVP verification

Task 11's automation deliberately stopped short of this step. Driving `read_report` →
`LayoutPlan` → `apply_layout` → `preview_report` end to end requires a live Claude Code
session with `vibey-reports` registered (see above, not yet applied), and the final
checkpoint below is a human action that cannot be simulated. Nothing in this section has
been ticked or exercised by Task 11 — it is left for you to run and fill in.

<!-- Fill in after walking the MVP loop end to end from a Claude Code session. -->
- [ ] Generated `.rpt` opens in Crystal Reports XI R2 — date, report used, result.
