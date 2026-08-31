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

    /// <summary>
    /// Final review F2: the never-overwrite-the-source guard is one of the project's five named
    /// safety invariants and, until now, the only one with no direct test. It is checked before
    /// the "destination already exists" check, so overwrite: true must not bypass it -- that
    /// combination is exactly what the ordering exists to protect against.
    /// </summary>
    [Fact]
    public void SaveAs_RefusesToOverwriteTheSourceEvenWithOverwriteTrue()
    {
        var originalBytes = File.ReadAllBytes(Fixtures.SampleReport);

        using var session = CrystalSession.Open(Fixtures.SampleReport);

        Action act = () => session.SaveAs(Fixtures.SampleReport, overwrite: true);

        act.Should().Throw<IOException>().WithMessage("*Refusing to overwrite the source report*");
        File.ReadAllBytes(Fixtures.SampleReport).Should().Equal(originalBytes,
            because: "the source must stay byte-identical even when overwrite is explicitly requested");
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

    /// <summary>
    /// Final review F5: the documented iterate-in-place workflow (save each round back to the same
    /// outputPath with overwrite: true) is a normal thing to do, so a plain successful overwrite
    /// must still work end to end against the new save-to-temp-then-move implementation, and must
    /// not leave a stray temp file behind.
    /// </summary>
    [Fact]
    public void SaveAs_OverwritesAnExistingDestinationWhenAsked()
    {
        var dir = Path.GetTempPath();
        var dest = Path.Combine(dir, $"vibey_{Guid.NewGuid():N}.rpt");

        try
        {
            int sectionCountBefore;
            using (var first = CrystalSession.Open(Fixtures.SampleReport))
            {
                first.SaveAs(dest, overwrite: false);
                sectionCountBefore = ReportReader.Read(first).Sections.Count;
            }

            using (var second = CrystalSession.Open(Fixtures.SampleReport))
                second.SaveAs(dest, overwrite: true);

            File.Exists(dest).Should().BeTrue();
            // Crystal's SaveAs is not byte-deterministic across two independent saves of the same
            // unmodified source (the .rpt is an OLE compound file that appears to embed a save
            // timestamp or similar), so equivalence is checked by re-reading the layout rather than
            // a raw byte comparison.
            using (var reopened = CrystalSession.Open(dest))
                ReportReader.Read(reopened).Sections.Should().HaveCount(sectionCountBefore);
            Directory.GetFiles(dir, ".vibey-tmp-*").Should().BeEmpty(
                because: "no temporary save file should be left behind after a successful overwrite");
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }

    /// <summary>
    /// Final review F5: SaveAs previously deleted an existing destination BEFORE calling
    /// Document.SaveAs, so a save that failed partway through left ok:false while the previous good
    /// output was already gone. This forces a failure at the final swap step (the destination file
    /// is held open with no sharing, so the delete-then-move cannot complete) and asserts the
    /// pre-existing good file survives untouched and no temp file leaks.
    /// </summary>
    [Fact]
    public void SaveAs_LeavesThePreviousGoodOutputIntactWhenTheFinalSwapFails()
    {
        var dir = Path.GetTempPath();
        var dest = Path.Combine(dir, $"vibey_{Guid.NewGuid():N}.rpt");

        using (var first = CrystalSession.Open(Fixtures.SampleReport))
            first.SaveAs(dest, overwrite: false);

        var goodBytes = File.ReadAllBytes(dest);

        try
        {
            using (File.Open(dest, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var second = CrystalSession.Open(Fixtures.SampleReport))
            {
                Action act = () => second.SaveAs(dest, overwrite: true);

                act.Should().Throw<IOException>();
            }

            File.Exists(dest).Should().BeTrue();
            File.ReadAllBytes(dest).Should().Equal(goodBytes,
                because: "a failed swap must not destroy the previous good output");
            Directory.GetFiles(dir, ".vibey-tmp-*").Should().BeEmpty(
                because: "the temp file must be cleaned up when the swap fails, not leaked");
        }
        finally
        {
            if (File.Exists(dest)) File.Delete(dest);
        }
    }
}
