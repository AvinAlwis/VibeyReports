# Task: add colour operations

Add four colour operations to Vibey Reports, plus colour **reading** so the AI can see current state.
This is what makes the user's target design — dark banner bars with light text, coloured status
values — reachable.

Repo root: `D:\VibeyReports`, branch `feat/remove-object` (colour work continues on the same branch).

## Verified SDK facts — I reflected these off the installed 11.5 assemblies

```
ISCRFontColor.Color          UInt32   -> text colour (Text, Field, FieldHeading)
ISCRBoxObject.FillColor      UInt32   -> box fill
ISCRBoxObject.LineColor      UInt32   -> box outline
ISCRLineObject.LineColor     UInt32   -> line colour
ISCRSectionFormat.BackgroundColor UInt32 -> SECTION background (the banner bars)
ISCRBorder.BackgroundColor / BorderColor  UInt32  (not used by this task)
```

`Section.Format` is reachable — `ReportReader` already does `section.Format.EnableSuppress`.

**I measured the round-trip myself**: setting `FontColor.Color = 255` on a real object, saving and
reopening returns `255`. Colour persists correctly, and the property reads via the RCW without
trouble. Default for `FontColor.Color` is `0` (black).

## The one genuine unknown: channel order

Crystal stores colour as `UInt32`. `255` is **red** under the Win32 `COLORREF` convention
(`0x00BBGGRR`) or **blue** under `0x00RRGGBB`. I have not determined which.

**Work to the COLORREF hypothesis** — Crystal historically uses it:

```csharp
// "#RRGGBB" -> COLORREF 0x00BBGGRR
uint colorRef = (uint)(b << 16 | g << 8 | r);
```

Put that conversion in **exactly one place** (a small helper in Contracts, e.g. `ColorRef.FromHex` /
`ColorRef.ToHex`) so that if the hypothesis is wrong, it is a one-line fix rather than a hunt.
Write a unit test that pins the conversion both ways.

The user will verify visually against a swatch report (see "Swatch report" below) and report back.
**Do not spend time trying to prove the channel order yourself** — the human check is faster and
definitive.

## Operations

Add four constants to `LayoutActions` (taking it to **sixteen** total) and one DTO field.

| Action | Takes | Applies to | Writes |
|---|---|---|---|
| `setTextColor` | `target`, `color` | Text, Field, FieldHeading | `FontColor.Color` |
| `setFillColor` | `target`, `color` | Box | `BoxObject.FillColor` |
| `setLineColor` | `target`, `color` | Line, Box | `LineColor` |
| `setSectionBackground` | `section`, `color` | any section | `Section.Format.BackgroundColor` |

`LayoutOperation` gains:

```csharp
/// <summary>Colour as "#RRGGBB", e.g. "#1F2A37". Required by the colour operations.</summary>
public string? Color { get; set; }
```

## Validator rules

- `color` must match `^#[0-9A-Fa-f]{6}$` — reject anything else with a message showing the expected
  form. This is the only input the AI can get wrong in a hundred ways, so the message matters.
- `setTextColor` — target must be a fontable kind. **Reuse the existing `IsFontable` helper**; do not
  duplicate the kind list. (It already includes `FieldHeading`.)
- `setFillColor` — target kind must be `Box`.
- `setLineColor` — target kind must be `Line` or `Box`.
- `setSectionBackground` — the section must exist.
- All four join the appropriate `needsTarget` / `needsSection` sets.

Kind checks must use the **simulated** kind, so an object added earlier in the same plan is handled
correctly — e.g. `addBox` then `setFillColor` on it must be **accepted**.

## Reading colour back — do not skip this

A write-only colour API leaves the AI blind: it cannot see existing colours, cannot preserve them,
and cannot verify its own changes. This project already learned that lesson the hard way with fonts.

- `ObjectInfo` gains `TextColorHex`, `FillColorHex`, `LineColorHex` (all `string?`, `"#RRGGBB"`,
  null when not applicable to the kind).
- `SectionInfo` gains `BackgroundColorHex` (`string?`).
- `ReportReader` populates them via the same `ColorRef` helper.

