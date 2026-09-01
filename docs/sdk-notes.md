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

## setSubreportLink cannot feed a stored-procedure parameter (measured 2026-09-01)

`SubreportLink.LinkedParameterName` does **not** round-trip. Written as `"performance_cycle_id"`,
Crystal discards it and stores its own auto-generated parameter instead:

```
main  = {sp_perf_goal_align_cascade;1.performance_cycle_id}
sub   = {sp_perf_goal_align_detail;1.goal_id}
param = {?Pm-sp_perf_goal_align_cascade;1.performance_cycle_id}
```

`setSubreportLink` therefore expresses only a Crystal **field link** — main field to an existing
sub-report *data field*, filtered through a `{?Pm-...}` parameter Crystal creates itself. It cannot
target a stored procedure's declared parameter.

Also measured: `SubreportFieldName` must name a real sub-report data field. Parameter-style forms
(`{?x}`, `{?@x}`, `{?Pm-x}`, bare `@x`) are all rejected with COM "Invalid field name."

**Consequence for parameter-driven sub-reports:** the sub-report's result set must *return* the
column being linked on. If the procedure takes the value only as a parameter and does not select it
back, no field link can be built and the Designer's Insert Subreport wizard is the fallback.

## A sub-report has two names, and only one of them links (measured 2026-09-01)

`ImportSubreportEx(Name, ...)`'s `Name` argument does **not** become the placed report object's
`Name`. Crystal auto-numbers the container object itself (`"Subreport1"`, `"Subreport2"`, ...) and
stores the supplied string as `ISCRSubreportObject.SubreportName`. The two are different strings for
the same sub-report, and they belong to different name-spaces:

| Name | Read from | Used by |
|---|---|---|
| `ro.Name` (`"Subreport1"`) | `GetAllReportObjects()` | `ReportObjectController.Modify/Remove` — so `move`, `resize`, `setAlignment`, `removeObject` |
| `SubreportName` (`"GoalDetail"`) | `ISCRSubreportObject.SubreportName` | `SubreportController.GetSubreportLinks/SetSubreportLinks/GetSubreport` — so `setSubreportLink` |

Measured against `out/reports/PMSV10_GoalAlignCascade.subreport.rpt`:

- `SetSubreportLinks("Subreport1", ...)` fails with COM **`"This value is write-only."`** — a real,
  loud failure, not a silent no-op.
- `SetSubreportLinks("GoalDetail", ...)` succeeds, and the link reads back through
  `GetSubreportLinks("GoalDetail")` after save/reopen.
- **Cross-plan linking works.** A sub-report embedded by an earlier save can be linked later, given
  its `SubreportName`; a second, separate apply against an already-linked file appends and preserves
  the first link. The earlier claim that only same-plan linking is supported was a consequence of
  never reporting `SubreportName`, not an SDK limitation.
- `GetSubreportLinks` returns the real collection whenever links exist, so the append path is not
  relying on the fallback in practice.

`SubreportController.GetSubreportNames()` is the authority on which names `SetSubreportLinks` will
accept, and is worth resolving against before the call: it turns an unaddressable name into a
message that lists what does exist, and canonicalises casing (COM is case-sensitive here; the
validator is not).

## Sub-report link field pairs are type-checked by Crystal (measured 2026-09-01)

Linking a String main-report field to a Number sub-report field is refused at save time with COM
"Invalid value type." Measured: `{...cascade;1.employee_name}` (String) to
`{...detail;1.goal_id}` (Number) fails; `employee_name` to `goal_name` (String to String) succeeds.

Note what this does NOT protect against: the check is on value *type*, not meaning. Linking
`performance_cycle_id` to `goal_id` passes cleanly because both are Number, while being semantically
nonsense. Type compatibility is not evidence that a link is correct.

---

## The data-source table operations (measured 2026-09-01)

Everything below was measured against the installed 11.5 assemblies via a purpose-built 32-bit
probe (`ISCDReportClientDocument` reached the usual way, through
`ReportDocument.Load(...).ReportClientDocument`), against `out/reports/PMSV10_GoalAlignCascade.rpt`
and the four fixtures in `tests/fixtures/`. Three of the four expectations the brief set out were
contradicted by measurement.

### The shape of a table in these reports

`out/reports/PMSV10_GoalAlignCascade.rpt` is bound to **three** stored procedures, not two:
`sp_perf_goal_align_cascade;1` (12 fields, 5 parameters), `sp_perf_goal_align_detail;1` (5 fields,
2 parameters) and `sp_perf_company_logo;1` (1 field, 0 parameters).

