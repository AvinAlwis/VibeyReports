using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using FluentAssertions;
using VibeyReports.CrystalWorker;
using Xunit;

namespace VibeyReports.CrystalWorker.Tests;

public class ReportRendererTests
{
    /// <summary>
    /// How long a single PDF export may take before the test gives up.
    /// <para>
    /// MEASURED (2026-09-01, healthy print subsystem): these six tests take 4m06s in total, i.e.
    /// roughly 40 seconds per export — PDF export is genuinely slow, not merely slower than the
    /// layout operations. The limit is set to three times that so an ordinarily slow machine does
    /// not go red, while still being far below the point where a human assumes the suite has died.
    /// Do not tighten this towards the measured figure: a timeout that trips on a slow-but-working
    /// machine is worse than the hang it replaced, because it looks like a real failure.
    /// </para>
    /// </summary>
    private const int ExportTimeoutSeconds = 120;

    /// <summary>
    /// Runs <see cref="ReportRenderer.ExportPdf"/> with a hard time limit.
    /// <para>
    /// MEASURED (2026-09-01): PDF export goes through a printer driver over COM, and that call can
    /// block <em>indefinitely</em> when the Windows print subsystem is in a bad state — observed as
    /// a test host sitting at 0.08s of CPU over 45 seconds with one thread in <c>Wait, LpcReply</c>
    /// and <c>splwow64</c> (the 32-bit print-driver surrogate) alive alongside it. It has also been
    /// seen failing outright with "the process cannot access the file because it is being used by
    /// another process", and passing normally, on the same fixture — so this is a flaky external
    /// interaction, not a deterministic fault in our code.
    /// </para>
    /// <para>
    /// Without this guard that hang stops the whole 7-minute suite forever and has to be killed by
    /// hand. A blocked COM call cannot be cancelled, so the point here is not to interrupt it: the
    /// export runs on a thread-pool (background) thread, which lets the test fail in
    /// <see cref="ExportTimeoutSeconds"/> seconds and lets the process exit afterwards even though
    /// that thread is still stuck.
    /// </para>
    /// </summary>
    private static byte[] ExportPdfWithin(CrystalSession session)
    {
        var task = Task.Run(() => ReportRenderer.ExportPdf(session));

        if (!task.Wait(TimeSpan.FromSeconds(ExportTimeoutSeconds)))
        {
            throw new TimeoutException(
                $"ExportPdf did not return within {ExportTimeoutSeconds}s. PDF export goes through " +
                "a printer driver over COM, which can block indefinitely when the Windows print " +
                "subsystem is wedged. This is an environment fault, not a report or layout fault: " +
                "restart the Print Spooler service (elevated: Restart-Service -Name Spooler -Force) " +
                "and run again. Layout operations do not use the print path and are unaffected.");
        }

        // GetAwaiter().GetResult() rather than .Result: it rethrows the original exception instead
        // of wrapping it in an AggregateException, which the parameter test below asserts against.
        return task.GetAwaiter().GetResult();
    }

    [Fact]
    public void ExportPdf_ReturnsBytesThatStartWithThePdfMagicNumber()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var pdf = ExportPdfWithin(session);

        pdf.Should().NotBeNull();
        pdf.Length.Should().BeGreaterThan(1000);
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public void ExportPdf_ProducesAFileEndingWithEof()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var pdf = ExportPdfWithin(session);
        var tail = Encoding.ASCII.GetString(pdf, Math.Max(0, pdf.Length - 32), Math.Min(32, pdf.Length));

        tail.Should().Contain("%%EOF");
    }

    [Fact]
    public void ExportPdf_OnAReportWithUnfilledParameters_FailsWithAnActionableMessage()
    {
        // VERIFIED (task-7-supplement.md, C2): Documents.rpt declares parameters with no saved
        // values. Crystal refuses to render it (COMException: "Missing parameter values."), and
        // Vibey Reports must not supply parameter values (Global Constraint 5) or skip this test
        // (task-7-supplement.md, C3) — the correct behaviour is a clear, actionable exception.
        var path = Path.Combine(Fixtures.Dir, "Documents.rpt");
        using var session = CrystalSession.Open(path);

        Action act = () => ExportPdfWithin(session);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*Documents.rpt*")
           .WithMessage("*parameter*")
           .WithInnerException<System.Runtime.InteropServices.COMException>();
    }

    [Theory]
    [InlineData("SampleReport.rpt")]
    [InlineData("JournalEntry.rpt")]
    [InlineData("PMSV10_IndPerfOverview.rpt")]
    public void ExportPdf_ProducesAValidPdfForEveryRenderableFixture(string fixtureName)
    {
        using var session = CrystalSession.Open(Path.Combine(Fixtures.Dir, fixtureName));

        var pdf = ExportPdfWithin(session);

        pdf.Length.Should().BeGreaterThan(1000);
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }
}
