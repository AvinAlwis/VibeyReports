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

**Superseded in one respect by the grouping work below:** `GroupController.Add` *does* create
sections — a Group Header and a Group Footer per group. It remains true that no method adds or
removes a section directly, and `addGroup` is the only operation in this tool that creates one.

---

## Grouping and sorting (measured 2026-09-02)

Measured against the installed 11.5 assemblies through a purpose-built 32-bit probe (the usual
`ReportDocument.Load(...).ReportClientDocument` bridge), against copies of
`tests/fixtures/SampleReport.rpt` and `tests/fixtures/PMSV10_IndPerfOverview.rpt` written to a
scratch directory outside the repo. No fixture and nothing under `out/reports/` was written to.

Three of the brief's assumptions were contradicted.

### Crystal names the group's sections after the FIELD, with non-alphanumerics stripped

This was the measurement the design hung on, and the obvious guess is wrong.

Adding a group creates a Group Header **area** and a Group Footer **area**, each holding one
section. The names come from the grouped field's `Name`, not from the group's index:

| Grouped field | Areas created | Sections created |
|---|---|---|
| `{Command.CardCode}` | `CardCodeHeader`, `CardCodeFooter` | `CardCodeHeaderSection1`, `CardCodeFooterSection1` |
| `{sp_perf_ind_perf_overview;1.overall_comment}` | `overallcommentHeader`, `overallcommentFooter` | `overallcommentHeaderSection1`, `overallcommentFooterSection1` |

**The underscores are dropped.** A validator that registered `overall_commentHeaderSection1` would
have accepted plans that then threw at `FindSection` mid-apply — the same shape of failure as the
`ImportSubreportEx` name split. The rule, as implemented in
`Contracts/ReportSchema.cs → GroupSectionNaming`, is: take the field name (the part after the last
`.`, braces stripped), remove every character that is not a letter or a digit, then append
`Header`/`Footer` + `Section1`. Only `[A-Za-z0-9_]` field names have been measured; the `;1`
procedure suffix in the *table* part is irrelevant because only the field part is used.

Because the rule is deterministic, `LayoutPlanValidator` registers the two predicted names in its
cumulative simulation, so `[addGroup, addText into the new group header]` validates as **one**
plan. `LayoutApplierTests.Apply_AddsAGroupAndCrystalNamesItsSectionsAsTheValidatorPredicts` asserts
the prediction against what Crystal actually assigned, so a divergence fails a test rather than a
customer's plan. `ReportReader` never uses the prediction — it reports the real names, and those
are what `read_report` shows.

Ordering, also measured: group **headers** appear after the Page Header in group order (outermost
first); group **footers** appear before the Page Footer in **reverse** group order. `ReportReader`
maps group *i* to header area *i* and footer area *n-1-i* on that basis, and only when the counts
line up exactly — a wrong section name is worse than none, because it is the name the caller would
then place objects into.

A new group section starts **250 twips** tall on every fixture tried, which is what the validator
registers so that a same-plan `addText` can be bounds-checked. Getting that number wrong could only
make the validator's bounds check slightly wrong: Crystal itself does not bounds-check an added
object against its section, so it can never cause a mid-plan COM failure. `resizeSection` in the
same plan is the documented way to make more room.

### A group has no direction of its own — a group's order IS a sort

`ISCRGroupOptions` exposes only `ClassName` and `ConditionFormulas`. There is no direction, no
sort order and no `Condition` property on it at all.

What actually happens: `GroupController.Add` creates an entry in `DataDefinition.Sorts` for the
grouped field, ascending. If the field **already** carried a sort, the group adopts it and keeps
its existing direction (measured: a descending standalone sort on `{Command.CardName}` stayed
descending after grouping on that field, and the sort count stayed at 1).

So `addGroup`'s `direction` is applied with `SortController.ModifySortDirection` against that sort,
and **only when the caller supplied one** — an `addGroup` with no direction must not silently flip
an order the report already had. `ModifySortDirection` persists through save and reopen.

The consequence for the schema is that `ReportSchema.Sorts` legitimately reports one entry per
group before any `addSort` has been issued. That is the truth about the report, not an artefact:
`GroupInfo.Direction` is read from that same entry.

