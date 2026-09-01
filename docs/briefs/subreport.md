# Task: add `addSubreport` and `setSubreportLink`

Add two operations so Vibey Reports can embed a sub-report and wire its parameters — the structure the
customer's report specification requires.

Repo root: `D:\VibeyReports`, branch `feat/remove-object`, clean tree.

## Why

The spec for the Goal Alignment report says: *"Crystal Reports can bind only one data source per report
object, so the report is built as a main report plus embedded sub-reports."* The main report is bound to
one stored procedure; the per-goal detail rows come from a second, in a sub-report whose parameters are
**linked from main-report fields**.

Without this, the report only works for a single employee — the bulk-generation flow selects several.

## Verified SDK surface — I reflected all of this off the installed 11.5 assemblies

`ISCRSubreportController`, reached via `session.Document.SubreportController`:

```
SubreportClientDocument ImportSubreport(String Name, String reportURL, Section Section)
SubreportClientDocument ImportSubreportEx(String Name, String reportURL, Section Section,
                                          Int32 left, Int32 top, Int32 width, Int32 height)
SubreportClientDocument GetSubreport(String Name)
Strings                 GetSubreportNames()
SubreportLinks          GetSubreportLinks(String SubreportName)
Void                    SetSubreportLinks(String SubreportName, SubreportLinks links)
```

`ISCRSubreportLink` (concrete class `SubreportLinkClass`, in `...ReportAppServer.ReportDefModel`):

```
String MainReportFieldName     -- the main-report field to link FROM
String SubreportFieldName      -- the subreport field to link TO
String LinkedParameterName     -- the subreport parameter that receives the value
```

`ISCRSubreportLinks` (concrete `SubreportLinksClass`) is a collection with
`Add(SubreportLink)`, `Remove(Int32)`, `Item`, `Count`, `RemoveAll()`.

`CrReportObjectKindEnum.crReportObjectKindSubreport` already maps to `"Subreport"` in
`ReportReader.ClassifyKind`, so a sub-report reads back as a normal object with geometry.

## Operations

Two new constants in `LayoutActions` (taking it to **eighteen**).

### `addSubreport`

| Field | Meaning |
|---|---|
| `section` | Section to place it in |
| `newName` | Name for the sub-report object |
| `reportPath` | Absolute path to an existing `.rpt` to import |
| `leftTwips` / `topTwips` / `widthTwips` / `heightTwips` | Geometry, as for any add |

Applier: `doc.SubreportController.ImportSubreportEx(newName, reportPath, section, l, t, w, h)`.
Wrap `COMException` with context the way `AddReportObject` and `AddField` already do.

### `setSubreportLink`

| Field | Meaning |
|---|---|
| `target` | The sub-report object's name |
| `mainReportField` | e.g. `{sp_perf_goal_align_cascade;1.performance_cycle_id}` |
| `subreportField` | The matching field inside the sub-report |
| `linkedParameter` | The sub-report parameter, e.g. `@performance_cycle_id` |

Applier: fetch the existing links with `GetSubreportLinks`, **append** a new `SubreportLinkClass`, then
`SetSubreportLinks`. Appending matters — a report needs two links (cycle and employee) and the second
call must not discard the first.

`LayoutOperation` gains: `ReportPath`, `MainReportField`, `SubreportField`, `LinkedParameter` (all `string?`).

## Validator rules

- `addSubreport` joins the `needsSection` set and the shared add branch: `newName` required and unique,
  full geometry required, bounds checked as for any other add.
- `reportPath` required, non-empty, and must end in `.rpt` (case-insensitive). **Do not check the file
  exists in the validator** — it must stay pure with no file I/O. The applier checks existence and
  throws a clear message naming the path.
- The simulated kind for an added sub-report is `"Subreport"`.
- `setSubreportLink` joins `needsTarget`. The target must exist **and its simulated kind must be
  `"Subreport"`** — linking a text object is a plan error worth catching. All three link fields are
  required and non-empty.
- `setSubreportLink` targeting a sub-report **added earlier in the same plan** must be accepted — that
  is the normal usage, and the cumulative simulation already supports it.
