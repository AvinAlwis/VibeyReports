# Task: add a `removeObject` layout operation

Add the **twelfth** layout operation to Vibey Reports: `removeObject`, which deletes a report object
from a `.rpt`. Removals must be **reported back to the caller**, so an AI agent can see what it
destroyed.

Repo root: `D:\VibeyReports`, branch `feat/remove-object`, clean at `43c7389`.

## Why

Users hit this immediately: building a layout on an existing `.rpt` shell leaves the shell's original
objects behind, with no way to get rid of them. The current workaround — resize to 0×0, move to 0,0,
collapse the section — is clumsy and leaves invisible objects in the file.

## Verified SDK facts — I reflected these off the installed 11.5 assemblies

```
ISCRReportObjectController.Remove(ISCRReportObject ReportObject)   <- exists, use this
ISCRReportSectionController.Remove(Section Section)                <- exists, DO NOT use (see scope)
```

## Scope decisions (already made — do not re-litigate)

- **Any object kind may be removed**, including `Field`, `Subreport`, `Chart` and `Crosstab`. Removing
  a field deletes data from the report; that is accepted. It is safe in context because the source
  `.rpt` is never modified and every apply writes a new file.
- **Removals are reported** in the response so the agent (and the user) can see them.
- **Section removal is NOT in scope.** Crystal treats Details/PageHeader as structural, `Remove` may
  refuse or corrupt, and collapsing a section to zero height already covers the real need.

## Design

### 1. Contracts — `LayoutPlan.cs`

```csharp
public const string RemoveObject = "removeObject";
```

Append it to `LayoutActions.All` (which becomes **twelve** entries). Reuse the existing
`LayoutOperation.Target` field — no new DTO property is needed.

### 2. Contracts — `LayoutPlanValidator.cs`

`removeObject` joins the `needsTarget` set. Its validation branch is short, but **the simulation
update is the part that matters** and is easy to get subtly wrong:

- The target must exist **at that point in the plan** — so `removeObject X` followed by `move X` must
  be rejected, with a message making clear X was removed earlier in this plan rather than never
  existing.
- On success the object is **removed from the simulation dictionary**, which frees its name — so
  `removeObject X` then `addText X` is legal and must pass.
- A later `resizeSection` shrink must account for the object being gone: removing the only tall
  object in a section, then shrinking that section, must be **accepted**.

### 3. Worker — `LayoutApplier.cs`

Add the switch arm. Use the existing `FindObject` helper, then:

```csharp
doc.ReportDefController.ReportObjectController.Remove(existing);
```

Wrap `COMException` with context the way `AddReportObject` and `AddField` already do — name the
action and the object, preserve the original as `InnerException`.

### 4. Reporting — this is the second half of the task

`LayoutApplier.Apply` currently returns `int`. Change it to return a small result type so removals
travel with the count:

```csharp
public sealed class ApplyResult
{
    public int OperationsApplied { get; set; }
    public List<string> RemovedObjects { get; set; } = new List<string>();
}
```

Then thread it through:

- `WorkerResponse` (Contracts) gains `public List<string>? RemovedObjects { get; set; }`.
- The worker's `Program.cs` `apply` handler populates it from the `ApplyResult`.
- `ReportTools.ApplyLayout` includes `removedObjects` in the JSON it returns.

Update every existing caller and test that assumed an `int` return. That signature change is
deliberate — do not paper over it with an overload.

### 5. Tool description — `ReportTools.cs`

Add `removeObject` to the `apply_layout` action list:

```
removeObject   target
```

And state plainly, in the description, that it **permanently removes the object from the generated
report, including bound database fields**, that the source file is never modified, and that removals
are listed in the response under `removedObjects`.

## Tests

**Validator (`LayoutPlanValidatorTests.cs`)**
- `removeObject` with no `target` is rejected.
- `removeObject` targeting an object that does not exist is rejected.
- A valid `removeObject` is accepted.
- `removeObject X` then `move X` is rejected, and the message indicates X was removed by this plan.
- `removeObject X` then `addText` reusing the name `X` is **accepted**.
- Removing the object that would otherwise block a shrink, then `resizeSection` smaller, is **accepted**.
- `LayoutActions.All` now has twelve entries including `removeObject`.

**Applier (`LayoutApplierTests.cs`)**
- Remove a real object from a fixture, save, reopen, assert it is **absent** from the returned schema
  and that the other objects survive.
- Assert `ApplyResult.RemovedObjects` names it.
- Assert the source `.rpt` is byte-identical afterwards (the `ApplyAndReread` helper already does this
  — keep it working).

**End to end (`ProgramEndToEndTests.cs`)**
- An `apply` containing a `removeObject` returns `ok: true` with `removedObjects` populated in the
  JSON response.

**MCP tool (`ReportToolsTests.cs`)**
- `apply_layout`'s returned JSON contains `removedObjects`.

## Global constraints

1. Crystal assemblies only from `C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2\`
   (RAS) and the GAC. **Never the `CrystalReports.*` 13.x NuGet packages** — they save `.rpt` in a
   format Crystal XI R2 and the customer's HR software cannot open.
2. Worker and its test project stay `net48` + `<PlatformTarget>x86</PlatformTarget>`.
3. `VibeyReports.Contracts` stays `netstandard2.0` with **no** Crystal dependency.
4. `VibeyReports.Mcp` must never reference a Crystal assembly.
5. **The source `.rpt` is never modified.**
6. **`LayoutPlanValidator` is the single gate.** `LayoutApplier.ApplyOperationsWithoutValidation` is an
   internal test-only seam that skips it; `Program.cs` is in the same assembly so the compiler will not
   stop you. Production code calls only the public `Apply`.
7. Geometry in twips (1440 = 1 inch).

## Practical notes

- Run **all three suites** in the **FOREGROUND** with a generous timeout — budget 20+ minutes. **Do not
  background the run, do not set up a monitor, do not poll**; several agents on this project have lost
  results that way and one stalled on a watchdog.
- **The VPN must be up.** Some fixtures point at `sgdev01db02.cloud`; with it unreachable, Crystal
  blocks indefinitely inside database field enumeration and the suite hangs rather than failing. If a
  run stalls with near-zero CPU, that is the cause — check connectivity before assuming a code fault.
- `FluentAssertions` 8.x uses `BeGreaterThanOrEqualTo`.
- Never create a git remote and never push. Local commits only. Conventional Commits.
