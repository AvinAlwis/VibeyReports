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

    /// <summary>
    /// The colour-reading half of the task, at the MCP boundary: read_report's JSON must expose
    /// textColorHex (FontColor.Color defaults to 0/black the moment a Text or Field object
    /// exists, so this is a real non-null value on every fixture, not a coincidence of a schema
    /// property merely existing).
    /// </summary>
    [Fact]
    public async Task ReadReport_JsonIncludesTextColorHex()
    {
        var json = await Tools().ReadReport(Fixtures.SampleReport, CancellationToken.None);

        var schema = JsonSerializer.Deserialize<ReportSchema>(json, VibeyJson.Options)!;
        var textish = schema.Sections.SelectMany(s => s.Objects).Where(o => o.Kind is "Text" or "Field").ToList();

        textish.Should().NotBeEmpty();
        textish.Should().OnlyContain(o => o.TextColorHex == "#000000");
        json.Should().Contain("textColorHex");
    }

    /// <summary>
    /// The subreport half of the task, at the MCP boundary: read_report's JSON must expose
    /// subreportLinks for a Subreport-kind object, using a real apply_layout round trip (addSubreport
    /// then setSubreportLink) rather than a hand-built schema, so a regression anywhere in the
    /// worker/MCP wiring shows up here too.
    /// </summary>
    [Fact]
    public async Task ReadReport_JsonIncludesSubreportLinksForASubreportObject()
    {
        var mainPath = Path.Combine(Fixtures.Dir, "PMSV10_IndPerfOverview.rpt");
        var schemaJson = await Tools().ReadReport(mainPath, CancellationToken.None);
        var schema = JsonSerializer.Deserialize<ReportSchema>(schemaJson, VibeyJson.Options)!;
        var sectionName = schema.Sections.First(s => s.Kind == "Details" && s.HeightTwips >= 400).Name;
        var mainField = schema.AvailableFields.First().FormulaForm;

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        var planJson = JsonSerializer.Serialize(new LayoutPlan
        {
            Operations =
            {
                new LayoutOperation
                {
                    Action = LayoutActions.AddSubreport, Section = sectionName, NewName = "McpLinkedSubreport",
                    ReportPath = Fixtures.SampleReport,
                    LeftTwips = 200, TopTwips = 30, WidthTwips = 3000, HeightTwips = 300
                },
                new LayoutOperation
                {
                    Action = LayoutActions.SetSubreportLink, Target = "McpLinkedSubreport",
                    MainReportField = mainField, SubreportField = "{Command.CardCode}",
                    LinkedParameter = "@performance_cycle_id"
                }
            }
        }, VibeyJson.Options);

        try
        {
            var applyResult = await Tools().ApplyLayout(mainPath, dest, planJson, false, CancellationToken.None);
            applyResult.Should().Contain("\"ok\": true", because: applyResult);

            var readJson = await Tools().ReadReport(dest, CancellationToken.None);
            readJson.Should().Contain("subreportLinks");
            // F2: subreportName must reach the agent over the wire too. It is the ONLY name
            // setSubreportLink resolves by, and it is a different string from the object's "name",
            // so without it in this JSON an already-embedded sub-report cannot be linked at all.
            readJson.Should().Contain("subreportName");

            var readSchema = JsonSerializer.Deserialize<ReportSchema>(readJson, VibeyJson.Options)!;
            var sub = readSchema.Sections.SelectMany(s => s.Objects)
                .Single(o => o.Kind == "Subreport" && o.LeftTwips == 200 && o.TopTwips == 30);
            sub.SubreportName.Should().Be("McpLinkedSubreport");
            sub.Name.Should().NotBe("McpLinkedSubreport");
            sub.SubreportLinks.Should().NotBeNull();
            sub.SubreportLinks!.Should().ContainSingle();
            sub.SubreportLinks[0].MainReportFieldName.Should().Be(mainField);
            sub.SubreportLinks[0].SubreportFieldName.Should().Be("{Command.CardCode}");
        }
        finally { if (File.Exists(dest)) File.Delete(dest); }
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

    /// <summary>
    /// removeObject's reporting half at the MCP boundary: apply_layout's returned JSON must
    /// surface removedObjects, not just operationsApplied, so an agent can see what it destroyed.
    /// </summary>
    [Fact]
    public async Task ApplyLayout_WithARemoveObjectOperation_ReturnsRemovedObjectsInTheJson()
    {
        var schemaJson = await Tools().ReadReport(Fixtures.SampleReport, CancellationToken.None);
        var schema = JsonSerializer.Deserialize<ReportSchema>(schemaJson, VibeyJson.Options)!;
        var target = schema.Sections.SelectMany(s => s.Objects).First(o => o.Kind is "Text" or "Field");

        var dest = Path.Combine(Path.GetTempPath(), $"vibey_{Guid.NewGuid():N}.rpt");
        var planJson = JsonSerializer.Serialize(new LayoutPlan
        {
            Operations = { new LayoutOperation { Action = LayoutActions.RemoveObject, Target = target.Name } }
        }, VibeyJson.Options);

        try
        {
            var result = await Tools().ApplyLayout(Fixtures.SampleReport, dest, planJson, false, CancellationToken.None);

            result.Should().Contain("\"ok\": true");
            result.Should().Contain("removedObjects");

            using var doc = JsonDocument.Parse(result);
            var removed = doc.RootElement.GetProperty("removedObjects");
            removed.ValueKind.Should().Be(JsonValueKind.Array);
            removed.GetArrayLength().Should().Be(1);
            removed[0].GetString().Should().Be(target.Name);
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
