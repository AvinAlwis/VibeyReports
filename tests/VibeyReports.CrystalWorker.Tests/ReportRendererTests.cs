using System;
using System.IO;
using System.Text;
using FluentAssertions;
using VibeyReports.CrystalWorker;
using Xunit;

namespace VibeyReports.CrystalWorker.Tests;

public class ReportRendererTests
{
    [Fact]
    public void ExportPdf_ReturnsBytesThatStartWithThePdfMagicNumber()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var pdf = ReportRenderer.ExportPdf(session);

        pdf.Should().NotBeNull();
        pdf.Length.Should().BeGreaterThan(1000);
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }

    [Fact]
    public void ExportPdf_ProducesAFileEndingWithEof()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var pdf = ReportRenderer.ExportPdf(session);
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

        Action act = () => ReportRenderer.ExportPdf(session);

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

        var pdf = ReportRenderer.ExportPdf(session);

        pdf.Length.Should().BeGreaterThan(1000);
        Encoding.ASCII.GetString(pdf, 0, 5).Should().Be("%PDF-");
    }
}
