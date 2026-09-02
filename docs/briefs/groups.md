# Task: grouping and sorting (`addGroup`, `addSort`)

Two operations taking `LayoutActions.All` from **twenty-six to twenty-eight**. Both are offline —
neither contacts the database.

Repo root: `D:\VibeyReports`, branch `main`, clean tree.

## Why this one matters more than its size suggests

`addGroup` is the only way to create a section. Measured while scoping the formatting operations:
**`ISCRReportDefController` cannot add or remove sections** — its only Add/Modify/Remove methods are
for chart objects. A report's five-section shape is otherwise fixed, and that has already cost us:

- **Report 3 cannot render several employees into one file.** Its page 1 lives in the Report Header
  because a section cannot exceed one printable page, and the Report Header renders once per report.
  With a group on `emp_number`, page 1 becomes a Group Header and repeats per employee — which is
  what the spec's bulk-generation flow actually describes.
- **Ordering is currently at the mercy of the stored procedure.** Every report relies on the
  procedure's `ORDER BY`, which Crystal is free to ignore once it has its own row set. `addSort`
  makes the order a property of the report rather than a hope.

## Verified SDK surface — reflected off the installed 11.5 assemblies

```
doc.DataDefController.GroupController : Add(Int32, Group)
                                        AddByName(Int32, String, CrDateConditionEnum)
                                        Modify(Object, Group), Remove(Object)
doc.DataDefController.SortController  : Add(Int32, ISCRSort)
                                        Modify(Object, ISCRSort)
                                        ModifySortDirection(Object, CrSortDirectionEnum)
                                        Remove(Object)
doc.DataDefController.FindFieldByFormulaForm(String)   -- resolves "{alias.field}" to an ISCRField

ISCRGroup : ConditionField (ISCRField), Options (ISCRGroupOptions)
ISCRSort  : SortField (ISCRField), Direction (CrSortDirectionEnum)
CrSortDirectionEnum : Ascending, Descending, TopN, BottomN, TopNPercentage, BottomNPercentage
Concrete classes: GroupClass, GroupOptionsClass, SortClass (DataDefModel)
```

`ReportReader.ClassifyArea` already maps `crAreaSectionKindGroupHeader` / `GroupFooter` to
`"GroupHeader"` / `"GroupFooter"`, so group sections already read back as sections. That half is done.

## MEASURE THIS FIRST — it decides the design

Adding a group creates a Group Header and Group Footer section, and **Crystal names them, not us.**
Before writing the validator, add a group to a scratch copy of a real report and read it back:

1. What are the new sections called? (`GroupHeaderSection1`? `GroupHeader1`? something else)
2. Is the naming deterministic and predictable from the group index?
3. Where do they land in the section order relative to the existing five?

This is the same trap `addSubreport` hit: `ImportSubreportEx`'s `Name` argument does not become the
object's `Name`, Crystal auto-numbers that, and the mismatch made cross-plan linking look impossible
for a whole review cycle. Do not guess the naming — measure it, then pick:

- **If naming is deterministic:** register the new sections in the validator's cumulative simulation
  under their predicted names, so `[addGroup, addText into the new group header]` validates in one
  plan. This is the good outcome and the one to aim for.
- **If it is not:** reject placement into a group section created earlier in the same plan, with a
  message saying so and telling the caller to run `read_report` and use the real name in a second
  plan. Worse to use, but honest — and far better than validating and then throwing mid-plan, which
  faults the session and loses the whole plan.

Document the answer in `docs/sdk-notes.md` either way, including what you tried.

## Operations

### `addGroup`

| Field | Meaning |
|---|---|
| `fieldRef` | Field to group on, e.g. `{sp_perf_detailed_eval_overview;1.emp_number}` |
| `groupIndex` | Optional, 0-based position among existing groups. Defaults to appending. |
| `direction` | Optional, `ascending` (default) or `descending` |

Applier: resolve `fieldRef` with `DataDefController.FindFieldByFormulaForm`, build a `GroupClass`
with that as `ConditionField`, and `GroupController.Add(index, group)`. Prefer this over `AddByName`
so the field is resolved explicitly and a bad `fieldRef` fails with a clear message rather than
whatever `AddByName` does with an unknown name — but try both and say which behaves better.

### `addSort`

| Field | Meaning |
|---|---|
| `fieldRef` | Field to sort on |
| `direction` | `ascending` or `descending`, required |
| `sortIndex` | Optional, 0-based position among existing sorts. Defaults to appending. |

Applier: `SortClass` with `SortField` and `Direction`, then `SortController.Add(index, sort)`.

**Only `ascending` and `descending` are in scope.** The four TopN variants need an N that this
operation has no way to express; the validator must reject any other direction string rather than
passing it through.

## Validator rules