- A sub-report is **not** fontable: `setFont` / `setBold` / `setFontSize` on one must be rejected by the
  existing `IsFontable` check. Confirm this already falls out and add a test.

## Reading links back

The same lesson this project has learned twice: a write-only API leaves the agent blind, and a
round-trip test that reads a different property than it writes can never fail.

`ObjectInfo` gains:

```csharp
/// <summary>Populated only for Kind == "Subreport". Null otherwise.</summary>
public List<SubreportLinkInfo>? SubreportLinks { get; set; }
```

```csharp
public sealed class SubreportLinkInfo
{
    public string MainReportFieldName { get; set; } = "";
    public string SubreportFieldName { get; set; } = "";
    public string LinkedParameterName { get; set; } = "";
}
```

`ReportReader` populates it via `SubreportController.GetSubreportLinks(objectName)` for Subreport-kind
objects. Wrap that call defensively — if it throws for a sub-report with no links, leave the list empty
rather than failing the whole read.

## Tool description

Add both to `apply_layout`'s action list:

```
addSubreport      section, newName, reportPath, leftTwips, topTwips, widthTwips, heightTwips
setSubreportLink  target, mainReportField, subreportField, linkedParameter
```

State that `reportPath` must be an existing `.rpt`, that a sub-report is how you combine a second data
source into one report, and that each linked parameter needs its own `setSubreportLink` call.

## Tests

**Contracts**
- `addSubreport` accepted with valid input; rejected without `reportPath`; rejected when `reportPath`
  doesn't end in `.rpt`; rejected on `newName` collision; rejected on out-of-bounds geometry.
- `setSubreportLink` accepted against a sub-report added earlier in the same plan.
- `setSubreportLink` rejected when the target is a Text object (wrong kind).
- `setSubreportLink` rejected when any of the three link fields is missing.
- `setFontSize` on a sub-report is rejected (kind gate).
- `LayoutActions.All` has eighteen entries.

**Worker**
- Import `tests/fixtures/SampleReport.rpt` as a sub-report into a fixture, save, reopen; assert an object
  of kind `Subreport` exists at the requested geometry.
- Add **two** links to one sub-report, save, reopen, assert `SubreportLinks` has both, in order, with the
  right field and parameter names. This is the test that catches a `SetSubreportLinks` implementation
  that replaces rather than appends.
- The source `.rpt` is byte-identical afterwards (`ApplyAndReread` already asserts this).

**MCP**
- `read_report` output includes `subreportLinks` for a sub-report object.

## Global constraints

1. Crystal assemblies only from `C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2\`
   (RAS) and the GAC. **Never the `CrystalReports.*` 13.x NuGet packages** — they save `.rpt` in a
   format Crystal XI R2 and the customer's HR software cannot open.
2. Worker and its test project stay `net48` + `<PlatformTarget>x86</PlatformTarget>`.
3. `VibeyReports.Contracts` stays `netstandard2.0` with **no** Crystal dependency — `SubreportLinkInfo`
   is a plain DTO.
4. `VibeyReports.Mcp` never references Crystal.
5. **The source `.rpt` is never modified.**
6. **`LayoutPlanValidator` is the single gate.** `LayoutApplier.ApplyOperationsWithoutValidation` is an
   internal test-only seam; production code calls only the public `Apply`.
7. **Do not touch the database controller.** `AddTable`, `RemoveTable`, `SetDataSource` and friends exist
   on `ISCRDatabaseController`, but changing a report's data sources is outside this tool's remit and
   outside this task. Importing a sub-report brings its *own* data source with it — that is the point,
   and it does not require touching the main report's.

## Testing

**Do not run the test suites.** CLI `dotnet test` runs on this machine hang intermittently — a known
environment issue, not a code fault. The user runs tests from Visual Studio and reports results.

`dotnet build` is expected and must be clean across the whole solution before you commit. Say so in your
report. Write the tests; do not execute them.

Never create a git remote and never push. Local commits only. Conventional Commits.
