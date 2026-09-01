# Task: formatting operations (`setSectionBreak`, `addSpecialField`, `setNumberFormat`, `setCanGrow`, `setSuppress`)

Five operations taking `LayoutActions.All` from **twenty-one to twenty-six**. All are offline
property writes — none contacts the database, none needs `VIBEY_DB_PASSWORD`.

Repo root: `D:\VibeyReports`, branch `main`, clean tree.

## Why

Building three reports produced a short list of things the tool could not do, each of which forced a
manual step in the Crystal Designer that does not survive regenerating the report from
`out/scripts/`. Every operation below closes one of them:

| Gap hit | Where it bit |
|---|---|
| No page break | Report 3 is two pages of content with no way to split them |
| No page number / print date | Every report footer in all three specs; none built |
| No number formatting | `goal_id` rendered `10,311.00`; fixed by hand in the Designer, and a note now warns that regenerating reverts it |
| No "can grow" | FDP comment columns clip long free text |
| No suppression | Empty sub-report sections render as blank gaps |

## Verified SDK surface — reflected off the installed 11.5 assemblies

```
ISCRSectionFormat : EnableNewPageBefore, EnableNewPageAfter, EnableSuppress,
                    EnableSuppressIfBlank, EnableKeepTogether, EnableUnderlaySection,
                    EnableResetPageNumberAfter, EnablePrintAtBottomOfPage, BackgroundColor
ISCRObjectFormat  : EnableCanGrow, EnableSuppress, EnableKeepTogether,
                    EnableCloseAtPageBreak, HorizontalAlignment, ToolTipText,
                    TextRotationAngle, HyperlinkType, HyperlinkText
ISCRNumericFieldFormat : NDecimalPlaces, RoundingFormat, ThousandsSeparator,
                    ThousandSymbol, DecimalSymbol, EnableSuppressIfZero,
                    NegativeFormat, CurrencySymbol, ZeroValueString
ISCRFieldFormat   : CommonFormat, NumericFormat, DateFormat, TimeFormat,
                    DateTimeFormat, StringFormat, BooleanFormat
ISCRSpecialField  : SpecialType (CrSpecialFieldTypeEnum), plus the normal field members
CrSpecialFieldTypeEnum includes: crSpecialFieldTypePageNumber, crSpecialFieldTypePageNOfM,
                    crSpecialFieldTypeTotalPageCount, crSpecialFieldTypePrintDate,
                    crSpecialFieldTypePrintTime, crSpecialFieldTypeReportTitle,
                    crSpecialFieldTypeRecordNumber, crSpecialFieldTypeGroupNumber
```

Also measured, and worth knowing before you design anything: **`ISCRReportDefController` cannot add
or remove sections.** Its only Add/Modify/Remove methods are for chart objects. The five-section
shape of a report is fixed as far as this SDK is concerned; only adding a *group* creates new
sections, and that is deliberately out of scope here.

## Operations

### `setSectionBreak`

| Field | Meaning |
|---|---|
| `section` | Section to format |
| `newPageBefore` | bool, optional |
| `newPageAfter` | bool, optional |

At least one of the two booleans must be present; both may be. Sets `EnableNewPageBefore` /
`EnableNewPageAfter` on `ISCRSectionFormat`.

### `addSpecialField`

Places a Crystal Special Field — a page number, print date and so on.

| Field | Meaning |
|---|---|
| `section` | Section to place it in |
| `newName` | Name for the new object |
| `specialType` | One of the allowed names below |
| `leftTwips` / `topTwips` / `widthTwips` / `heightTwips` | Geometry, as for any add |

Accept a short, friendly `specialType` vocabulary and map it to the enum in the worker; do not make
callers write `crSpecialFieldTypePageNOfM`. Support exactly these, and reject anything else:

```
pageNumber | pageNOfM | totalPageCount | printDate | printTime | reportTitle | recordNumber
```

The validator's allowlist is the contract. Adding more later is cheap; accepting an unknown string
and failing at COM is not.

### `setNumberFormat`

| Field | Meaning |
|---|---|
| `target` | Field object to format |
| `decimalPlaces` | int, optional, 0-10 |
| `thousandsSeparator` | bool, optional |
| `suppressIfZero` | bool, optional |

At least one optional must be present. This is the `10,311.00` fix: `decimalPlaces: 0` plus
`thousandsSeparator: false`.

Note the shape: `ISCRFieldFormat.NumericFormat` is a nested object, so the applier reads the field's
format, clones it, mutates `NumericFormat`, and writes it back. Follow the existing
clone-mutate-Modify discipline; RAS objects cannot be mutated in place.

### `setCanGrow`

| Field | Meaning |
|---|---|
| `target` | Object to format |
| `canGrow` | bool, required |

### `setSuppress`

`EnableSuppress` exists on **both** `ISCRSectionFormat` and `ISCRObjectFormat` — it is one Crystal
concept with two hosts — so this is one operation that accepts either, not two operations.

| Field | Meaning |
|---|---|
| `target` | Object to suppress — **exactly one of** `target` or `section` |
| `section` | Section to suppress |
| `suppress` | bool, required |
| `suppressIfBlank` | bool, optional; **valid only with `section`** |

The validator must reject a plan supplying both `target` and `section`, neither, or
`suppressIfBlank` alongside `target`. If a reviewer would rather see this split into
`setSuppress` / `setSectionSuppress` to match the `setFillColor` / `setSectionBackground`
precedent, say so in your report — but note that pair formats two *different* properties
(`FillColor` vs `BackgroundColor`), whereas this is the same property on two hosts.