### Crystal refuses a duplicate group or a duplicate sort on the same field

| Attempt | Result |
|---|---|
| second `GroupController.Add` on an already-grouped field | `COMException: The grouping already exists.` |
| second `SortController.Add` on an already-sorted field | `COMException: The sorting already exists` |
| `GroupController.Add` on a field that already has a standalone sort | **succeeds**, adopting that sort |
| `SortController.Add` on a field the report groups on | `COMException: The sorting already exists` (the group's own sort is already there) |

`LayoutPlanValidator` rejects the first, second and fourth before anything is written, so neither
operation can fault a session mid-plan. The third is deliberately allowed, because Crystal allows it.

### `GroupController.AddByName` works, and is still the worse path

Unlike the `ReportObjectController.AddByName` of note 6 — which had no reachable success case at all
— this one does work, and produces exactly the same result as the explicit path:

- `AddByName(-1, "{Command.CardCode}", crDateConditionDaily)` → one group, sections
  `CardCodeHeaderSection1` / `CardCodeFooterSection1`, identical to `Add`.
- `AddByName(-1, "CardCode", ...)` (the raw field name) → `COMException: Database Field Not Found`.
- `AddByName(-1, "{Command.no_such_field}", ...)` → the same `COMException: Database Field Not
  Found`, naming neither the operation nor the field the caller wrote.

`AddGroup` uses `FindFieldByFormulaForm` + `GroupController.Add` instead, because
`FindFieldByFormulaForm` returns **null** for an unknown formula rather than throwing (measured),
which lets the applier raise a message that names the field and points at `availableFields`.
`AddByName` also demands a `CrDateConditionEnum` that is meaningless for a non-date field.

### Neither operation needs the database

`FindFieldByFormulaForm`, `GroupController`, `SortController` and `DataDefinition.Groups`/`Sorts`
all read and write the report's own metadata. None of them walks
`DatabaseController.Database.Tables` — the one call measured to *block* rather than fail when the
database is unreachable (post-merge finding PM1) — which is why `AddGroup` resolves its field
through `FindFieldByFormulaForm` rather than reusing `AddField`'s `ResolveDbField` walk. Grouping
needs the field, not its value type.

---

## Object borders (measured 2026-09-02)

`setBorder`'s findings, all established by set → `SaveAs` → reopen → `ReportReader.Read` against
the installed 11.5 RAS, on scratch copies (the fixtures and `out/reports/` were never written to).
Probing came first deliberately: `setNumberFormat` returned `ok` and persisted nothing for a whole
review cycle, so "the applier did not throw" is not evidence that anything was written.

### `ISCRBorder` needs no gate and no wholesale replacement — the good news first

`ISCRReportObject.Border` mutates in place on the clone `ModifyObject` has already deep-copied, and
all five properties (`LeftLineStyle`, `RightLineStyle`, `TopLineStyle`, `BottomLineStyle`,
`BorderColor`) survive save and reopen exactly as written. Setting
`left=single right=double top=dashed bottom=dotted color=#1F2A37` on `CardCode1` in
`SampleReport.rpt` read back as precisely that.

There is **no `EnableSystemDefault`-style gate** over the border, unlike `ISCRFieldFormat`
(note: "The formatting operations"), and no need to assign a separately constructed border back onto
the object, unlike what `FieldFormat` was suspected of needing. Sides not written are left alone, so
a plan adding a bottom rule does not disturb an existing left divider.

`BorderColor` is the same COLORREF `ColorRef` already handles, and its default on an untouched
object is `0` → `#000000`, **not** the `0xFFFFFFFF` "no colour" sentinel that `FillColor` and
section `BackgroundColor` carry. So an unset border colour reads as black rather than null.

### `crLineStyleDouble` is not valid for a Line or a Box

| Target kind | `double` |
|---|---|
| Field, Text, FieldHeading, Subreport | accepted, round-trips |
| Line, Box | `COMException (0x8004201C): The line style value is not valid.` from `ReportObjectController.Modify` |

The rejection is raised for a `double` on *any* of the four sides of a Line or a Box, including the
three a Line ignores — `Modify` validates every style value before deciding what it keeps. This is
Crystal's own line-style domain showing through: a drawn object has single/dashed/dotted (what the
Designer's line-style dropdown offers) and no double.

`LayoutPlanValidator` therefore rejects `double` on a Line or Box target before anything is written.
That is **not** a kind allowlist on the operation — every kind still takes a border, Box and
Subreport included — it is a rule about one style, and it exists because letting it through faults
the session mid-plan and loses every remaining operation.

### A Line's border IS the line, on the one edge it lies along

Setting each of the four sides to each of the five styles on a horizontal line (`height = 0`) and on
a vertical line (`width = 0`), saving and reopening each time:

| Line | side written | kept? |
|---|---|---|
| horizontal | `top` | **yes** — `none`, `single`, `dashed`, `dotted` all round-trip |
| horizontal | `left`, `right`, `bottom` | no — accepted, reported `ok`, discarded on save |
| vertical | `left` | **yes** |
| vertical | `top`, `right`, `bottom` | no — same silent discard |

A freshly added Line reads back `top=single` (horizontal) or `left=single` (vertical): its own
style, exposed through the border. Writing the other three sides is exactly the silent no-op this
project has been burned by before, so the validator rejects them, choosing the legal side from the
object's own geometry (`Height == 0` → `top`, else `left`). The rejection message points at the
better answer: if the caller wants a rule that grows with a cell, the border belongs on the Text or
Field object and the Line should not be drawn at all.

### A freshly added Box arrives with all four sides `single`

`addBox` produces an object whose border already reads `single` on every side — that outline is the
box. It is not something `setBorder` did, and a test asserting "all four sides single" after
bordering a new Box would pass without the applier touching anything. Per-side values are honoured
on a Box regardless (`left=single right=none top=dotted bottom=none` round-trips), so there is no
forced-uniformity rule; only `double` is refused.

## PowerShell skill's read path (measured 2026-09-02)

Measured while implementing `skill/vibey-reports/scripts/VibeyCrystal.psm1`'s `Get-VibeySchema`,
against `tests/fixtures/SampleReport.rpt` through the x86 `powershell.exe` at
`C:\Windows\SysWOW64\WindowsPowerShell\v1.0\powershell.exe` (the Crystal 11.5 assemblies are x86
and will not load in a 64-bit host — the same constraint `vibey.ps1`'s relaunch-under-32-bit logic
exists for). Every finding below reproduces the C# `ReportReader.cs` findings already recorded
above; PowerShell's late-bound COM access agrees with the strongly-typed C# access on every point
checked. Two things the task-2 brief guessed at (not measured, written from the C# implementation
by reflection) turned out wrong, both caught before they shipped by printing real members instead
of trusting the guess.

### `PrintOutputController.GetPageMargins()` does not exist

The brief's `Get-VibeySchema` called `$doc.PrintOutputController.GetPageMargins()`. Printing
`$doc.PrintOutputController | Get-Member` shows no such method — the real methods are
`GetPrintOptions`, `ModifyPageMargins`, `ModifyPrintOptions`, `Export`, `ExportEx`, etc. `Get-Member`
on the return of `GetPrintOptions()` shows `PageContentWidth`, `PageContentHeight`, `PageMargins`
(an object with `Left`/`Right`/`Top`/`Bottom`, not `leftMargin`/`rightMargin`/`topMargin`/
`bottomMargin` as the brief assumed), `PaperSize`, `PaperOrientation`.

`PageContentWidth`/`PageContentHeight` are the **printable** area with margins already excluded, not
the full paper size, and there is no direct paper-size-in-twips property anywhere on the object (only
a `PaperSize` enum, which cannot express a custom size). The full paper size is reconstructed by
adding the margins back on, exactly as the C# `ReportReader.cs` already does (see "FIX (round 1, F1)"
above) — measured against `SampleReport.rpt`:

