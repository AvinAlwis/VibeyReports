# Vibey Reports as a Claude Code Skill — Design

**Status:** agreed in conversation 2026-09-02; this document records the decisions.

## Goal

Package Crystal Reports layout editing as a **Claude Code skill written entirely in PowerShell**,
talking to the Crystal SDK directly. No MCP server, no compiled worker, no build step on the machine
that uses it. Installing it is copying a folder.

## Why, and what it replaces

The existing tool is three .NET projects: `Contracts` (validator), `CrystalWorker` (net48 x86, the
only code that touches Crystal), and `Mcp` (a 598-line shell exposing three tools). Using it on a new
machine means installing the .NET 10 runtime, building, publishing, and registering an MCP server.

Distribution is the driver. A skill is a folder of files.

Feasibility is settled, not assumed: on 2026-09-02 a ~50-line PowerShell script opened
`PMSV10_GoalAlignDetail.rpt`, moved an object, saved a new file and reopened it to confirm the change
persisted — with no `.exe` involved. The mechanism works.

## Scope

### In

`read`, plus the layout operations:

```
move  resize  resizeSection  setAlignment
addText  addLine  addBox  addField  addSpecialField  removeObject
setFont  setFontSize  setBold
setTextColor  setFillColor  setLineColor  setSectionBackground
setSectionBreak  setNumberFormat  setCanGrow  setSuppress
```

Twenty-one operations. All are offline property writes needing no database connection.

### Out, and why

| Excluded | Reason |
|---|---|
| `addTable`, `removeTable`, `setTableLocation` | `removeTable` is the one genuinely destructive operation: measured on a live report, Crystal does NOT refuse to remove a table with bound fields — it silently cascade-deletes them (70 objects to 53, reported as success). Its protection would have to be re-earned in PowerShell rather than inherited. Omission is a stronger safeguard than reimplementation. These also need database credentials. |
| `addSubreport`, `setSubreportLink` | Crystal renames a placed sub-report, so the name in the plan is not the name of the object. That two-name-space split took an entire review cycle to get right. |
| `addGroup`, `addSort` | Creating sections has its own naming rule (field name, non-alphanumerics stripped) and interacts with the validator's section model. |
| Formulas, record selection | Deliberate boundary: laying out a report and authoring its logic fail in different ways, and the second produces wrong reports that look right. |

`removeObject` **is** included despite being destructive, because removing a layout object is
recoverable by re-running the generator that placed it, and layout work is impractical without it.
`removeTable` destroys data bindings that no generator restores.

**Consequence, stated plainly:** Report 3 could not have been built with this skill. It needed
sub-reports and shell rebinding. The skill covers "read a report and make it look right", which is
most day-to-day work, not all of it.

## Architecture

```
skill/vibey-reports/
  SKILL.md                  instructions + the workflow knowledge
  scripts/vibey.ps1         single entry point: one JSON request on stdin, one JSON response on stdout
  scripts/VibeyCrystal.psm1 module: open/save/read/validate/apply
  install.ps1               copies the folder to ~/.claude/skills/
tests/
  Run-VibeyTests.ps1        dependency-free harness (Pester 5 is not installed; Pester 3.4 is)
  *.Tests.ps1               assertion scripts
```

**The JSON contract is unchanged from the existing worker**, so plans written by the generators in
`out/scripts/` keep working and the two implementations stay comparable.

```
{"command":"read","reportPath":"..."}
{"command":"apply","reportPath":"...","outputPath":"...","overwrite":true,
 "plan":{"planVersion":1,"operations":[...]}}
```

## Safety model

Three guarantees, in priority order. Everything else is a detail.

1. **Validate the whole plan before touching the SDK.** If any operation fails, nothing is applied
   and no output file is written. This is the property that has never once let a bad plan through,
   and it must survive the port.
2. **The source `.rpt` is never modified.** Open, mutate in memory, `SaveAs` elsewhere.
3. **Write to a temporary file, then move.** A save that fails partway must not leave a corrupt file
   at the destination.

The validator is a pure function over a plan and a schema: no file I/O, no Crystal, no environment
access. That is what makes it testable without a report.

## SDK behaviour this must encode

Measured on this project; every one of these contradicts the obvious approach.

| Trap | Rule |
|---|---|
| `new ReportClientDocument()` hangs forever on a TCP self-connect to port 1566 | Open via `ReportDocument.Load(path)`, then `.ReportClientDocument` |
| `ReportDefinition.Sections` does not exist | Sections live on `Areas`; walk `Areas` → `Sections` → `ReportObjects` |
| RAS objects cannot be mutated in place | `Clone($true)`, change the clone, `ReportObjectController.Modify(old, new)` |
| `SaveAs` takes three arguments | `SaveAs(name, directory, options)`, options `0` = default — **not** `(path, overwrite)` |
| `addField` throws "field value type is not valid" | Set `FieldValueType` explicitly |
| Number formatting silently does nothing | `ISCRCommonFieldFormat.EnableSystemDefault` must be cleared first, or Crystal formats from locale defaults and discards the settings |
| Added objects inherit the font of the first fontable object in their section | Always set `fontSizePt` explicitly on anything added |
| Fonts are `ISCRFont` with a `decimal` Size | Not `System.Drawing.Font` |
| Colours are COLORREF | `0x00BBGGRR` — byte order reversed from HTML hex |

## Testing

Pester 5 is not installed; only the Windows-bundled Pester 3.4, whose syntax diverges sharply. The
harness is therefore **dependency-free**: plain PowerShell that runs assertions and reports counts,
so the skill needs nothing installed on any machine it is copied to.

Validator tests need no Crystal and no fixture. Applier tests use `tests/fixtures/SampleReport.rpt`
and must assert the source file is byte-identical afterwards.

## Distribution

Requires Crystal Reports XI R2 installed and Windows. Requires **no** .NET SDK, no runtime install,
no MCP registration. `install.ps1` copies the folder to `~/.claude/skills/`.

The entry point must **relaunch itself under 32-bit PowerShell** when started 64-bit — the Crystal
assemblies are x86, and Claude will invoke the script without knowing which host it lands in.

## What is NOT decided

- Whether the existing MCP server and worker are retired, kept for the operations the skill omits, or
  left dormant. This design does not remove them.
- Whether the skill later grows sub-reports and data-source operations. If it does, that is the point
  to reconsider shipping the compiled worker instead.