| Property | Value |
|---|---|
| `ClassName` | `CrystalReports.Procedure` (every table in every PeoplesHR fixture) |
| `Name` | `sp_perf_goal_align_cascade;1` — the `;1` overload suffix is part of the name |
| `Alias` | identical to `Name` |
| `QualifiedName` | `hrmmain_philippinesdev.PeoplesHR.sp_perf_goal_align_cascade;1` — catalog.schema.name, **ending with `Name` verbatim, `;1` included** |
| `Parameters` | populated from the report's own saved metadata, no database needed |
| `ConnectionInfo.UserName` | **populated** (`sgdev01db01_devlogin`) |
| `ConnectionInfo.Password` | **null** — Crystal does not persist passwords |

`ConnectionInfo.Attributes` is a `PropertyBag` carrying `Database DLL`, `QE_DatabaseName`,
`QE_DatabaseType`, `QE_ServerDescription`, `QE_SQLDB`, `SSO Enabled` and a nested
`QE_LogonProperties` bag (provider, data source, catalog, timeouts). No password anywhere.

That a report *does* persist a user name is the reason `LayoutOperation` deliberately has no field
that could carry one: a plan must never become a place where a credential is written down, and
nothing in the applier reads `UserName`/`Password` either.

### `RemoveTable` does not protect bound fields — it silently deletes them

The expectation was that Crystal might refuse to remove a table while report objects are still
bound to it. It is worse than that: it removes the table **and the bound objects with it**, without
reporting anything.

Measured on `PMSV10_GoalAlignCascade.rpt`, removing all three tables in turn:

```
RemoveTable sp_perf_goal_align_cascade;1  -> ok, objects 70 -> 59
RemoveTable sp_perf_goal_align_detail;1   -> ok, objects 59 -> 54   (DR0..DR4, the five bound fields)
RemoveTable sp_perf_company_logo;1        -> ok, objects 54 -> 53
```

Confirmed by reopening the saved file: `DR0`–`DR4` are simply gone. Nothing asks, nothing warns,
and `ApplyResult.RemovedObjects` would not list them because no `removeObject` was ever issued.

**`LayoutPlanValidator` is therefore the only protection**, and its rule — refuse `removeTable`
while any surviving object's `DataSource` starts with `"{" + alias + "."` — is not belt-and-braces.
It is the mechanism.

Removing the last remaining table is also allowed (`SampleReport.rpt` goes to zero tables), so
there is no "a report must keep one table" backstop either.

### Table links are irrelevant to `RemoveTable`

The brief's hypothesis was that a table participating in a `TableLink` might be refused until the
link is removed. Measured both directions; it is false.

- **Linked tables remove cleanly, and Crystal drops the link itself.** On
  `PMSV10_GoalAlignCascade.rpt`: `AddTableLink(cascade -> detail)`, then
  `RemoveTable("sp_perf_goal_align_detail;1")` → succeeded, and `Database.TableLinks` went `1 -> 0`
  without being touched.
- **Removing the link first does not rescue a refusal.** On `tests/fixtures/Documents.rpt`,
  `RemoveTable("Lines")` throws `COMException: Unable to remove table 'Lines'.` Removing its one
  `TableLink` first and retrying throws the *same* error. And `"CompanyInfo"` in the same report,
  which participates in **no** link at all, is refused identically.

So a speculative `RemoveTableLink` pre-step would destroy real links without ever unblocking a
removal. `LayoutApplier` deliberately has none.

### When Crystal *does* refuse a removal

The refusal is about other in-report references, not links. `PMSV10_IndPerfOverview.rpt` gives the
full message:

> Unable to remove table 'sp_perf_ind_perf_overview;1'. **There are still fields in the report from
> this table. Please clear them before removing the table.**

— and it says that even after every `Field` *object* bound to the table has been removed, so what
remains is a formula, a record-selection formula, a group or a sort. All of those are outside what
this tool edits, so the applier wraps the `COMException` with that explanation and points at the
Crystal Designer. `Documents.rpt` refuses for the same reason (its fields are all bound through
`{@...}` formulas).

### `AddTable` and `SetTableLocation` require a live database logon

This is the finding that most constrains the feature, and it was measured, not inferred.

`ISCRDatabaseController.AddTable` **always contacts the database server**. Against
`PMSV10_GoalAlignCascade.rpt` it throws:

> `COMException: Logon failed. Unable to connect: incorrect log on parameters.`

because the cloned `ConnectionInfo` carries the server, database and user name but no password.
The failure is identical across every variation that could plausibly matter:

