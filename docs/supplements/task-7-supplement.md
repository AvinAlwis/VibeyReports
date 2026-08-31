# Task 7 supplement — corrections from verified SDK reality

I exercised PDF export against all four fixtures on this machine before this task was dispatched.
**Where this file and the brief disagree, this file wins.**

---

## C1 — `ISCRByteArray` has no `get_Stream()`. Use the `ByteArray` property.

The brief writes:

```csharp
using (var stream = byteArray.get_Stream() as Stream)   // <-- does not exist
```

I reflected the type. `ISCRByteArray` exposes:

```
Int32   Count
Byte[]  ByteArray            <- the bytes, directly
Byte[]  getref_ByteArray()
Void    Save(String FilePath, Boolean OverwriteExisting)
Void    AttachArray(Byte[] ByteArray, Boolean OwnArray)
Byte[]  DetachArray()
```

There is no stream anywhere on the type. The whole method collapses to:

```csharp
public static byte[] ExportPdf(CrystalSession session)
{
    if (session == null) throw new ArgumentNullException(nameof(session));

    var byteArray = session.Document.PrintOutputController.Export(
        CrReportExportFormatEnum.crReportExportFormatPDF, 0);

    if (byteArray == null) throw new InvalidOperationException("Crystal returned no export data.");

    var bytes = byteArray.ByteArray;
    if (bytes == null || bytes.Length == 0)
        throw new InvalidOperationException("Crystal returned an empty PDF export.");

    return bytes;
}
```

Drop the `System.IO` stream plumbing and the `using` blocks around it.

**Verified working** — I ran exactly this path against three fixtures:

```
SampleReport.rpt            OK  bytes=23632  magic='%PDF-'  tail='%%EOF'
JournalEntry.rpt            OK  bytes=41583  magic='%PDF-'  tail='%%EOF'
PMSV10_IndPerfOverview.rpt  OK  bytes=49363  magic='%PDF-'  tail='%%EOF'
```

Both of the brief's tests (`%PDF-` prefix, `%%EOF` tail) will pass on `SampleReport.rpt`.

---

## C2 — Reports with unfilled parameters cannot be exported, and that must be a clear error

The fourth fixture fails:

```
Documents.rpt               FAILED: COMException: Missing parameter values.
```

`Documents.rpt` declares parameters with no stored values. Crystal refuses to render it.

**Do not add parameter-supplying code.** Global Constraint 5 forbids touching parameters, and this
task is layout-only. The correct behaviour is to fail with a message that tells the caller what is
actually wrong — this error will surface to Claude through the MCP preview tool in a later task, so
a bare `COMException: Missing parameter values.` with no context is not good enough.

Wrap it:

```csharp
using System.Runtime.InteropServices;

// inside ExportPdf, around the Export call:
try
{
    // ... the Export call ...
}
catch (COMException ex)
{
    throw new InvalidOperationException(
        $"Could not render \"{Path.GetFileName(session.SourcePath)}\" to PDF: {ex.Message.Trim()} " +
        "Reports that declare parameters with no saved values cannot be previewed, because Vibey Reports " +
        "does not supply parameter values. The layout can still be read and edited.",
        ex);
}
```

That last sentence matters: a report that cannot be *previewed* can still be *read and edited*, and
the caller should know the failure is not fatal to the whole workflow.

### Add this test

```csharp
[Fact]
public void ExportPdf_OnAReportWithUnfilledParameters_FailsWithAnActionableMessage()
{
    var path = Path.Combine(Fixtures.Dir, "Documents.rpt");
    using var session = CrystalSession.Open(path);

    Action act = () => ReportRenderer.ExportPdf(session);

    act.Should().Throw<InvalidOperationException>()
       .WithMessage("*Documents.rpt*")
       .WithMessage("*parameter*");
}
```

### And a breadth test, so this is not only proven on one fixture

```csharp
[Theory]
[InlineData("SampleReport.rpt")]
[InlineData("JournalEntry.rpt")]
[InlineData("PMSV10_IndPerfOverview.rpt")]
public void ExportPdf_ProducesAValidPdfForEveryRenderableFixture(string fixtureName)
{
    using var session = CrystalSession.Open(Path.Combine(Fixtures.Dir, fixtureName));

    var pdf = ReportRenderer.ExportPdf(session);

    pdf.Length.Should().BeGreaterThan(1000);
    Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
}
```

`PMSV10_IndPerfOverview.rpt` is the customer's real PeoplesHR report and it renders cleanly
(49363 bytes) — keeping it in the breadth test is the point.

---

## C3 — The brief's troubleshooting notes are now obsolete

The brief carries two "if this fails, try X" notes. Both are settled:

- *"If `get_Stream()` does not exist"* — it does not. C1 above is the answer; do not go looking.
- *"If the export throws a database logon error, mark the test `Skip`"* — no fixture does this. The
  only failure is the parameter one, which C2 turns into a real assertion instead of a skip. **Do not
  skip any test in this task.**

---

## Unchanged and still binding

- `ReportRenderer.ExportPdf(CrystalSession session) -> byte[]` — exact signature, called by Task 8.
- `CrReportExportFormatEnum.crReportExportFormatPDF` lives in
  `CrystalDecisions.ReportAppServer.ReportDefModel`.
- Never open a report yourself — always `CrystalSession.Open`. Never `new ReportClientDocument()`.
- Task 7 is **read-only**: no `.rpt` may be written or modified.
- The export renders whatever saved data or connection the report already carries. Do not add
  database, logon, or parameter code.
