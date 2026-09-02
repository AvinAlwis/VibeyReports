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
`setLineColor`, `setSectionBackground`, `addSubreport`, `setSubreportLink`, `removeTable`,
`addTable`, `setTableLocation`, `setSectionBreak`, `addSpecialField`, `setNumberFormat`,
`setCanGrow`, `setSuppress`, `addGroup`, `addSort` — twenty-eight in all (`LayoutActions.All`).

Layout, data-source bindings, grouping and sorting. SQL, formulas, parameters and record selection
cannot be changed; `LayoutPlanValidator` rejects any attempt. **The database is only ever read** — the
three table operations rewrite the report's own binding metadata and nothing else, and **no
operation accepts a username or password**, ever (plans are JSON files on disk; `addTable` clones
the connection of a table already in the report instead). Where a password is unavoidable —
`addTable` and `setTableLocation` only — it comes from the `VIBEY_DB_PASSWORD` environment
variable, never from the plan; see below.

`addField`'s `fieldRef` has its own security boundary: it must exactly match a `formulaForm`
already exposed by the report's own data source (see `ReportSchema.AvailableFields`).
`removeObject` permanently removes an object from the generated report — including a bound field,
which deletes that data from the output — though the source `.rpt` is never modified. The four
colour operations (`setTextColor`, `setFillColor`, `setLineColor`, `setSectionBackground`) take a
`color` of the form `#RRGGBB`.

`removeTable` is the answer to a report bound to two unrelated tables with no link between them,
which Crystal renders as a cartesian join. Its `target` is a table **alias**, not an object name.
Crystal itself will remove such a table *and silently delete every object bound to it* (measured —
see `docs/sdk-notes.md`), so the validator refuses while any bound object survives; remove those
objects earlier in the same plan and the removal is accepted. Removed aliases come back in the
response as `removedTables`.

`addGroup` is the only operation that creates a section. Crystal's band structure is otherwise
fixed through this SDK, so a page that has to repeat once per employee cannot live in the Report
Header (which prints once per report) — group the report on the employee's field and that content
becomes a Group Header. Crystal names the two sections it creates, from the grouped **field's**
name with every non-alphanumeric character removed:
`{sp_perf_ind_perf_overview;1.emp_number}` → `empnumberHeaderSection1` / `empnumberFooterSection1`
(measured; the underscores really are dropped). Because the rule is deterministic, a plan may
create a group and place objects into its new sections in one go. `read_report` reports each
group's real `headerSection`/`footerSection`, so nothing depends on reproducing the rule by hand.

`addSort` makes the record order a property of the report rather than a hope about the stored
procedure's `ORDER BY`. Only `ascending` and `descending` are supported. A group's order **is** a
sort in Crystal, so a grouped field is already sorted — `addGroup` takes its own `direction`, and a
second group or a second sort on the same field is rejected before anything is written. Groups and
sorts read back through `read_report` as `groups` and `sorts`.

### `addTable` / `setTableLocation` and `VIBEY_DB_PASSWORD`

These two operations — and only these two — **make Crystal connect to the database** to verify the
object. A saved Crystal connection carries a user name but never a password (measured), so the
password has to come from somewhere, and it is deliberately **not** the plan:

```powershell
$env:VIBEY_DB_PASSWORD = '<password of the report connection''s database user>'
```

Set it in the environment of the process that launches the MCP server. The worker reads it at the
moment of the call, sets it on a *clone* of the report's connection, and never stores, returns or
logs it: it is not a field of `LayoutOperation` or `LayoutPlan`, nothing in `VibeyReports.Contracts`
knows it exists, and every message these operations can produce — Crystal's own COM text included —
is scrubbed of the value before it leaves the worker.

- **Unset** → the operation fails immediately, naming the variable and what it is for, without
  touching the network.
- **Set but rejected** → Crystal's own reason is surfaced (scrubbed), naming the database user the
  connection logs on as.
- Reports whose connection uses **integrated security** need no variable at all.

Its honest weakness: an environment variable is readable by any process running as the same user,
and making it permanent writes it into a shell profile or the user's environment. It is chosen for
being strictly better than a credential in a plan file, not for being a secret store.

`removeTable` is unaffected — it needs no password and no network. `addSubreport` remains the
credential-free way to combine a second data source.

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