| Variation tried | Result |
|---|---|
| `ProcedureClass` | Logon failed |
| `TableClass` | Logon failed |
| `QualifiedName` = `catalog.schema.newName` | Logon failed |
| `QualifiedName` = bare `newName` | Logon failed |
| `QualifiedName` = the source table's verbatim | Logon failed |
| `Parameters` cloned from the source procedure | Logon failed |
| `Parameters` left empty | Logon failed |
| full `source.Clone(true)`, carrying the source's `DataFields` | Logon failed |

The call never gets far enough for the table's shape to matter, so **the brief's questions about
`ProcedureClass` vs `TableClass` and the required `QualifiedName` form cannot be answered by
measurement on these reports** — they are decided by the logon, not by the metadata. The
implementation mirrors the source table's own shape (`ProcedureClass` when the source is an
`ISCRProcedure`; qualified name = the source's prefix with the new object name substituted, keeping
the `;1`) because that is what the report itself looks like, and records the choice as unmeasured.

`ISCRDatabaseController.SetTableLocation` fails the same way, for the same reason.

**Consequence:** without a password `addTable` and `setTableLocation` only work where the report's
saved connection can log on unattended (integrated security, or a connection with no password). On
the customer's reports they cannot. `addSubreport` is the credential-free way to bring a second
data source into a report, and the `apply_layout` tool description says so. `removeTable`, by
contrast, needs no database connection at all.

### Supplying the missing password (measured 2026-09-01, VPN up)

`ISCRConnectionInfo` exposes `UserName` and `Password` as read/write `String` properties (reflected
on both the `ISCRConnectionInfo` interface and `ConnectionInfoClass`; the `ConnectionInfo` type the
applier uses is an interface deriving from `ISCRConnectionInfo`, so `.Password` is reachable
through it). `Clone(true)` carries the password with the rest of the connection — verified by
setting it on a fresh `ConnectionInfoClass`, cloning, and reading it back.

So the missing half of the credential can be supplied at the point of call. `LayoutApplier` sets
`Password` on the **clone** it hands to `AddTable`/`SetTableLocation`, from the
`VIBEY_DB_PASSWORD` environment variable, and never from a plan.

Measured against `out/reports/PMSV10_GoalAlignCascade.rpt` through the published worker, writing to
a scratch path (the source `.rpt` was hash-checked unchanged; no output file was produced on any
of these runs):

| Variable | Result | Elapsed |
|---|---|---|
| unset | our own error naming `VIBEY_DB_PASSWORD`, thrown before any COM call | ~2s, no network |
| set to a deliberately wrong value | `COMException: Logon failed. ... Unable to connect: incorrect log on parameters. Details: [Database Vendor Code: 18456]` | ~3s |

**`Database Vendor Code: 18456` is SQL Server's "Login failed for user".** It appears only once a
password is supplied, and it is the evidence that the connection now reaches the server and is
refused *there*, rather than never getting that far. That is the difference between the two failure
modes the tool reports, and it is why they are worth distinguishing to the caller.

Identical behaviour for `setTableLocation` (same message, same vendor code).

**Not measurable here:** the success path. Nobody working on this has the password for
`sgdev01db01_devlogin`, so `addTable` has still never succeeded on this machine. What the wrong-
password probe does establish is that everything up to the server's authentication check now works;
what it cannot establish is that a *correct* password makes `AddTable` return, nor — since Crystal
is measured never to persist a password into a `.rpt` — that the password stays out of a saved
report on a run that actually saves one. Both remain open until someone with the credential tries
it.

### Nothing here needs the database except `AddTable`/`SetTableLocation`

`RemoveTable`, `Database.Tables`, `Database.TableLinks`, `AddTableLink`, `RemoveTableLink` and
`ISCRProcedure.Parameters` all read and write the report's own metadata and were exercised with the
saved connection unusable. Only the two operations that must *verify a database object exists* go to
the server.

## addTable succeeds with VIBEY_DB_PASSWORD, and the password is not persisted (measured 2026-09-01)

First successful `addTable` on this machine. With `VIBEY_DB_PASSWORD` set to the real password for
`sgdev01db01_devlogin`, adding `sp_perf_goal_align_detail` to
`out/reports/PMSV10_GoalAlignCascade.rpt` under the new alias `sp_perf_probe;1` returned `ok: true`.
Reading the saved report back shows the alias present with **all five of its fields discovered from
the server** -- so `Parameters` and `DataFields` do not need to be populated by the caller, which
answers the question the earlier brief could not.

This also closes the residual that was previously expectation rather than measurement:

**The password does not appear in the saved `.rpt`.** The 74,752-byte output was scanned for the
literal value in ASCII/Latin-1, UTF-16LE and UTF-8; no match in any of them. Crystal genuinely does
not persist a connection password, so a report saved by an operation that supplied one is safe to
commit.

