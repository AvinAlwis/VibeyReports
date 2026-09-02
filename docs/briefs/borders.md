# Task: `setBorder`

One operation, taking `LayoutActions.All` from **twenty-eight to twenty-nine**. Offline: no database,
no `VIBEY_DB_PASSWORD`.

Repo root: `D:\VibeyReports`, branch `main`, clean tree.

## Why

Every table this project has built draws its grid with `addLine` objects — vertical dividers between
columns and a rule under each row. That works only while rows are a fixed height. It breaks the
moment a cell can grow:

> The FDP comment columns hold `NVARCHAR(2000)` free text. With `setCanGrow` on the fields, a row
> grows to several times its designed height — but the dividers and the rule are separate Line
> objects at the original height, and a Line has no can-grow. The text runs on past the ruling, so
> the table looks like it is leaking out of its own borders. Confirmed on screen by the user.

A Line cannot be made to stretch to match a neighbour that grew. The fix is not to draw the grid at
all: **give the fields their own borders.** A border belongs to the object, so it grows when the
object grows, and the cell stays closed around the text at any height.

## Verified SDK surface — reflected off the installed 11.5 assemblies

```
ISCRReportObject.Border  ->  ISCRBorder
ISCRBorder : LeftLineStyle, RightLineStyle, TopLineStyle, BottomLineStyle  (CrLineStyleEnum)
             BorderColor (UInt32), BackgroundColor (UInt32)
             HasDropShadow, EnableTightHorizontal, ConditionFormulas
CrLineStyleEnum : crLineStyleNoLine, crLineStyleSingle, crLineStyleDouble,
                  crLineStyleDashed, crLineStyleDotted
```

`Border` sits on `ISCRReportObject` itself — the same level as `Left`/`Top`/`Width`/`Height` — not on
`ObjectFormat`. Every report object has one.

`BorderColor` is a `UInt32` COLORREF, exactly like the colours `setTextColor` / `setFillColor`
already handle. Reuse `ColorRef`; do not write a second converter.

## The operation

### `setBorder`

| Field | Meaning |
|---|---|
| `target` | Object to give a border |
| `left` / `right` / `top` / `bottom` | Optional line style, one of the names below |
| `color` | Optional `#RRGGBB` for `BorderColor` |

Style vocabulary, mapped to the enum in the worker — callers should not write
`crLineStyleSingle`:

```
none | single | double | dashed | dotted
```

At least one of the four sides or `color` must be present, so a no-op operation is a plan error
rather than a silent nothing.

**`BackgroundColor` is deliberately NOT exposed here.** It would give two ways to fill an object —
this and `setFillColor` — with different semantics on different kinds, and no way for a reader to
tell which one produced a given fill. Add it later as its own decision if wanted.

## Validator rules

- `setBorder` joins `needsTarget`.
- Each supplied side must be one of the five style names, matched case-insensitively. Reject anything
  else by name rather than passing it through to COM.
- `color`, when present, must satisfy the existing hex-colour rule — reuse it, do not re-implement.
- At least one side or `color` required.
- **No kind restriction.** Every report object carries a Border, and unlike can-grow there is nothing
  semantically odd about bordering a Box or a Subreport. The project has now made the
  too-narrow-allowlist mistake twice — `FontableKinds` omitting `FieldHeading`, and `CanGrowKinds`
  omitting `Subreport`, the second of which shipped and caused the defect this brief exists to fix.
  Do not add a third.
- The validator stays pure: no file I/O, no Crystal, no environment access.

## Reading it back

`ObjectInfo` gains a nullable `Border`, populated for every object:

```csharp
public sealed class BorderInfo
{
    public string Left { get; set; } = "none";
    public string Right { get; set; } = "none";
    public string Top { get; set; } = "none";
    public string Bottom { get; set; } = "none";
    public string? ColorHex { get; set; }
}
```

Assert the same properties you set. This project has shipped a write-only property twice, and the
last round found `setNumberFormat` silently doing nothing because a round-trip test checked a
different property than the one written.

## Applier

Clone the object, set `Border` on the clone, `Modify(old, new)` — as everywhere else. RAS objects
cannot be mutated in place. Wrap `COMException` with context naming the operation and target.

**Probe before settling the shape.** `Border` may need to be replaced wholesale rather than mutated
in place, the way `FieldFormat` did — and `setNumberFormat` was a silent no-op until
`EnableSystemDefault` was cleared, which nothing in the type signature hinted at. Set a border, save,
reopen, and confirm it is actually there before writing the tests around it. Record what you measure
in `docs/sdk-notes.md`, negative results included.

## Tests

**Contracts**
- Accepted with one side; with all four; with `color` only.
- Rejected with no side and no colour; on an unknown style name; on a malformed colour.
- Accepted against a Box and against an embedded Subreport (no kind gate).
- `LayoutActions.All` has twenty-nine entries — through the one exhaustive test in
  `LayoutPlanTests`. Do not add a second count assertion.

**Worker** — through `ApplyAndReread`, which asserts the source `.rpt` stays byte-identical
- Set all four sides and a colour, save, reopen, assert every one round-trips.
- Set only `bottom`, save, reopen, assert the other three are still `none`.

**MCP**
- `read_report` output includes `border`.

## Follow-on, NOT part of this task

Once this exists, the table generators (`gen-eval-subreports.ps1`, `gen-detail-subreport.ps1`) should
drop their `DV*` divider lines and `DRule` bottom rule and put borders on the `DR*` fields instead.
That is a separate change and will be done separately.

## Global constraints

1. Crystal assemblies only from `C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2\`
   and the GAC. **Never the `CrystalReports.*` 13.x NuGet packages** — they save `.rpt` in a format
   Crystal XI R2 and the customer's HR software cannot open.
2. Worker stays `net48` + `x86`; `Contracts` stays `netstandard2.0` with no Crystal dependency;
   `Mcp` never references Crystal.
3. **The source `.rpt` is never modified.** Never write to `out\reports\` or `tests\fixtures\` —
   probe to a scratch path and delete after.
4. **`LayoutPlanValidator` is the single gate.**
5. No operation accepts a credential.

## Testing

Put a hard timeout on anything touching Crystal — it blocks rather than failing on several paths, and
an agent on this project once stalled ten minutes on exactly that. A genuine hang shows as no CPU
growth, not merely elapsed time.

Do the offline work first, get it building and Contracts green, and commit before live probing.

Run the suites and fix what you turn red. Contracts and Mcp take under a minute each; the worker
suite about 11 minutes. `dotnet build` must be clean. Rebuild with `.\publish.ps1` before probing.

Never create a git remote and never push. Local commits only, Conventional Commits.
