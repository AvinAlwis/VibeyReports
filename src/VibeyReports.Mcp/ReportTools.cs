using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using VibeyReports.Contracts;

namespace VibeyReports.Mcp;

/// <summary>
/// Crystal report tools exposed over MCP: read_report, apply_layout, preview_report.
/// All Crystal work happens in the out-of-process worker (see CrystalWorkerClient); this type
/// never references a Crystal assembly and never writes to stdout (reserved for MCP JSON-RPC).
/// </summary>
[McpServerToolType]
public sealed class ReportTools
{
    private readonly CrystalWorkerClient _worker;

    public ReportTools(CrystalWorkerClient worker) => _worker = worker;

    [McpServerTool(Name = "read_report", ReadOnly = true)]
    [Description("""
        Read a Crystal Reports XI R2 .rpt file and return its layout as JSON: page size and
        margins in twips, every section with its height, and every object with its name, kind,
        position, size, font and alignment. Also returns availableFields: the database fields
        the report's data source exposes, each with a formulaForm you can pass as fieldRef to
        apply_layout's addField action. Object and section names in this result are the
        identifiers you must use in a layout plan. 1440 twips = 1 inch.
        """)]
    public async Task<string> ReadReport(
        [Description("Absolute path to the .rpt file to read.")] string reportPath,
        CancellationToken cancellationToken)
    {
        var response = await _worker.ReadAsync(reportPath, cancellationToken);

        if (!response.Ok || response.Schema is null)
            return Fail(response.Error ?? "The worker could not read the report.", response.ValidationErrors);

        return JsonSerializer.Serialize(response.Schema, VibeyJson.Options);
    }

    [McpServerTool(Name = "apply_layout")]
    [Description("""
        Apply a layout plan to a .rpt file and save the result as a NEW .rpt that opens in
        Crystal Reports XI R2. The plan is JSON: {"planVersion":1,"operations":[...]}.
        Supported actions: move, resize, setFont, setFontSize, setBold, setAlignment,
        addText, addLine, addBox, addField, resizeSection.
        move/resize/setFont/setFontSize/setBold/setAlignment take "target" (an existing object
        name). addText/addLine/addBox/resizeSection/addField take "section" (a section name);
        addText/addLine/addBox/addField also take "newName" and full geometry (leftTwips,
        topTwips, widthTwips, heightTwips).
        addField places a bound database field. It takes "section", "newName", "fieldRef" and
        full geometry. fieldRef MUST be one of the formulaForm values from the schema's
        availableFields (from read_report) — it can only reference fields already in the
        report's data source, and cannot add tables or change any connection.
        A newly added text or field object inherits the font of existing objects already in its
        target section (falling back to Arial 10pt if the section has none), so a follow-up
        setFont is only needed when you want a different font from the section's existing style.
        All coordinates are twips (1440 = 1 inch).
        Layout only: database connections, SQL, formulas, parameters, record selection and
        grouping cannot be changed and any attempt is rejected. The whole plan is validated
        before anything is written, so a rejected plan leaves no output file behind.
        """)]
    public async Task<string> ApplyLayout(
        [Description("Absolute path to the source .rpt file. Never modified.")] string reportPath,
        [Description("Absolute path for the new .rpt file to create.")] string outputPath,
        [Description("The layout plan as a JSON string.")] string planJson,
        [Description("Set true to replace an existing file at outputPath.")] bool overwrite,
        CancellationToken cancellationToken)
    {
        LayoutPlan? plan;
        try
        {
            plan = JsonSerializer.Deserialize<LayoutPlan>(planJson, VibeyJson.Options);
        }
        catch (JsonException ex)
        {
            return Fail($"Could not parse the layout plan as JSON: {ex.Message}");
        }

        if (plan is null) return Fail("Could not parse the layout plan: it deserialised to null.");

        var response = await _worker.ApplyAsync(reportPath, outputPath, plan, overwrite, cancellationToken);

        if (!response.Ok)
            return Fail(response.Error ?? "The worker could not apply the plan.", response.ValidationErrors);

        return JsonSerializer.Serialize(new
        {
            ok = true,
            operationsApplied = response.OperationsApplied,
            outputPath = response.OutputPath,
            schema = response.Schema
        }, VibeyJson.Options);
    }

    [McpServerTool(Name = "preview_report", ReadOnly = true)]
    [Description("""
        Render the first page of a .rpt file and return it as an image so you can see the
        actual laid-out report. Use this after apply_layout to check for overlapping fields,
        uneven spacing, misaligned columns, oversized headings and inconsistent margins, then
        issue a corrected layout plan.
        """)]
    public async Task<CallToolResult> PreviewReport(
        [Description("Absolute path to the .rpt file to render.")] string reportPath,
        [Description("Render resolution in DPI. 96 is usually enough; use 150 for fine detail.")] int dpi,
        CancellationToken cancellationToken)
    {
        var response = await _worker.RenderAsync(reportPath, cancellationToken);

        if (!response.Ok || string.IsNullOrWhiteSpace(response.PdfBase64))
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = response.Error ?? "The worker could not render the report." }]
            };
        }

        byte[] png;
        try
        {
            png = PdfRasterizer.FirstPageToPng(Convert.FromBase64String(response.PdfBase64), dpi <= 0 ? 96 : dpi);
        }
        catch (Exception ex)
        {
            return new CallToolResult
            {
                IsError = true,
                Content = [new TextContentBlock { Text = $"Rendered the PDF but could not rasterise it: {ex.Message}" }]
            };
        }

        return new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = $"Page 1 of {Path.GetFileName(reportPath)} at {(dpi <= 0 ? 96 : dpi)} dpi." },
                ImageContentBlock.FromBytes(png, "image/png")
            ]
        };
    }

    private static string Fail(string error, List<ValidationError>? validationErrors = null) =>
        JsonSerializer.Serialize(new { ok = false, error, validationErrors }, VibeyJson.Options);
}