- Both join neither `needsSection` nor `needsTarget` — they address a *field*, not an object or a
  section. `fieldRef` is required and non-empty on both; `direction` is required on `addSort`.
- `direction` must be `ascending` or `descending`, matched case-insensitively. Reject everything else
  by name, including the TopN variants, so an unsupported value is a plan error rather than a COM
  failure.
- **`fieldRef` should be checked against `schema.AvailableFields` — but skipped entirely when that
  list is empty.** `ReportReader` clears it when there is no database connection, which is a normal
  supported state; rejecting on an empty list would break both operations whenever the VPN is down.
  `removeTable`'s existence check documents the same trap, and `setNumberFormat` follows it too.
- `groupIndex` / `sortIndex`, when supplied, must be >= 0 and no greater than the current count
  (simulated cumulatively, so two `addGroup`s in one plan index correctly).
- Whatever you learn from the measurement above determines how new sections enter the simulation.
- The validator stays pure: no file I/O, no Crystal, no environment access.

## Reading it back — do not skip this

`ReportSchema` gains groups and sorts. Without this the agent can add a group and then has no way to
see it, which is the write-only failure this project has shipped twice.

```csharp
public sealed class GroupInfo
{
    public string FieldRef { get; set; } = "";     // formula form
    public string Direction { get; set; } = "";    // "ascending" | "descending"
    public string? HeaderSection { get; set; }     // the section names Crystal assigned
    public string? FooterSection { get; set; }
}

public sealed class SortInfo
{
    public string FieldRef { get; set; } = "";
    public string Direction { get; set; } = "";
}
```

Populate from `DataDefinition.Groups` and `DataDefinition.Sorts`. Wrap defensively — a report with
no groups must read back as an empty list, not a failure. Assert the same properties you set: a
round-trip test that reads a different property than the one written can never fail.

## Scope — what is deliberately NOT here

**No `removeGroup` or `removeSort`.** Removing a group destroys its header and footer sections and
everything placed in them. That is the same shape as `removeTable`'s measured cascade-delete, and it
needs its own validator rule and its own review rather than being bundled in as a symmetry. Add them
later if wanted.

No TopN sorting, no group selection formulas, no `GroupFilterController`.

## Tests

**Contracts**
- `addGroup` accepted with a valid `fieldRef`; rejected without one.
- `addSort` accepted with each direction; rejected without `direction`; rejected on an unknown
  direction; rejected specifically on `topN`.
- Both accepted when `AvailableFields` is empty (no field check).
- Both rejected on a negative index and on an index past the end.
- Two `addGroup`s in one plan index correctly against the cumulative simulation.
- Whatever in-plan section-placement rule the measurement leads to, test both sides of it.
- `LayoutActions.All` has twenty-eight entries — assert through the existing exhaustive test in
  `LayoutPlanTests`. Do NOT add a second count assertion; one was deleted for going red on every
  addition without catching anything.

**Worker** — through `ApplyAndReread`, which asserts the source `.rpt` stays byte-identical
- Add a group, save, reopen, assert a `GroupHeader` section exists and `Groups` reports the field.
- Add two groups, save, reopen, assert both are present in order.
- Add a sort each way, save, reopen, assert direction round-trips.
- Add a group then place a text object in its header — in one plan if the measurement allows it, in
  two if not.

**MCP**
- `read_report` output includes `groups` and `sorts`.

## Global constraints

1. Crystal assemblies only from `C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2\`
   and the GAC. **Never the `CrystalReports.*` 13.x NuGet packages** — they save `.rpt` in a format
   Crystal XI R2 and the customer's HR software cannot open.
2. Worker stays `net48` + `x86`; `Contracts` stays `netstandard2.0` with no Crystal dependency;
   `Mcp` never references Crystal.
3. **The source `.rpt` is never modified.** Never write to `out\reports\` or `tests\fixtures\` —
   probe to a scratch path and delete after.
4. **`LayoutPlanValidator` is the single gate.** `ApplyOperationsWithoutValidation` is an internal
   test-only seam; production calls only the public `Apply`.
5. No operation accepts a credential.

## Testing

Put a hard timeout on anything touching Crystal — it blocks rather than failing on several paths,
and an earlier agent on this project stalled for ten minutes on exactly that. The tell for a real
hang is CPU, not elapsed time: sample the process twice and compare `.CPU`.

Do the offline work first, get it building and the Contracts tests green, and commit that before any
live probing — so a stall costs a probe rather than the task.

Run the suites and fix what you turn red. Contracts and Mcp take under a minute each; the worker
suite takes about 11 minutes since parallelisation was disabled there. `dotnet build` must be clean
across the solution before you commit. Rebuild with `.\publish.ps1` before probing so `dist\` is not
stale — that has caught this project out three times.

Never create a git remote and never push. Local commits only, Conventional Commits.
