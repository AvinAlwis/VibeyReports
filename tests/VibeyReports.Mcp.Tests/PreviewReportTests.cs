using FluentAssertions;
using ModelContextProtocol.Protocol;
using VibeyReports.Mcp;
using Xunit;

namespace VibeyReports.Mcp.Tests;

/// <summary>
/// round-1 fix T1: preview_report carries the entire visual-feedback loop (Claude actually sees the
/// rendered report) and had zero coverage - dropping the image block, dropping IsError on failure, or
/// double-encoding the PNG into valid-looking garbage would all have kept the rest of the suite green.
/// The success path runs against VibeyReports.FakeWorker (extended with a "RENDER_OK" keyword that
/// returns the real minimal.pdf fixture as pdfBase64) so it stays fast and Crystal-free; the failure
/// path runs against the real worker and Documents.rpt, the fixture that is known to fail rendering
/// because it declares parameters with no saved values (see ProgramEndToEndTests.
/// Render_OnAReportWithUnsetParameters_ReturnsOkFalseWithTheActionableMessage in
/// VibeyReports.CrystalWorker.Tests).
/// </summary>
public class PreviewReportTests
{
    private static string FakeWorkerPath
    {
        get
        {
            const string exeName = "VibeyReports.FakeWorker.exe";
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && dir is not null; i++)
            {
                var candidateDir = Path.Combine(dir, "tests", "VibeyReports.FakeWorker");
                if (Directory.Exists(candidateDir))
                {
                    var candidate = Directory.GetFiles(candidateDir, exeName, SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (candidate is not null) return candidate;
                }
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }

            throw new FileNotFoundException($"Could not find {exeName}. Build tests/VibeyReports.FakeWorker first.");
        }
    }

    [Fact]
    public async Task PreviewReport_OnSuccess_ReturnsAPngImageBlock()
    {
        var tools = new ReportTools(new CrystalWorkerClient(FakeWorkerPath));

        var result = await tools.PreviewReport(@"C:\reports\RENDER_OK.rpt", dpi: 96, CancellationToken.None);

        (result.IsError ?? false).Should().BeFalse();
        result.Content.Should().NotBeNull();

        var image = result.Content!.OfType<ImageContentBlock>().Should().ContainSingle().Subject;
        image.MimeType.Should().Be("image/png");

        var bytes = image.DecodedData.ToArray();
        bytes.Length.Should().BeGreaterThan(100);
        bytes[0].Should().Be(0x89);
        bytes[1].Should().Be((byte)'P');
        bytes[2].Should().Be((byte)'N');
        bytes[3].Should().Be((byte)'G');

        result.Content!.OfType<TextContentBlock>().Should().ContainSingle()
            .Which.Text.Should().Contain("96 dpi");
    }

    [Fact]
    public async Task PreviewReport_WhenTheWorkerCannotRender_ReturnsIsErrorWithTheActionableMessage()
    {
        var tools = new ReportTools(new CrystalWorkerClient(WorkerLocator.Find()));
        var path = Path.Combine(Fixtures.Dir, "Documents.rpt");

        var result = await tools.PreviewReport(path, dpi: 96, CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().NotBeNull();
        result.Content!.OfType<ImageContentBlock>().Should().BeEmpty();

        var text = result.Content!.OfType<TextContentBlock>().Should().ContainSingle().Subject;
        text.Text.Should().Contain("Documents.rpt");
        text.Text.Should().Contain("parameter");
    }
}