```
PageContentWidth=11186  + Left=360 + Right=360  = 11906   (matches the fixture's known page width)
PageContentHeight=16118 + Top=360  + Bottom=360 = 16838   (matches the fixture's known page height)
```

`Invoke-VibeyRead`'s `Get-VibeySchema` now reads `GetPrintOptions().PageMargins.{Left,Right,Top,
Bottom}` and computes `widthTwips`/`heightTwips` from `PageContentWidth`/`PageContentHeight` plus
those margins — `ReportDefController.ReportDefinition.PageSetup` (the brief's other guess for page
geometry) was never used and its member set was not even checked, since this path already reproduces
the exact measured fixture values.

### The area-kind and object-kind enum integers the brief guessed are wrong from certain points onward

Reflecting the installed `CrystalDecisions.ReportAppServer.ReportDefModel, Version=11.5.3300.0`
assembly's actual enums (rather than trusting the brief's guessed switch statements) gives:

```
CrAreaSectionKindEnum:  ReportHeader=1  PageHeader=2  GroupHeader=3  Detail=4
                        GroupFooter=5   [6 unused]    PageFooter=7   ReportFooter=8
CrReportObjectKindEnum: Field=1  Text=2  Line=3  Box=4  Subreport=5  Picture=6
                        Chart=7  Crosstab=8  BlobField=9  Map=10  OlapGrid=11  FieldHeading=12