**Measure and document what Crystal uses for "no colour"/transparent** — a fill that has never been
set may come back as a sentinel rather than a real colour. If so, map it to `null` rather than
reporting a bogus hex value, and say in your report what the sentinel was.

## Tool description — `ReportTools.cs`

Add the four actions to `apply_layout`'s action list with their required fields, and state that
colours are `#RRGGBB`. Mention that `setSectionBackground` is how you get a full-width banner band
(rather than drawing a box), since that is the non-obvious one.

## Tests

**Contracts**
- `ColorRef.FromHex` / `ToHex` round-trip, including the channel order under the COLORREF hypothesis.
- Invalid colour strings rejected: missing `#`, wrong length, non-hex characters, `null`.
- Each of the four operations: accepted when valid; rejected on wrong target kind
  (`setFillColor` on a Text, `setLineColor` on a Field, `setTextColor` on a Line).
- `addBox` then `setFillColor` on that new box in the same plan is **accepted**.
- `LayoutActions.All` has sixteen entries.

**Worker**
- Set a text colour, save, reopen, assert `TextColorHex` comes back as the value set.
- Set a section background, save, reopen, assert `BackgroundColorHex`.
- Set a box fill and line colour, save, reopen, assert both.
- The source `.rpt` is byte-identical afterwards (`ApplyAndReread` already asserts this).

**MCP**
- `read_report`'s JSON includes the colour fields.

## Swatch report — the deliverable that settles the channel order

After the tests pass, generate `D:\VibeyReports\out\ColourSwatch.rpt` from
`tests/fixtures/SampleReport.rpt`: a column of text objects, each labelled with its intended colour
name and hex, set to that colour — RED `#FF0000`, GREEN `#00AA00`, BLUE `#0000FF`, plus a couple of
boxes with fills and one section with a background. Render it to
`D:\VibeyReports\out\ColourSwatch.pdf` too.

The user will open it and tell us whether the labels match the colours. If red and blue are swapped,
the fix is the one line in `ColorRef`.

## Global constraints

1. Crystal assemblies only from `C:\Program Files (x86)\Business Objects\Common\3.5\managed\dotnet2\`
   (RAS) and the GAC. **Never the `CrystalReports.*` 13.x NuGet packages** — they save `.rpt` in a
   format Crystal XI R2 and the customer's HR software cannot open.
2. Worker and its test project stay `net48` + `<PlatformTarget>x86</PlatformTarget>`.
3. `VibeyReports.Contracts` stays `netstandard2.0` with **no** Crystal dependency — the `ColorRef`
   helper lives there and must be pure.
4. `VibeyReports.Mcp` never references Crystal.
5. **The source `.rpt` is never modified.**
6. **`LayoutPlanValidator` is the single gate.** `LayoutApplier.ApplyOperationsWithoutValidation` is
   an internal test-only seam; production code calls only the public `Apply`.
7. Geometry in twips (1440 = 1 inch).

## Testing — READ THIS

**Do not run the test suites.** CLI `dotnet test` runs on this machine hang intermittently — a known
environment issue, not a code fault. The user runs tests from Visual Studio and reports results.

You may run `dotnet build` freely to check compilation. Make sure the whole solution builds with zero
errors before you commit, and say so in your report.

If you believe a test would fail, say which and why rather than running it.

## Do not

- Do not dispatch subagents. Review comes from me after your report.
- Never create a git remote and never push. Local commits only. Conventional Commits.
- Do not weaken or delete an existing test. If a required assertion looks factually wrong, **stop and
  tell me** — implementers on this project have correctly done that several times when my
  instructions were wrong, and they were right every time.

## Report

Write your report to `D:\VibeyReports\.superpowers\sdd\colour\report.md`.

Include: what you changed, the exact `ColorRef` conversion you used, what Crystal returns for an
unset fill colour (the transparent sentinel), the build output, which tests you wrote and what you
expect them to prove, and anything about the SDK that surprised you.

**Return to me only:** status, commit SHA (short), a one-line build summary, and concerns. Keep it short.
