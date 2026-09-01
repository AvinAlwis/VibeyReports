# Task 6 supplement — corrections to the brief, from verified SDK reality

The brief for Task 6 was written before the SDK was exercised. Three parts of it are now known to be
wrong. **Where this file and the brief disagree, this file wins.** Everything else in the brief stands.

---

## C1 — Fonts are RAS `ISCRFont`, not `System.Drawing.Font`

The brief's `WithFont` helper takes a `Func<Font, Font>` and constructs `new Font(name, size, style)`.
That will not compile. I reflected the installed assembly:

```
ISCRFont:
  String  Name
  Decimal Size          <- decimal, NOT float
  Boolean Bold
  Boolean Italic
  Boolean Underline
  Boolean Strikethrough
  Int16   Weight
  Int16   Charset
```

It is a **mutable RAS object**, reached via `FontColor.Font` on `ISCRFieldObject` and
`ISCRTextObject`. Task 5's reader already reads it successfully this way, so the read path is proven.

### Replace the brief's `WithFont` with this

```csharp
/// <summary>
/// Applies a mutation to whichever object kinds carry a font.
/// ISCRFont is mutable, so we set properties directly rather than constructing a new font.
/// </summary>
private static void WithFont(ISCRReportObject obj, Action<ISCRFont> mutate)
{
    ISCRFontColor fontColor;
    switch (obj)
    {
        case ISCRFieldObject field: fontColor = field.FontColor; break;
        case ISCRTextObject text:   fontColor = text.FontColor;  break;
        default:
            throw new InvalidOperationException(
                $"Object \"{obj.Name}\" ({obj.Kind}) has no font to change.");
    }

    if (fontColor?.Font == null)
        throw new InvalidOperationException($"Object \"{obj.Name}\" has no font object.");

    mutate(fontColor.Font);
}
```

### Replace the three font call sites

```csharp
case LayoutActions.SetFont:
    ModifyObject(doc, op.Target, o => WithFont(o, f => f.Name = op.FontName));
    break;

case LayoutActions.SetFontSize:
    ModifyObject(doc, op.Target, o => WithFont(o, f => f.Size = (decimal)op.FontSizePt.Value));
    break;

case LayoutActions.SetBold:
    ModifyObject(doc, op.Target, o => WithFont(o, f => f.Bold = op.Bold.Value));
    break;
```

Note the explicit `(decimal)` cast — `LayoutOperation.FontSizePt` is `float?` and `ISCRFont.Size` is
`decimal`. There is no implicit conversion; omitting the cast is a compile error.

Drop `using System.Drawing;` from `LayoutApplier.cs` if nothing else needs it.

### If the font change does not survive save-and-reopen

The brief's test `Apply_SetsFontNameSizeAndBoldTogether` asserts the font persists through
`SaveAs` → reopen. I could not settle this from my PowerShell harness (late binding cannot see the
derived interface members), so **your test is the thing that decides it.**

Mutating the clone's font in place is the approach to try first — it is the natural reading of a
mutable `ISCRFont`. If the test shows the change does not persist, the likely cause is that
`FontColor.Font` on a clone still points at the original's font instance, so `Modify` sees no
difference. In that case, construct a fresh font and assign it:

```csharp
var replacement = new FontClass
{
    Name = existing.Name, Size = existing.Size, Bold = existing.Bold,
    Italic = existing.Italic, Underline = existing.Underline,
    Strikethrough = existing.Strikethrough, Weight = existing.Weight, Charset = existing.Charset
};
// ...apply the requested change to `replacement`, then:
fontColor.Font = replacement;
```

`FontClass` exists in `CrystalDecisions.ReportAppServer.ReportDefModel` — I confirmed it by
reflection. **Do not weaken the test either way**; persistence is the whole point of the operation.
Say in your report which path worked.

---

## C2 — Sections live on Areas