```

Cross-checked live against `SampleReport.rpt`'s five areas, in document order:
`AreaKind=1 ReportHeaderSection1`, `AreaKind=2 PageHeaderSection1`, `AreaKind=4 DetailSection1`,
`AreaKind=7 PageFooterSection1`, `AreaKind=8 ReportFooterSection1` — confirming the gap at 6 is real,
not a fixture artefact.

The brief's `ConvertFrom-VibeyAreaKind` assumed a contiguous 1–7 run (`ReportHeader..ReportFooter`
with no gap), which would have mislabelled `PageFooterSection1` as `"GroupFooter"` (its guessed 6)
and `ReportFooterSection1` as `"PageFooter"` (its guessed 7). The brief's
`ConvertFrom-VibeyObjectKind` guessed `FieldHeading=8`; the real value is 12 (8 is `Crosstab`).
Measured impact: `PageHeaderSection1`'s `Text1`/`Text2` objects — named "Text" by Crystal's own
default naming, easy to mistake for the `Text` kind — report kind integer 12, i.e. `FieldHeading`,
not 2 (`Text`). With the brief's wrong number they would have fallen through to `"Other"` instead of
being recognised as `FieldHeading`, matching the task's measured-facts note that this fixture has
zero `Text`-kind objects despite two objects named `Text1`/`Text2`.

Both switch statements in `VibeyCrystal.psm1` now use the reflected values above, with the reasoning
recorded inline as comments so a future edit does not "correct" them back to the brief's numbers.

### Font properties on an object with no explicitly-set font come back `$null`, not an accessor failure

`FontColor.Font` on every object in `SampleReport.rpt` (all of which use the report's default font
rather than an explicitly overridden one) returns a real, non-null COM object, but `.Name`/`.Size`/
`.Bold` on it all come back `$null` through late-bound PowerShell property access — confirmed with
`$null -eq $font.Name` etc., not just an empty-looking string. This is a legitimate "not explicitly
set" reading (the object inherits the report's default at render time), not a broken or
mistargeted accessor: casting the COM object to a strongly-typed `.NET` interface via `-as` or a
direct cast (`[CrystalDecisions.ReportAppServer.ReportDefModel.ISCRFieldObject]$ro`) fails outright
with a COM interface-mismatch error in both directions, so the late-bound property access the brief
already used is the only one that works here, and its `$null` result is data, not a symptom.
`Get-VibeyObjectInfo` leaves `fontName`/`fontSizePt`/`bold` at their `$null` default in this case
rather than reporting a guessed value.

---

### Subreport takes a full border

Measured on `out/reports/PMSV10_IndDetailedEval.rpt`'s `Subreport3` (read as the source, written to
a scratch path): all four sides plus the colour round-trip, `double` included. This is the kind
`CanGrowKinds` wrongly omitted and whose omission caused the defect `setBorder` exists to fix, so it
was checked explicitly rather than assumed.
