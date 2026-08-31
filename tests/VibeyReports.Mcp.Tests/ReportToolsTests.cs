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

    // round-1 fix T3: apply_layout's description now promises availableFields, and addField's
    // fieldRef allowlist depends on it, but the only prior assertion was
    // Sections.Should().NotBeEmpty(). SampleReport.rpt's fields weren't exercised here, so use
    // PMSV10_IndPerfOverview.rpt, which has real bound database fields.
    [Fact]
    public async Task ReadReport_ReturnsAPopulatedAvailableFields()
    {
        var path = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");

        var json = await Tools().ReadReport(path, CancellationToken.None);

        var schema = JsonSerializer.Deserialize<ReportSchema>(json, VibeyJson.Options)!;
        schema.AvailableFields.Should().NotBeEmpty();
        schema.AvailableFields.Should().OnlyContain(f => !string.IsNullOrWhiteSpace(f.FormulaForm));
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

        // round-1 fix T2: pin the *structure* of validationErrors, not just that "NotReal" appears
        // somewhere in the payload. A regression that folded ValidationError.Message into "error"
        // and dropped the validationErrors array (destroying the operationIndex/message retry
        // signal the brief calls out) would still contain "NotReal" and keep the assertions above
        // green.
        using var doc = JsonDocument.Parse(result);
        var errors = doc.RootElement.GetProperty("validationErrors");
        errors.ValueKind.Should().Be(JsonValueKind.Array);
        errors.GetArrayLength().Should().BeGreaterThan(0);
        var firstError = errors[0];
        firstError.GetProperty("operationIndex").GetInt32().Should().Be(0);
        firstError.GetProperty("message").GetString().Should().Contain("NotReal");
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
