# Task: data-source operations (`removeTable`, `addTable`, `setTableLocation`)

Add three operations that manage a report's data source tables.

Repo root: `D:\VibeyReports`, branch `feat/remove-object`.

**Sequencing:** a separate agent is landing review fixes to `LayoutPlanValidator.cs`,
`LayoutApplier.cs`, `ReportReader.cs`, `ReportSchema.cs` and `ReportTools.cs` on this branch. Start
from a clean tree with those fixes already committed; run `git log --oneline -5` first and base your
work on what is actually there rather than on the line numbers quoted below.

## Why, and why this is a deliberate boundary change

Every previous brief carried this constraint:

> **Do not touch the database controller.** Changing a report's data sources is outside this tool's remit.

**The user has explicitly lifted it**, on the reasoning that these operations never write to SQL
Server — they only change the report's own binding metadata, and the database itself is read-only
throughout. That reasoning is correct, and it is the boundary: **report metadata may be written; the
database is only ever read.**

Scope is `removeTable`, `addTable`, `setTableLocation`. Still OUT of scope:
`ModifyTableConnectionInfo`, `SetTableLocationByServerDatabaseName`, and anything else carrying
credentials. **No layout operation may ever accept a username or password** — plans are written to
JSON files on disk and quoted in docs. `ISCRConnectionInfo` exposes `UserName`/`Password`; never
surface either through the contracts. `addTable` sidesteps this entirely by CLONING the connection
info of a table already in the report.

Record the boundary change and the credential rule in `docs/decision-log.md`.

The driving case: `PMSV10_GoalAlignCascade.rpt` is bound to both `sp_perf_goal_align_cascade;1` and
`sp_perf_goal_align_detail;1`. Two unlinked stored procedures in one report is a cartesian join —
every cascade row multiplied by every detail row. The detail rows now come from an embedded
sub-report instead, so the second table is redundant and actively harmful, but nothing in the tool
can remove it.

## Verified SDK surface — reflected off the installed 11.5 assemblies

```
ISCRDatabaseController.RemoveTable(String TableAlias)          // alias STRING, not a table object
ISCRDatabaseController.AddTable(ISCRTable Table, Object RelatedTableLinks)
ISCRDatabaseController.SetTableLocation(ISCRTable CurTable, ISCRTable NewTable)
ISCRDatabaseController.RemoveTableLink(TableLink TableLink)
ISCRDatabaseController.FindTableLink(Object fromTable, Object toTable)
```

`ISCRTable`: `Name, Alias, QualifiedName, Description, ClassName, ConnectionInfo, DataFields, Attributes`

`ISCRProcedure`: all of the above **plus `Parameters`** — concrete classes `TableClass`,
`ProcedureClass`, `ConnectionInfoClass` in `CrystalDecisions.ReportAppServer.DataDefModel`.

`doc.DatabaseController.Database.Tables` is already enumerated in `ReportReader` and `LayoutApplier`,
so the access pattern is established.

## Operations

Three new constants in `LayoutActions` (taking it to **twenty-one**).

### `removeTable`

| Field | Meaning |
|---|---|
| `target` | Table alias to remove, e.g. `sp_perf_goal_align_detail;1` |

### `addTable`

Adds a table or stored procedure, cloning its connection from a table already present.

| Field | Meaning |
|---|---|
| `target` | Alias of an existing table whose `ConnectionInfo` is cloned |
| `tableName` | Database object name, e.g. `sp_perf_goal_align_detail` |
| `newName` | Alias for the new table, e.g. `sp_perf_goal_align_detail;1` |

**Measure before you build.** `ISCRProcedure` is a distinct type from `ISCRTable`, and every table in
the driving reports is a stored procedure — so `ProcedureClass`, not `TableClass`. Probe live to
determine: which concrete class `AddTable` needs for an SP; whether `Parameters` must be populated
before the call or is discovered from the server; and what `QualifiedName` must look like. The
existing tables in `out/reports/PMSV10_GoalAlignCascade.rpt` are the reference — read their
`Name`, `Alias` and `QualifiedName` and mirror the shape. Document what you measure in
`docs/sdk-notes.md`.

Also determine whether a cloned `ConnectionInfo` carries enough to render, or whether the added
table prompts for a logon (Crystal commonly does not persist passwords). Report the answer either
way — if it does prompt, that is a real limitation the tool description must state.

Pass `null` for `AddTable`'s `RelatedTableLinks` unless probing shows otherwise.

### `setTableLocation`

Repoints an existing table at a different database object on the **same** connection.

| Field | Meaning |
|---|---|
| `target` | Alias of the table to repoint |
| `tableName` | New database object name |

Applier: clone the current table, set the name/qualified name on the clone, then
`SetTableLocation(current, clone)`. The clone keeps the original `ConnectionInfo` untouched — this
operation must never change server, database, or credentials.

## Validator rules — this is where the safety lives

`removeTable`, `addTable` and `setTableLocation` all join `needsTarget`. Beyond that:

