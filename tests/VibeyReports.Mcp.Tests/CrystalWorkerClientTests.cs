using System.Text;
using FluentAssertions;
using VibeyReports.Contracts;
using VibeyReports.Mcp;
using Xunit;

namespace VibeyReports.Mcp.Tests;

public static class Fixtures
{
    public static string Dir
    {
        get
        {
            var d = AppContext.BaseDirectory;
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

public class CrystalWorkerClientTests
{
    private static CrystalWorkerClient Client() => new CrystalWorkerClient(WorkerLocator.Find());

    [Fact]
    public void WorkerLocator_FindsTheCrystalWorkerExecutable()
    {
        var path = WorkerLocator.Find();

        path.Should().EndWith("VibeyReports.CrystalWorker.exe");
        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public async Task ReadAsync_ReturnsASchemaForARealReport()
    {
        var response = await Client().ReadAsync(Fixtures.SampleReport, CancellationToken.None);

        response.Ok.Should().BeTrue(because: response.Error);
        response.Schema!.Sections.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadAsync_ReturnsOkFalseForAMissingFileRatherThanThrowing()
    {
        var response = await Client().ReadAsync(@"C:\definitely\not\here.rpt", CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ApplyAsync_RoundTripsAPlanAndWritesTheOutput()
    {
        var read = await Client().ReadAsync(Fixtures.SampleReport, CancellationToken.None);
        var target = read.Schema!.Sections.SelectMany(s => s.Objects).First(o => o.Kind is "Text" or "Field");

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        try
        {
            var plan = new LayoutPlan
            {
                Operations = { new LayoutOperation { Action = LayoutActions.SetBold, Target = target.Name, Bold = true } }
            };

            var response = await Client().ApplyAsync(Fixtures.SampleReport, dest, plan, overwrite: false, CancellationToken.None);

            response.Ok.Should().BeTrue(because: response.Error);
            response.OperationsApplied.Should().Be(1);
            File.Exists(dest).Should().BeTrue();
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task RenderAsync_ReturnsPdfBytes()
    {
        var response = await Client().RenderAsync(Fixtures.SampleReport, CancellationToken.None);

        response.Ok.Should().BeTrue(because: response.Error);
        var bytes = Convert.FromBase64String(response.PdfBase64!);
        Encoding.ASCII.GetString(bytes, 0, 5).Should().Be("%PDF-");
    }

    // round-1 fix F1: stdin between the client and worker was not pinned to UTF-8 (it fell back to
    // Console.InputEncoding, typically the OEM/ANSI code page on Windows), while stdout/stderr were.
    // A non-ASCII report path is the failing scenario the finding calls out by name - café.rpt, or a
    // field name with an accent or a non-Latin character - and is realistic for an HR product
    // deployed across the Philippines and Sri Lanka.
    [Fact]
    public async Task ReadAsync_RoundTripsANonAsciiReportPath()
    {
        var nonAsciiName = "vibey_café_niño_" + Guid.NewGuid().ToString("N") + ".rpt";
        var dest = Path.Combine(Path.GetTempPath(), nonAsciiName);
        File.Copy(Fixtures.SampleReport, dest, overwrite: true);
        try
        {
            var response = await Client().ReadAsync(dest, CancellationToken.None);

            response.Ok.Should().BeTrue(because: response.Error);
            response.Schema!.ReportPath.Should().Contain("café_niño");
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }
}
