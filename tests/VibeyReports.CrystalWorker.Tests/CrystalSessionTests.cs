using System;
using System.IO;
using FluentAssertions;
using VibeyReports.CrystalWorker;
using Xunit;

namespace VibeyReports.CrystalWorker.Tests;

public static class Fixtures
{
    public static string Dir
    {
        get
        {
            var d = AppDomain.CurrentDomain.BaseDirectory;
            for (var i = 0; i < 8 && d != null; i++)
            {
                var candidate = Path.Combine(d, "fixtures");
                if (Directory.Exists(candidate)) return candidate;
                d = Path.GetDirectoryName(d.TrimEnd(Path.DirectorySeparatorChar));
            }
            throw new DirectoryNotFoundException("Could not locate tests/fixtures.");
        }
    }

    public static string SampleReport => Path.Combine(Dir, "SampleReport.rpt");
}

public class CrystalSessionTests
{
    [Fact]
    public void Open_BindsToCrystalXiR2AndReportsTheDocumentIsOpen()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        session.Document.Should().NotBeNull();
        session.Document.IsOpen.Should().BeTrue();
        session.Document.ReportDefController.Should().NotBeNull();
    }

    [Fact(Skip =
        "BLOCKED pending orchestrator confirmation: brief assumes ReportDefinition.Sections exists. " +
        "Reflection against the real 11.5.3300.0 CrystalDecisions.ReportAppServer.ReportDefModel.dll shows " +
        "ISCRReportDefinition has no Sections member at all - it exposes Areas (ISCRAreas, a collection of " +
        "ISCRArea). Sections lives one level down, on each ISCRArea (Area.Sections), not on ReportDefinition " +
        "directly. This is a genuine brief/SDK mismatch, not a guess - see task-4-report.md for the full " +
        "reflected member list. Left as Skip rather than silently rewritten, per the instruction not to " +
        "weaken/delete a brief-provided test without confirmation.")]
    public void Open_ExposesAtLeastOneSection()
    {
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        // Original brief code below does not compile against the real 11.5.3300.0 SDK -
        // ISCRReportDefinition has no Sections member (see Skip reason above). Left commented,
        // not rewritten, pending orchestrator confirmation of the correct replacement
        // (e.g. ReportDefinition.Areas[i].Sections.Count summed across areas).
        // var sections = session.Document.ReportDefController.ReportDefinition.Sections;
        // sections.Count.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SaveAs_WritesANewRptFileWithoutTouchingTheOriginal()
    {
        var originalBytes = File.ReadAllBytes(Fixtures.SampleReport);
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");

        try
        {
            using (var session = CrystalSession.Open(Fixtures.SampleReport))
            {
                session.SaveAs(dest, overwrite: false);
            }

            File.Exists(dest).Should().BeTrue();
            new FileInfo(dest).Length.Should().BeGreaterThan(0);
            File.ReadAllBytes(Fixtures.SampleReport).Should().Equal(originalBytes);
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }

    [Fact]
    public void SaveAs_RefusesToOverwriteAnExistingFileUnlessAsked()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        File.WriteAllText(dest, "occupied");

        try
        {
            using var session = CrystalSession.Open(Fixtures.SampleReport);

            Action act = () => session.SaveAs(dest, overwrite: false);

            act.Should().Throw<IOException>().WithMessage("*already exists*");
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }
}