1. **`removeTable` must refuse if any surviving object is still bound to the table.** This is the
   rule that matters. Removing a table out from under a bound field leaves a broken report. A field
   object's binding is its `ObjectInfo.DataSource`, the formula form — e.g.
   `{sp_perf_goal_align_detail;1.goal_id}`. An object is bound to alias `A` when its `DataSource`
   starts with `"{" + A + "."`. Match **ordinally and case-insensitively**; do not use a plain
   `Contains`, which would let alias `foo;1` falsely match `{other_foo;1.x}`.

2. **Respect the cumulative simulation.** A plan that removes the bound fields *first* and then the
   table must be **accepted** — that is the real usage. This means `SimObject` needs a new
   `DataSource` field, populated from `ObjectInfo.DataSource` when seeding from the schema and from
   `fieldRef` for an `addField` added earlier in the same plan. A plan that removes the table while
   a bound field survives must be **rejected**, naming the offending object(s). Simulate table
   membership cumulatively too: `addTable` then `removeTable` of the same alias must validate, and
   `removeTable` then `addField` referencing it must be rejected.

3. **Existence checks, carefully.** Derive known aliases from `schema.AvailableFields`'s distinct
   `TableAlias`. But `ReportReader` deliberately clears `AvailableFields` when field enumeration
   fails (no DB connection — a normal, supported state). So if `AvailableFields` is empty, **skip
   the existence check** rather than rejecting every table operation. You cannot prove absence from
   an empty list. Rule 1 still applies either way.

4. **A sub-report must never block removal.** A sub-report carries its own data source; it is not
   bound to the main report's tables and its `DataSource` is null. Confirm this falls out of rule 1
   and add a test that pins it — otherwise embedding a sub-report would silently make its host's
   tables unremovable.

5. `addTable`: `newName` required and unique among existing aliases; `tableName` required.
   `setTableLocation`: `tableName` required and non-empty.

The validator stays pure: no file I/O, no Crystal.

## Applier

Wrap `COMException` in context naming the operation and alias, consistent with `AddReportObject` /
`AddField` / `SetSubreportLink`.

**Probe this live before deciding the shape:** a table that participates in a table link may be
refused by `RemoveTable` until the link is removed. Find out. If Crystal refuses, remove the
`TableLink`s referencing that alias via `RemoveTableLink` first, then retry — and document what you
measured in `docs/sdk-notes.md`. If it removes cleanly regardless, document that instead. Do not
guess; this project has been burned repeatedly by assuming SDK behaviour that measurement
contradicted.

## Reporting

`removeObject` already reports what it removed in the response. Mirror that for tables — same shape,
same reasoning: a destructive operation must say what it destroyed.

## Tests

**Contracts**
- `removeTable` accepted when nothing is bound to the alias.
- `removeTable` accepted when the plan removes the bound fields earlier in the same plan.
- `removeTable` **rejected** when a bound field survives; the message names the object.
- `removeTable` rejected on missing/empty `target`.
- Table ops accepted when `AvailableFields` is empty (existence check skipped).
- A sub-report in the report does not block `removeTable`.
- Alias matching is not substring-fooled: alias `foo;1` must not match `{other_foo;1.x}`.
- `addTable` rejected on alias collision; rejected without `tableName`.
- `addTable` then `removeTable` of the same alias validates; `removeTable` then `addField`
  referencing it is rejected.
- `setTableLocation` rejected on unknown target and on missing `tableName`.
- `LayoutActions.All` has twenty-one entries.

**Worker**
- Remove a table from a fixture, save, reopen, assert the alias is gone from `AvailableFields`.
- `addTable` cloning an existing connection, save, reopen, assert the new alias is present.
- `setTableLocation`, save, reopen, assert the table points at the new object and `ConnectionInfo`
  is unchanged.
- The source `.rpt` is byte-identical afterwards (`ApplyAndReread` already asserts this).

## Global constraints

1. Crystal assemblies only from `C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2\`
   and the GAC. **Never the `CrystalReports.*` 13.x NuGet packages** — they save `.rpt` in a format
   Crystal XI R2 and the customer's HR software cannot open.
2. Worker and its test project stay `net48` + `<PlatformTarget>x86</PlatformTarget>`.
3. `VibeyReports.Contracts` stays `netstandard2.0` with **no** Crystal dependency.
4. `VibeyReports.Mcp` never references Crystal.
5. **The source `.rpt` is never modified.**
6. **`LayoutPlanValidator` is the single gate.** `ApplyOperationsWithoutValidation` is an internal
   test-only seam; production calls only the public `Apply`.

## Testing

**Do not run `dotnet test`** — CLI test runs hang on this machine (a known environment issue, not a
code fault). The user runs tests from Visual Studio. `dotnet build` must be clean across the solution
before you commit; say so in your report. Write the tests; do not execute them.

Small one-off worker probes against real `.rpt` files via `dist\worker\VibeyReports.CrystalWorker.exe`
ARE encouraged — that is how the `addTable` questions above must be answered. Rebuild with
`.\publish.ps1` first so `dist\` is not stale.

Never create a git remote and never push. Local commits only. Conventional Commits.