## Validator rules

- `setSectionBreak`, `setSuppress` (section form) join the `needsSection` set; `setNumberFormat`,
  `setCanGrow`, `setSuppress` (object form) join `needsTarget`; `addSpecialField` needs both
  `section` and a unique `newName`, with full geometry bounds-checked like every other add.
- `addSpecialField`'s simulated kind is `"Field"` — a special field IS a field object, and it must
  be fontable so `setFontSize` and `setBold` work on it.
- `setNumberFormat`'s target must be kind `"Field"`. Reject Text, Box, Line, Subreport.
- **Do not attempt to verify the field is numeric.** The value type is only knowable from
  `schema.AvailableFields`, which `ReportReader` deliberately clears when there is no database
  connection — a normal, supported state. Rejecting on an empty list would make the operation fail
  whenever the VPN is down, which is the trap `removeTable`'s existence check already avoids.
- `setCanGrow` only makes sense on objects that hold text: allow `"Text"` and `"Field"`, reject the
  rest.
- Every "at least one optional field present" rule above must be enforced, so a no-op operation is a
  plan error rather than a silent nothing.
- The validator stays pure: no file I/O, no Crystal, no environment access.

## Reading it back — do not skip this

This project has twice shipped a write-only property and been blinded by it; the `setSubreportLink`
review found `SubreportName` was read and then discarded, which made cross-plan linking look
impossible when it was not. **Every property these operations write must be readable through
`read_report`.**

`SectionInfo` gains `NewPageBefore`, `NewPageAfter`, `SuppressIfBlank` (it already carries
`Suppressed`).

`ObjectInfo` gains `CanGrow`, `Suppressed`, and a nullable `NumberFormat` populated only for Field
objects:

```csharp
public sealed class NumberFormatInfo
{
    public int DecimalPlaces { get; set; }
    public bool ThousandsSeparator { get; set; }
    public bool SuppressIfZero { get; set; }
}
```

Wrap the format read defensively — a non-numeric field may throw or return nothing; leave the
property null rather than failing the whole read, exactly as `ReadSubreportLinks` does.

A round-trip test that asserts a *different* property than the one written can never fail. Assert
the same property you set.

## Applier

Clone → mutate the clone → `Modify(old, new)`, as everywhere else. Wrap `COMException` with context
naming the operation and target, consistent with `AddReportObject` / `AddField` / `SetSubreportLink`.

`addSpecialField` needs a `SpecialField` created and given a `SpecialType`, then placed as a field
object. Probe it live before settling the shape — `AddField` needed `FieldValueType` set or Crystal
threw "field value type is not valid", and this is close enough to hit something similar. Document
what you measure in `docs/sdk-notes.md`, including negative results.

## Tests

**Contracts**
- Each operation accepted with valid input; rejected with a missing required field.
- `setSectionBreak` rejected when neither boolean is supplied.
- `setNumberFormat` rejected when no optional field is supplied; rejected against a Text target.
- `setNumberFormat` accepted when `AvailableFields` is empty (no numeric-type check).
- `setCanGrow` rejected against a Box or Line.
- `setSuppress` rejected with both `target` and `section`; with neither; and with
  `suppressIfBlank` alongside `target`.
- `addSpecialField` rejected on an unknown `specialType`; rejected on `newName` collision;
  rejected on out-of-bounds geometry; `setFontSize` on one is accepted (fontable).
- `LayoutActions.All` has twenty-six entries — assert it through the existing exhaustive test in
  `LayoutPlanTests`, and do not add a second weaker copy. One was deleted for going red on every
  addition without ever catching anything.

**Worker** — each through `ApplyAndReread`, which already asserts the source `.rpt` is byte-identical
- Set a page break, save, reopen, assert `newPageAfter` is true.
- Add each of the seven special-field types, save, reopen, assert an object of kind `Field` exists at
  the requested geometry.
- Set `decimalPlaces: 0`, `thousandsSeparator: false`, save, reopen, assert both round-trip.
- Set can-grow and suppress, save, reopen, assert both round-trip.

**MCP**
- `read_report` output includes the new section and object properties.

## Global constraints

1. Crystal assemblies only from `C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2\`
   and the GAC. **Never the `CrystalReports.*` 13.x NuGet packages** — they save `.rpt` in a format
   Crystal XI R2 and the customer's HR software cannot open.
2. Worker stays `net48` + `x86`; `Contracts` stays `netstandard2.0` with no Crystal dependency;
   `Mcp` never references Crystal.
3. **The source `.rpt` is never modified.**
4. **`LayoutPlanValidator` is the single gate.** `ApplyOperationsWithoutValidation` is an internal
   test-only seam; production calls only the public `Apply`.
5. No operation accepts a credential.

## Testing

Run the suites and fix what you turn red. `dotnet test` works — the earlier CLI hangs were Crystal
blocking on database enumeration with the VPN down, plus a separate wedged print subsystem, and the
renderer tests are now bounded by a 120s timeout. The full worker suite takes about 9 minutes; the
Contracts and Mcp suites take under a minute each.

`dotnet build` must be clean across the solution before you commit. Rebuild with `.\publish.ps1`
before any live probing so `dist\` is not stale — that has caught this project out three times.

Never create a git remote and never push. Local commits only. Conventional Commits.