The source report was left byte-identical, as always.

For the record, the full ladder of failure modes now measured for `addTable`:

| State | Result |
|---|---|
| `VIBEY_DB_PASSWORD` unset | Fails fast (~2s, no network) naming the variable |
| Set to a wrong value | `Logon failed ... [Database Vendor Code: 18456]` -- SQL Server's "login failed", i.e. the credential reached the server and was refused there |
| Set correctly | `ok: true`, table added with server-discovered fields |

## The RAS SDK cannot re-verify a report's schema (measured 2026-09-01)

When a stored procedure gains a column, an existing `.rpt` does NOT see it. The report carries a
cached result-set schema, and nothing in the RAS SDK refreshes it:

- The only Verify-style method on the whole surface is
  `ISCRDatabaseController.VerifyTableConnectivity(Object)`, which tests that a table is *reachable*.
  There is no equivalent of the Designer's **Database > Verify Database**.
- Repointing a table at itself with `SetTableLocation` -- the usual trick -- **does not work**.
  Measured: `setTableLocation` on `sp_perf_goal_align_cascade;1` returned `ok: true`, and the
  reopened report still listed the original twelve fields with no sign of the newly added
  `emp_number`.

Consequence: after a procedure's result set changes, the report must be opened in the **Crystal
Designer** and verified there before any operation can reference the new fields. Until then,
`setSubreportLink` against a new column fails with COM "Invalid field name" -- which reads as a
typo but is actually a stale schema.

A sub-report embedded in a host report has its own cached schema, so it must be verified
separately, from inside the sub-report, not just from the host.

---

## The formatting operations (measured 2026-09-01)

Everything below was measured against the installed 11.5 assemblies, first by reflection and then
by save-reopen-read round trips against `tests/fixtures/SampleReport.rpt` (through a scratch output
path; the fixture was hash-checked unchanged throughout). Two of the brief's expectations were
contradicted by measurement.

### `SpecialFieldClass` derives everything from `SpecialType` — no lookup table needed

The brief flagged `addSpecialField` as the operation most likely to repeat note 5's trap, where a
freshly constructed `FieldObjectClass` was rejected with `COMException: The field value type is not
valid.` until `FieldValueType` was resolved by walking the database tables. **That walk is not
needed here, and neither is a hardcoded table of formula strings.**

A bare `new SpecialFieldClass { SpecialType = ... }` populates `FormulaForm`, `Name`, `Type` and
`Length` on its own, with no report open, no COM server and no database contact:

| specialType | FormulaForm | Type | Length |
|---|---|---|---|
| `crSpecialFieldTypePageNumber` | `PageNumber` | `crFieldValueTypeInt32uField` | 4 |
| `crSpecialFieldTypePageNOfM` | `PageNofM` | `crFieldValueTypeStringField` | 512 |
| `crSpecialFieldTypeTotalPageCount` | `TotalPageCount` | `crFieldValueTypeInt32uField` | 4 |
| `crSpecialFieldTypePrintDate` | `PrintDate` | `crFieldValueTypeDateField` | 4 |
| `crSpecialFieldTypePrintTime` | `PrintTime` | `crFieldValueTypeTimeField` | 4 |
| `crSpecialFieldTypeReportTitle` | `ReportTitle` | `crFieldValueTypeStringField` | 512 |
| `crSpecialFieldTypeRecordNumber` | `RecordNumber` | `crFieldValueTypeInt32uField` | 4 |

Note `PageNofM` — lowercase `o`, unlike the enum member's `PageNOfM`.

`AddSpecialField` therefore constructs a `SpecialFieldClass`, reads `FormulaForm` and `Type` off it,
and builds an ordinary `FieldObjectClass` from those. All seven `Add` cleanly; **the "field value
type is not valid" failure never occurred**, because the type is always supplied.

**A special field's `DataSource` is a bare, UNBRACED string.** Confirmed against the fixture's own
pre-existing objects rather than inferred: `PrintDate1.DataSource == "PrintDate"` and
`PageNumber1.DataSource == "PageNumber"`, while a database field on the same report is braced
(`CardCode1.DataSource == "{Command.CardCode}"`). Both are `crReportObjectKindField`, which is why
`addSpecialField`'s simulated kind is `"Field"` and the font operations work on one.

### `EnableSystemDefault` silently discards a number format — the finding that mattered

