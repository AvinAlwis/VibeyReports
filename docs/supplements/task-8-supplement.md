# Task 8 supplement — corrections and carried-forward constraints

The brief was written before Tasks 4–7 were implemented. **Where this file and the brief disagree,
this file wins.**

---

## C1 (Critical) — stdout must carry exactly one JSON document, with no BOM

The brief writes:

```csharp
Console.OutputEncoding = Encoding.UTF8;
```

`Encoding.UTF8` is constructed with `encoderShouldEmitUTF8Identifier: true`. Assigning it to
`Console.OutputEncoding` can emit a UTF-8 byte-order mark (`EF BB BF`) ahead of your JSON. Task 9's
client does `JsonSerializer.Deserialize<WorkerResponse>(stdout, ...)`, and a leading BOM makes that
throw `JsonException: '0xEF' is an invalid start of a value` — a failure that would look like a
worker crash and be genuinely nasty to diagnose across a process boundary.

**Use a BOM-less encoding:**

```csharp
Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
```

Add a test that pins this, because it is invisible in a string comparison:

```csharp
[Fact]
public void Worker_WritesNoByteOrderMarkBeforeItsJson()
{
    var raw = RunRaw(new WorkerRequest { Command = WorkerCommands.Read, ReportPath = Fixtures.SampleReport });

    raw.Length.Should().BeGreaterThan(0);
    // '{' is 0x7B. A UTF-8 BOM would put 0xEF 0xBB 0xBF first.
    raw[0].Should().Be((byte)'{', because: "stdout must be parseable JSON with no byte-order mark");
}
```

`RunRaw` should capture stdout as **bytes**, not as a string — a `StreamReader` silently swallows the
BOM, so a string-based test cannot detect this. Read `process.StandardOutput.BaseStream` into a
`MemoryStream`.

**The general rule this protects:** stdout carries exactly one JSON object and nothing else. Every
diagnostic, warning and stack trace goes to **stderr**. Do not `Console.WriteLine` anywhere on the
success path.

---

## C2 — replace the Task 4 `Program.cs` stub; do not add a second entry point

Task 4 created a placeholder `src/VibeyReports.CrystalWorker/Program.cs` purely because the csproj
sets `OutputType=Exe` and needed *an* entry point. It carries a comment saying Task 8 replaces it.

Replace its contents. Do not create a second class with a second `Main` — that is a compile error
(`CS0017: Program has more than one entry point defined`).

---

## C3 (Critical) — never call `ApplyOperationsWithoutValidation`

`LayoutApplier` contains an **internal test-only seam**, `ApplyOperationsWithoutValidation`, exposed
to the test assembly via `InternalsVisibleTo`. It exists so one regression test can exercise
applier-level failure directly. **It skips the validator, which is this project's single safety gate.**

`Program.cs` lives in the *same assembly*, so the compiler will not stop you calling it. The
discipline is yours. **All work goes through the public `LayoutApplier.Apply`**, which validates first.

---

## C4 — a mid-plan failure must not produce an output file

`LayoutApplier.Apply` is not transactional. If operation 5 of 10 throws, operations 1–4 are already on
the in-memory document. Task 6 added a guard for exactly this: the applier calls
`session.MarkFaulted()`, and `CrystalSession.SaveAs` then refuses with an `InvalidOperationException`
mentioning "partially-applied".

So for the `apply` command:

- An `InvalidPlanException` (validation failure, thrown **before** any mutation) → `ok: false` with
  `validationErrors` populated. The session is **not** faulted; nothing was written.
- A mid-plan failure → the applier faults the session, `SaveAs` refuses, your outer `catch` turns it
  into `ok: false` with the error message. **No output file must exist.**

Add a test asserting that after a failed `apply`, `File.Exists(outputPath)` is `false`. The brief
already has this for the invalid-plan case; make sure it also holds for the mid-plan case if you can
construct one through the public API.

---

## C5 — `ReportSchema` now carries `AvailableFields`

Task 5 (as amended) added `ReportSchema.AvailableFields` — the list of database fields the report's
data source exposes, each with a bindable `FormulaForm`. The `read` command returns it automatically
as part of the schema; no extra work is needed.

It matters because it is the allowlist for the `addField` operation: Claude may only place a field
whose `formulaForm` appears there. Do not filter, truncate, or omit it from the response.

---

## C6 — `render` can legitimately fail, and the message must survive

`ReportRenderer.ExportPdf` wraps Crystal's `COMException` in an `InvalidOperationException` whose
message names the report and explains that a report which cannot be *previewed* can still be *read
and edited*. `Documents.rpt` in the fixtures is exactly this case (it declares parameters with no
saved values).

Your `render` handler's error path must carry that message through to `WorkerResponse.Error` intact —
do not replace it with a generic string. Add a test: `render` on `Documents.rpt` returns `ok: false`
with an error mentioning the file and "parameter", and exit code still 0.

---

## C7 — test counts in the brief are wrong

The brief says the worker suite should reach "20 total". It is already at 46 before this task. Verify
**"0 failed"**, never a count match.

---

## Unchanged and still binding

- Exit code **0** whenever a JSON response was produced, including `ok: false`. Non-zero means the
  worker itself crashed before it could answer.
- `[STAThread]` on `Main` — required for COM.
- Never call `new ReportClientDocument()`; always `CrystalSession.Open`. It hangs on a TCP
  self-connect to port 1566 otherwise.
- Never modify the source `.rpt`. `apply` always writes to a new path and refuses an existing
  destination unless `overwrite: true`.
- `WorkerRequest` / `WorkerResponse` / `WorkerCommands` shapes are consumed verbatim by Task 9's
  `CrystalWorkerClient`. Do not rename fields.
