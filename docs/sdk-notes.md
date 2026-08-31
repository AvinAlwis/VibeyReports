# Crystal Reports XI R2 RAS SDK — notes from measurement

This project's implementation plan (`docs/superpowers/plans/2026-08-31-vibey-reports.md`) was
written before anyone had exercised the Crystal Reports XI R2 in-process RAS SDK on this machine.
During execution, nine assumptions in that plan turned out to be factually wrong, each confirmed by
direct measurement (reflection against the installed 11.5 assemblies, or a temporary diagnostic
test run against a real fixture) rather than by guessing. **Where this file and the plan disagree,
this file is authoritative.**

The full per-task write-ups this file summarises live in `docs/supplements/`. Anyone changing
`VibeyReports.CrystalWorker` should treat both as required reading before touching the SDK surface.

---

## 1. `new ReportClientDocument()` hangs; open through the classic engine instead

**Plan assumed:** construct `CrystalDecisions.ReportAppServer.ClientDoc.ReportClientDocument`
directly and call `.Open(path, 0)`.

**Actually true:** doing this activates a standalone RAS COM server that attempts a same-machine
TCP self-connect to port 1566, addressed by the machine's real hostname/IP rather than loopback.
On this VM's networking setup that self-connect is silently dropped and the call hangs until the
OS's TCP connect timeout (~20-24s), surfacing as `COMException: Failed to connect to server
"<hostname>"`.

**Fix:** open with the classic engine instead —
`CrystalDecisions.CrystalReports.Engine.ReportDocument.Load(path)` — and reach the RAS object model
through `ReportDocument.ReportClientDocument`. This costs ~2ms, makes no network connection, and
exposes the full object model (`ReportDefController`, `SaveAs`, etc.) that direct construction
would have given. This is how `CrystalSession.Open` works today.

**Verified:** `task-4-report.md` captured the hang with `netstat` (`SYN_SENT` to the machine's own
LAN IP on port 1566, never acknowledged); the engine-based open was then measured to work
end-to-end (open → read → modify → save → reopen) with no hang, across all four fixtures.

---

## 2. `ISCRReportDefinition` has no `Sections` property

**Plan assumed:** `doc.ReportDefController.ReportDefinition.Sections`.

**Actually true:** reflecting the real `CrystalDecisions.ReportAppServer.ReportDefModel.dll` shows
`ISCRReportDefinition` exposes `Areas` (plus named single-area properties like `DetailArea`), not
`Sections`. Sections live one level down, on `ISCRArea.Sections` — each report band (page header,
report header, group header, details, group footer, report footer, page footer) is its own `Area`,
and each `Area` carries its own `Sections` collection.

**Fix:** walk `Areas`, then each area's `Sections`:

```csharp
var areas = doc.ReportDefController.ReportDefinition.Areas;
for (var a = 0; a < areas.Count; a++)
{
    var sections = areas[a].Sections;
    for (var i = 0; i < sections.Count; i++) { /* ... */ }
}
```

The section's parent `Area.Kind` (not the section's own name — RAS names them generically,
`"Section1"`, `"Section2"`, ...) is also the only way to classify a section into a recognisable
band (`ReportHeader`, `PageHeader`, `Details`, etc.); a name-based heuristic returns nothing useful.

**Verified:** confirmed by reflection against the installed 11.5.3300.0 assembly (`task-4-report.md`
Deviation 4), then exercised live across all four fixtures with no further surprises
(`task-6-supplement.md` C2).

---

## 3. `ISCRByteArray` has no `get_Stream()` — use `.ByteArray` directly

**Plan assumed:** `PrintOutputController.Export(...)` returns something exposing
`get_Stream()`/a `Stream`.

**Actually true:** reflecting `ISCRByteArray` shows no stream member anywhere on the type. It
exposes `Count`, `ByteArray` (`byte[]`, the export bytes directly), `getref_ByteArray()`, `Save`,
`AttachArray`, `DetachArray`.

**Fix:** `ReportRenderer.ExportPdf` reads `byteArray.ByteArray` directly — no stream plumbing at
all.

**Verified:** exercised against three of the four fixtures before this task was even dispatched,
producing valid `%PDF-...%%EOF` output each time (`task-7-supplement.md` C1). The fourth fixture
(`Documents.rpt`) fails for an unrelated, legitimate reason — see note 6 below.

---

## 4. Fonts are RAS `ISCRFont`, not `System.Drawing.Font`

**Plan assumed:** `WithFont` takes a `Func<Font, Font>` and constructs
`new System.Drawing.Font(name, size, style)`.

**Actually true:** `FontColor.Font` (on both `ISCRFieldObject` and `ISCRTextObject`) is
`CrystalDecisions.ReportAppServer.ReportDefModel.Font` (`ISCRFont`) — an entirely different,
**mutable** type: `string Name`, `decimal Size` (not `float`), `bool Bold`/`Italic`/`Underline`/
`Strikethrough`, `short Weight`, `short Charset`. There is no constructor to call; you mutate the
existing instance's properties in place.

**Fix:** `WithFont` takes `Action<ISCRFont> mutate` and applies it to `fontColor.Font` directly,
with an explicit `(decimal)` cast wherever a `float`/`float?` from the DTO layer meets `Size` (no
implicit conversion exists).

**Verified:** confirmed by reflection against the installed assembly (`task-6-supplement.md` C1),
then confirmed live — mutating the clone's `ISCRFont` in place and calling
`ReportObjectController.Modify(old, new)` does persist through save/reopen, so the "construct a
replacement `FontClass`" fallback the supplement described as a contingency was never needed.

A related, separately-measured wrinkle (`task-6c-report.md`): a **freshly added** `TextObjectClass`
carries its font in two places that must agree — the outer `TextObjectClass.FontColor` and the
paragraph run's `ParagraphTextElementClass.FontColor` — and `Add()` does not propagate one to the
other. `AddText` sets both explicitly for this reason.

That raised a follow-up question for the *modify* path (`setFont`/`setFontSize`/`setBold` on an
object that already exists): does `ReportObjectController.Modify(old, new)` propagate a change to
the outer `FontColor` down to the paragraph run on its own, or does the run keep its old font while
the outer property silently disagrees with what actually renders? This was settled by direct
measurement, not assumption: apply `setBold` (and separately, `setFont`+`setFontSize`) to an
existing Text object via the normal `LayoutApplier.Apply` path, save, reopen, and read **both**
`text.FontColor.Font` (the outer property) and
`((ISCRParagraph)text.Paragraphs[0]).ParagraphElements[0].FontColor.Font` (the run that actually
renders) directly. Measured result: they agree in every case tried — `Bold` went `false → true` on
both simultaneously, and `Name`/`Size` both landed on the requested values on both. **`Modify()`
does propagate the outer font change down to the run**, so `WithFont`'s existing outer-only
mutation needed no change.

---

## 5. A freshly constructed `FieldObjectClass` needs `FieldValueType` set explicitly

**Plan assumed:** constructing a `FieldObjectClass` with `Name`/`DataSource`/geometry set is enough
for `ReportObjectController.Add` to accept it — RAS would presumably infer the value type from the
`DataSource` string.

**Actually true:** it does not infer anything. A bare construction leaves `FieldValueType`
(`CrFieldValueTypeEnum`) at its CLR default, and `Add` rejects it outright with
`COMException: The field value type is not valid.`

**Fix:** resolve the real `CrFieldValueTypeEnum` for the target field by walking
`doc.DatabaseController.Database.Tables → DataFields → ISCRDBField` (the same walk
`ReportReader.Read` already does for `AvailableFields`), matching on `FormulaForm`, and set
`field.FieldValueType = dbField.Type` before calling `Add`. The same lookup also yields the
report's own canonical `FormulaForm` string, which is bound as `DataSource` instead of the
caller's (possibly case-variant) string, so a case-insensitive match at the validator boundary can
never bind to a value RAS itself cannot resolve.

**Verified:** `task-6b-report.md` — the fix was applied and low-level construction was proven to
work end-to-end (`DataSource` persists through save/reopen) once `FieldValueType` was set.

---

## 6. The documented `AddByName` fallback has no reachable success case

**Plan assumed:** if direct `FieldObjectClass` construction fails, fall back to
`ReportObjectController.AddByName(fieldName, section, top, left)`, then reposition and rename the
result with the existing `Modify`-based helper.

**Actually true, measured by deliberately exercising the fallback before the real fix was found:**

- `AddByName`'s `fieldName` parameter wants the `FormulaForm` expression (`"{Table.Field}"`), not
  the raw field name — passing the raw name throws `COMException: Database Field Not Found`.
- `AddByName` creates **two** report objects (a `FieldHeading` text object plus the field itself),
  both placed at RAS's own default position, not the caller's requested position.
- `ReportObjectController.Modify()` cannot rename an object:
  `COMException: Cannot change report object name.` Reflecting `ISCRReportObjectController`'s full
  method set (`Add`, `Remove`, `Modify`, `GetAllReportObjects`, `GetReportObjectsByKind`,
  `AddByName`, `ImportPicture`) confirms there is no rename/`SetName` operation anywhere on the
  controller.

So the fallback's own second half — "locate the created object and give it the requested name" —
is not achievable through this SDK surface at all. Once the real fix (note 5 above) made the
primary path succeed on every fixture, the fallback was deleted entirely rather than kept as a path
that could only ever fail after already mutating the live document.

**Verified:** `task-6b-report.md` fix round 1, F1/F2. `AddField` now has exactly one path: resolve
the `ISCRDBField`, construct, `Add`, wrap any `COMException` with full context. No fallback.

---

## 7. `Console.OutputEncoding = Encoding.UTF8` can emit a byte-order mark

**Plan assumed:** `Console.OutputEncoding = Encoding.UTF8;` is enough to make the worker's stdout
BOM-less UTF-8.

**Actually true:** `Encoding.UTF8` is constructed with `encoderShouldEmitUTF8Identifier: true`, so
assigning it can emit a UTF-8 byte-order mark (`EF BB BF`) ahead of the JSON response. A leading BOM
makes `JsonSerializer.Deserialize` throw `'0xEF' is an invalid start of a value` on the client side —
indistinguishable from a worker crash across the process boundary, and invisible to a plain string
comparison (a `StreamReader` on the reading side silently swallows the BOM).

**Fix:** `Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);`

**Verified and regression-guarded:** `Worker_WritesNoByteOrderMarkBeforeItsJson`
(`ProgramEndToEndTests.cs`) reads stdout as raw bytes (bypassing any decoder that would hide a BOM)
and asserts the first byte is `{` (`0x7B`), not `0xEF`.

---

## 8. Worker stdin also needs an explicit BOM-less UTF-8 encoding, on both sides

**Plan assumed (round-1 template bug, not specific to this project's deviations):** only
`StandardOutputEncoding`/`StandardErrorEncoding` were pinned to UTF-8 on the client's
`ProcessStartInfo`; `StandardInputEncoding` was left unset, and the worker read stdin via
`Console.In.ReadToEnd()` with no explicit input encoding either.

**Actually true:** an unset `StandardInputEncoding` falls back to `Console.InputEncoding`,
typically the OEM/ANSI code page on Windows — a different encoding from the BOM-less UTF-8 used
everywhere else in the protocol. A report path or object name containing non-ASCII characters
(realistic for an HR product deployed across the Philippines and Sri Lanka) goes in corrupted.

**Fix, both sides of the pipe:**
- Client (`CrystalWorkerClient`'s `ProcessStartInfo`):
  `StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)`.
- Worker (`Program.cs`): rather than assigning `Console.InputEncoding` directly (which can throw
  "The handle is invalid" when stdin is redirected, on some Windows configurations), read the raw
  stdin stream and decode it explicitly with the same BOM-less `UTF8Encoding`:
  ```csharp
  using var stdin = Console.OpenStandardInput();
  using var reader = new StreamReader(stdin, new UTF8Encoding(false));
  var raw = reader.ReadToEnd();
  ```

**Verified:** `ReadAsync_RoundTripsANonAsciiReportPath` (`CrystalWorkerClientTests.cs`) copies a
fixture to a temp path containing `café`/`niño`, reads it through the real worker process, and
asserts the returned schema's `ReportPath` still contains those characters.

---

## 9. `ImageContentBlock.FromBytes` avoids a double-encoding trap the plan's approach would have hit

**Plan assumed:** an approach to building the preview image's `ImageContentBlock` that would have
base64-encoded the PNG bytes itself before handing them to the SDK.

**Actually true:** `ImageContentBlock.Data` is documented as already-base64 bytes, and
`ImageContentBlock.FromBytes` takes **raw** bytes and encodes them lazily. Pre-encoding before
calling `FromBytes` would have produced valid-*looking* but garbage (double-encoded) image data —
the kind of defect that renders as a broken image with no exception anywhere, and that a
test asserting only "response contains an ImageContentBlock" would not catch.

**Fix:** call `ImageContentBlock.FromBytes(png, "image/png")` with the raw PNG bytes, no
pre-encoding.

**Verified:** `task-10-findings-round1.md` confirmed this by inspecting the real
`ModelContextProtocol.Core` 1.2.0 assembly rather than assuming the plan's approach was safe; the
implementation had already avoided the trap, but no test at the time asserted the PNG-magic-number
bytes actually round-tripped correctly, so that assertion was added
(`PreviewReportTests`/`ReportToolsTests` — `png[0..3]` equals the PNG magic number).

---

## Where to look next

- `docs/supplements/` — the six per-task supplement files these notes are drawn from
  (`task-5-supplement.md` through `task-11-supplement.md`), each written from direct measurement
  against the installed SDK before its task was dispatched.
- `docs/superpowers/plans/2026-08-31-vibey-reports.md` — the original plan. Predates all of the
  above; this file wins wherever they disagree.