`setNumberFormat` was implemented first exactly as the brief described (clone the `FieldFormat`,
mutate the clone's `NumericFormat`, write it back) and **it was a silent no-op**: the apply returned
`ok: true`, no exception was raised anywhere, and the reopened report read back the values it had
before. Mutating the object clone's nested `NumericFormat` in place instead — the shape `WithFont`
and `setAlignment` use — behaved identically. Neither approach was the problem.

The cause is a gate one level sideways: **`ISCRCommonFieldFormat.EnableSystemDefault`**. While it is
true, Crystal formats the field from the system/locale defaults and discards `NDecimalPlaces` and
`ThousandsSeparator` on save. It is true on every field of every fixture here. Measured on
`PageNumber1`, requesting `NDecimalPlaces = 3` and `ThousandsSeparator = false`:

| Variant | Result after save + reopen |
|---|---|
| Mutate `NumericFormat` in place only | dp=0, thousands=true, sysDefault=true — **nothing persisted** |
| Clone `FieldFormat`, mutate, assign back to `FieldObject.FieldFormat` | **nothing persisted** |
| `CommonFormat.EnableSystemDefault = false` + mutate in place | dp=3, thousands=false — **both persisted** |

`EnableSuppressIfZero` is the exception that makes this dangerous: it persisted *even with the gate
on*, so a test covering only that property would have passed against a two-thirds-broken operation.

`SetNumberFormat` therefore always sets `EnableSystemDefault = false`. This is what the Crystal
Designer does the moment an explicit number format is applied by hand, and the values it freezes are
the ones the field was already displaying, so nothing changes visually beyond what was asked for.
Because the operation writes that property, `read_report` reports it too, as
`numberFormat.systemDefault` — reporting `decimalPlaces` without it would state a number that does
not describe what renders.

### `NDecimalPlaces` does not control precision on its own

`RoundingFormat` rounds the value independently of how many decimals are displayed. Measured mapping
across `CrRoundingTypeEnum`: `crRoundingTypeRoundToUnit` = 11 is 0 decimals,
`crRoundingTypeRoundToTenth` = 10 is 1, `crRoundingTypeRoundToHundredth` = 9 is 2 — the enum value
is `11 - decimalPlaces` over the 0-10 range the validator allows. Setting `NDecimalPlaces = 3` while
leaving rounding at `RoundToUnit` persists both, and renders `10311.000`: three decimal places of a
value already rounded to a whole number. `SetNumberFormat` keeps rounding in step with the requested
precision, as the Designer does.

### A number format does not apply to a non-numeric field

Measured on the String fields `CardCode1`/`CardName1`: with the gate cleared, `EnableSystemDefault`
itself persists, but `NDecimalPlaces` and `ThousandsSeparator` are still discarded. So
`setNumberFormat` against a String field is harmless and does nothing visible.

This is worth stating precisely because it is the reason **the validator must still not check that
the target is numeric.** A field's value type is knowable only from `schema.AvailableFields`, which
`ReportReader` deliberately clears whenever the data source cannot be enumerated — so a type check
would reject every `setNumberFormat` whenever the database is unreachable, trading a harmless no-op
for a hard failure in a normal, supported state. The operation is gated on object *kind* only.

### The section and object format properties all round-trip unremarkably

No surprises here, unlike the above. Written through the existing clone-mutate-commit idioms
(`SetProperty(section, crReportSectionPropertyFormat, clone)` for sections, `Clone` + `Modify` for
objects) and read straight back after save and reopen:

- `ISCRSectionFormat.EnableNewPageBefore` / `EnableNewPageAfter` (`setSectionBreak`)
- `ISCRSectionFormat.EnableSuppress` / `EnableSuppressIfBlank` (`setSuppress`, section form)
- `ISCRObjectFormat.EnableCanGrow` (`setCanGrow`)
- `ISCRObjectFormat.EnableSuppress` (`setSuppress`, object form)

`EnableSuppress` genuinely is one property on two hosts — `ISCRSectionFormat` and `ISCRObjectFormat`
each declare their own — which is why `setSuppress` is a single operation taking either, rather than
a `setSuppress`/`setSectionSuppress` pair. `EnableSuppressIfBlank` exists **only** on the section
form, with no counterpart on `ISCRObjectFormat`, so supplying it with an object target is rejected
by the validator rather than silently ignored.

`ISCRFieldFormat` is null on a Text or FieldHeading object and non-null on every Field object
regardless of value type, so `ObjectInfo.NumberFormat` is populated for Field objects only.

### Sections still cannot be added or removed

Re-confirmed while scoping `setSectionBreak`: `ISCRReportDefController`'s only Add/Modify/Remove
methods are for chart objects. A report's band structure is fixed as far as this SDK is concerned,
so a page break is always set on a section that already exists.
