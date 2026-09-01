# Continuing development on another machine

Short answer to the usual question: **yes** — clone or pull, run `publish.ps1`, and the updated
build is live. But this project's bottleneck is not CPU, and a faster machine will not help unless
it meets the requirements below. Read this before assuming a clone will build.

## Hard prerequisites

The build **cannot succeed without these**. They are not NuGet packages and cannot be restored.

| Requirement | Why |
|---|---|
| **Windows** | Crystal's RAS SDK is COM. There is no Linux or macOS path. |
| **Crystal Reports XI R2 (11.5)** installed | `VibeyReports.CrystalWorker` references assemblies from `C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2\` and the GAC. Without the install, that project does not compile. |
| **.NET Framework 4.8 developer pack** | The worker targets `net48` and **must** stay `x86` — the Crystal assemblies are 32-bit. |
| **.NET 10 SDK** | `VibeyReports.Mcp` targets `net10.0`. |

**Never** substitute the `CrystalReports.*` 13.x NuGet packages for the installed 11.5 assemblies.
They compile, and then save `.rpt` files in a format Crystal XI R2 and the customer's HR software
cannot open — a failure that does not surface until someone tries to open the report. This is the
single most important constraint in the project.

## Why a more powerful machine buys less than you would expect

Nearly all wall-clock time here is spent inside Crystal's COM layer, which is single-threaded and
I/O-bound. The test suite takes roughly seven minutes, and most of that is Crystal opening, mutating
and saving reports one at a time. More cores and faster storage help the .NET builds, not the part
that dominates.

What *does* speed the loop up: a machine that can hold the VPN connection reliably (see below).

## Database and VPN

Layout work — reading, mutating and saving `.rpt` files — is **fully offline** and needs no database.

These need database connectivity, via VPN:

- Rendering a report to PDF with live data
- `addTable` / `setTableLocation` (see the limitation below)
- Enumerating fields on a report whose saved data has been discarded

With the VPN **down**, Crystal's OLE DB layer does not fail fast — it **blocks**. A test run that
appears hung for tens of minutes at 0% CPU is usually this, not a deadlock in our code. Check the
VPN before diagnosing anything else; this was misdiagnosed twice before `--blame-hang` settled it.

### `addTable` and `setTableLocation` do not work on the PeoplesHR reports

Crystal persists a connection's **user name** but never its **password**. Both operations always
contact the server and fail with `Logon failed`. This is not a bug to fix on a better machine — it
is a deliberate design boundary: no Vibey Reports operation accepts a username or password, because
plans are written to JSON files on disk. `removeTable` is unaffected and works fully offline.

## Getting running

```powershell
git clone <repo-url> ; cd VibeyReports
dotnet build VibeyReports.slnx
.\publish.ps1
```

`publish.ps1` writes `dist\VibeyReports.Mcp.exe` and `dist\worker\VibeyReports.CrystalWorker.exe`.
They go to **separate** directories deliberately — both reference `VibeyReports.Contracts`, and
publishing them together lets the second overwrite files the first needs.

Then register the MCP server with Claude Code; see **Registration with Claude Code** in
[README.md](../README.md).

**After every pull that touches `src/`, re-run `publish.ps1`.** `dist\` is git-ignored, so a pull
updates the source but leaves the built worker stale. A stale `dist\` has silently wasted time on
this project three times — the symptom is an operation being rejected as unsupported when the
validator's error message lists actions that the source clearly contains.

## What is in the repository

- `src/` — the three projects. `Contracts` (netstandard2.0, no Crystal), `CrystalWorker` (net48 x86,
  the only project that touches Crystal), `Mcp` (net10.0, never references Crystal).
- `tests/` — three suites plus `tests/fixtures/*.rpt`.
- `docs/decision-log.md` — every ruling made, with reasoning. Read this before changing behaviour;
  most surprising choices are deliberate and explained.
- `docs/sdk-notes.md` — **SDK behaviour measured live**, much of which contradicts the
  documentation. Trust this over any Crystal reference you find online.
- `docs/superpowers/plans/` and `docs/briefs/` — the original plan and the per-task
  briefs, kept so the reasoning behind each feature is portable.
- `out/reports/` — the generated `.rpt` deliverables and hand-made base reports.
- `out/scripts/` — the PowerShell generators that build those reports from a base.

Note that some `out/reports/` files are **not** regenerable: `Report1.rpt` and the
`PMSV10_IndPerfOverview` bases were created by hand in the Crystal Designer. They are committed for
that reason.

## Working across two machines

Ordinary git. Commit on one, push, pull on the other, re-run `publish.ps1`. Two things worth
knowing:

- `.rpt` files are **binary**. Git cannot merge them. If both machines edit the same report, one
  side's changes are lost — coordinate, or regenerate from `out/scripts/` instead of merging.
- `dist/`, `bin/`, `obj/` and `TestResults/` are ignored by design. `TestResults/` in particular can
  contain hang dumps of several hundred megabytes.
