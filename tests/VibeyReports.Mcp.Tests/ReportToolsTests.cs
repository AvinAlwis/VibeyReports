using System.Text.Json;
using FluentAssertions;
using VibeyReports.Contracts;
using VibeyReports.Mcp;
using Xunit;

namespace VibeyReports.Mcp.Tests;

public class ReportToolsTests
{
    private static ReportTools Tools() => new ReportTools(new CrystalWorkerClient(WorkerLocator.Find()));

    [Fact]
    public async Task ReadReport_ReturnsSchemaJsonThatDeserialisesBack()
    {
        var json = await Tools().ReadReport(Fixtures.SampleReport, CancellationToken.None);

        var schema = JsonSerializer.Deserialize<ReportSchema>(json, VibeyJson.Options)!;
        schema.Sections.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReadReport_ReturnsAReadableErrorForAMissingFile()
    {
        var json = await Tools().ReadReport(@"C:\nope\missing.rpt", CancellationToken.None);

        json.Should().Contain("error");
    }

    [Fact]
    public async Task ApplyLayout_AppliesAValidPlanAndReportsTheOutputPath()
    {
        var schemaJson = await Tools().ReadReport(Fixtures.SampleReport, CancellationToken.None);
        var schema = JsonSerializer.Deserialize<ReportSchema>(schemaJson, VibeyJson.Options)!;
        var target = schema.Sections.SelectMany(s => s.Objects).First(o => o.Kind is "Text" or "Field");

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        var planJson = JsonSerializer.Serialize(new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.SetBold, Target = target.Name, Bold = true } }
        }, VibeyJson.Options);

        try
        {
            var result = await Tools().ApplyLayout(Fixtures.SampleReport, dest, planJson, false, CancellationToken.None);

            result.Should().Contain("\"ok\": true");
            result.Should().Contain("operationsApplied");
            File.Exists(dest).Should().BeTrue();
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
    }

    [Fact]
    public async Task ApplyLayout_ReturnsValidationErrorsForABadPlanAndWritesNothing()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        var planJson = JsonSerializer.Serialize(new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.Move, Target = "NotReal", LeftTwips = 1, TopTwips = 1 } }
        }, VibeyJson.Options);

        var result = await Tools().ApplyLayout(Fixtures.SampleReport, dest, planJson, false, CancellationToken.None);

        result.Should().Contain("\"ok\": false");
        result.Should().Contain("NotReal");
        File.Exists(dest).Should().BeFalse();
    }

    [Fact]
    public async Task ApplyLayout_RejectsMalformedPlanJsonWithAHelpfulMessage()
    {
        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");

        var result = await Tools().ApplyLayout(Fixtures.SampleReport, dest, "{ not json", false, CancellationToken.None);

        result.Should().Contain("\"ok\": false");
        result.Should().Contain("Could not parse");
    }

    [Fact]
    public void PdfRasterizer_ConvertsAPdfToAPngWithThePngMagicNumber()
    {
        var pdf = File.ReadAllBytes(Path.Combine(Fixtures.Dir, "minimal.pdf"));

        var png = PdfRasterizer.FirstPageToPng(pdf, dpi: 96);

        png.Length.Should().BeGreaterThan(100);
        png[0].Should().Be(0x89);
        png[1].Should().Be((byte)'P');
        png[2].Should().Be((byte)'N');
        png[3].Should().Be((byte)'G');
    }
}
