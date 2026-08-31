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

    [Fact]
    public void Open_ExposesAtLeastOneSection()
    {
        // ReportDefinition has no Sections member - confirmed by reflection against the real
        // 11.5.3300.0 CrystalDecisions.ReportAppServer.ReportDefModel.dll (see task-4-report.md,
        // "Deviation 4 / B2"). It exposes Areas instead, and each Area carries its own Sections
        // collection. This walks areas to preserve the original test's intent (the report exposes
        // at least one section) against the real object model.
        using var session = CrystalSession.Open(Fixtures.SampleReport);

        var areas = session.Document.ReportDefController.ReportDefinition.Areas;

        var sectionCount = 0;
        for (var i = 0; i < areas.Count; i++)
            sectionCount += areas[i].Sections.Count;

        areas.Count.Should().BeGreaterThan(0);
        sectionCount.Should().BeGreaterThan(0);
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