`ISCRReportDefinition` has **no** `Sections` property. The brief's `FindSection` has already been
corrected in the plan to walk `Areas`, but confirm yours does:

```csharp
var areas = doc.ReportDefController.ReportDefinition.Areas;
for (var a = 0; a < areas.Count; a++)
{
    var sections = areas[a].Sections;
    for (var i = 0; i < sections.Count; i++) { /* match by name */ }
}
```

If `areas[a]` does not compile as an indexer through the engine's RCD wrapper, use `areas.Item(a)`.

---

## C3 — `Move` / `Resize` must maintain `Right` and `Bottom` on lines and boxes

Flagged during the Task 3 review and carried forward. `ISCRLineObject` and `ISCRBoxObject` expose
`Right` and `Bottom` **in addition to** `Left/Top/Width/Height`, and the brief's `AddLine`/`AddBox`
set them explicitly — but the brief's `Move` and `Resize` set only `Left/Top/Width/Height`.

Moving or resizing a line or box therefore leaves stale endpoint geometry, and the object will very
likely render in its old position or at its old size.

### Fix

After mutating position or size in `Move` and `Resize`, update the endpoints for the two kinds that
carry them:

```csharp
private static void SyncEndpoints(ISCRReportObject obj)
{
    switch (obj)
    {
        case ISCRLineObject line:
            line.Right  = line.Left + line.Width;
            line.Bottom = line.Top  + line.Height;
            break;
        case ISCRBoxObject box:
            box.Right  = box.Left + box.Width;
            box.Bottom = box.Top  + box.Height;
            break;
    }
}
```

Call it at the end of the `Move` and `Resize` mutations, e.g.

```csharp
case LayoutActions.Move:
    ModifyObject(doc, op.Target, o =>
    {
        o.Left = op.LeftTwips.Value;
        o.Top  = op.TopTwips.Value;
        SyncEndpoints(o);
    });
    break;
```

### Test to add

The brief has no coverage for moving a line, which is exactly where this bites:

```csharp
[Fact]
public void Apply_MovingALineKeepsItsEndpointsConsistent()
{
    string sectionName;
    using (var s = CrystalSession.Open(Fixtures.SampleReport))
        sectionName = ReportReader.Read(s).Sections.First(x => x.HeightTwips >= 400).Name;

    var plan = new LayoutPlan
    {
        Operations =
        {
            new LayoutOperation
            {
                Action = LayoutActions.AddLine, Section = sectionName, NewName = "EndpointRule",
                LeftTwips = 0, TopTwips = 100, WidthTwips = 2880, HeightTwips = 0
            },
            new LayoutOperation
            {
                Action = LayoutActions.Move, Target = "EndpointRule",
                LeftTwips = 720, TopTwips = 200
            }
        }
    };

    var schema = ApplyAndReread(plan, out var saved);
    try
    {
        var line = schema.Sections.SelectMany(s => s.Objects).Single(o => o.Name == "EndpointRule");
        line.LeftTwips.Should().Be(720);
        line.TopTwips.Should().Be(200);
        line.WidthTwips.Should().Be(2880);
    }
    finally { if (File.Exists(saved)) File.Delete(saved); }
}
```

---

## Unchanged and still binding

Everything else in the brief stands, in particular:

- **The clone-modify-`Modify` idiom.** RAS objects cannot be mutated in place on the live document —
  clone, mutate the clone, then `ReportObjectController.Modify(old, new)`. I verified this whole path
  end to end (clone → mutate → `Modify` → `SaveAs` → reopen → change persisted, source untouched).
- **Validate before applying.** `LayoutApplier.Apply` calls `LayoutPlanValidator.Validate` first and
  throws `InvalidPlanException` before touching the report if the plan is invalid.
- **`addField` is NOT part of Task 6.** It is Task 6b, dispatched separately after this task is
  reviewed. Do not implement it, and do not add `AddField` to `LayoutActions`.
