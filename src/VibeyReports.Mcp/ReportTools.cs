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
        margins in twips, every section with its height and backgroundColorHex, and every object
        with its name, kind, position, size, font, alignment, and (where applicable)
        textColorHex/fillColorHex/lineColorHex/subreportLinks (a Subreport object's list of
        mainReportFieldName/subreportFieldName/linkedParameterName links, set by
        apply_layout's setSubreportLink). All colours are "#RRGGBB"; a null colour field
        means that property has never been explicitly set (not "black" or "white"). Also returns
        availableFields: the database fields the report's data source exposes, each with a
        formulaForm you can pass as fieldRef to apply_layout's addField action. Object and
        section names in this result are the identifiers you must use in a layout plan.
        1440 twips = 1 inch.
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
        planJson is an opaque string, so this description is the only place the required
        properties for each action are documented - the JSON schema alone cannot tell you them.
        Every operation needs "action" plus:

          move            target, leftTwips, topTwips
          resize          target, widthTwips, heightTwips
          setFont         target, fontName
          setFontSize     target, fontSizePt (4-72)
          setBold         target, bold
          setAlignment    target, alignment (Left | Right | Centre | Justified; "Center" is
                          also accepted)
          addText         section, newName, text, leftTwips, topTwips, widthTwips, heightTwips
          addLine         section, newName, leftTwips, topTwips, widthTwips, heightTwips
                          (a line must be horizontal or vertical: set widthTwips or
                          heightTwips to 0)
          addBox          section, newName, leftTwips, topTwips, widthTwips, heightTwips
          addField        section, newName, fieldRef, leftTwips, topTwips, widthTwips,
                          heightTwips
          resizeSection   section, heightTwips
          removeObject    target
          setTextColor    target, color (Text, Field or FieldHeading)
          setFillColor    target, color (Box only)
          setLineColor    target, color (Line or Box)
          setSectionBackground  section, color (any section)
          addSubreport    section, newName, reportPath, leftTwips, topTwips, widthTwips,
                          heightTwips
          setSubreportLink  target, mainReportField, subreportField, linkedParameter

        "target" names an existing object (from read_report); "section" names an existing
        section. addField's fieldRef MUST be one of the formulaForm values from the schema's
        availableFields (from read_report) - it can only reference fields already in the
        report's data source, and cannot add tables or change any connection.
        "color" is "#RRGGBB", e.g. "#1F2A37" - always six hex digits with the leading "#".
        setSectionBackground is how you get a full-width dark banner band across the page (e.g.
        a report header or group header rendered as a coloured bar) - set the section's
        background colour directly rather than drawing a Box over it, which would only cover
        part of the section width the box itself occupies and would still show the report's
        page background around it.
        removeObject permanently removes the object from the generated report - any kind,
        including a Field (which deletes that bound data from the output) or a Subreport, Chart
        or Crosstab. The source .rpt is never modified - every apply writes a new file - but the
        removal is permanent in that new file. Removals are listed in the response under
        "removedObjects" so you can see exactly what was deleted. Section removal is not
        supported - resizeSection to 0 covers collapsing a section instead.
        addSubreport is how you combine a second data source into one report - Crystal Reports
        can bind only one data source per report object, so a report needing rows from a second
        stored procedure or table embeds a sub-report rather than adding a second table to the
        main report. reportPath must be an absolute path to an existing .rpt; the sub-report
        brings its own data source with it, so no addTable/setDataSource step is needed or
        supported. setSubreportLink wires one of the sub-report's parameters to a main-report
        field so the sub-report only shows rows relevant to the current main-report row (e.g. the
        current employee); each linked parameter needs its own setSubreportLink call - a report
        that needs both an evaluation-cycle link and an employee-number link issues two
        setSubreportLink operations against the same target, and both survive (links accumulate,
        they do not replace each other). target for setSubreportLink must be a Subreport object,
        and MUST be the newName you gave that sub-report in the same plan's own addSubreport
        operation, not a name read back from a prior read_report - a sub-report's placed object is
        given an internal, auto-numbered name (e.g. "Subreport1") by Crystal that is DIFFERENT from
        the name you chose, and setSubreportLink only resolves by the name you chose. Only
        linking a sub-report added earlier in the SAME plan is supported; setSubreportLink against
        a sub-report from an earlier apply_layout call or already embedded in the source .rpt is
        not supported and will fail.
        A newly added text or field object inherits the font of existing objects already in its
        target section (falling back to Arial 10pt if the section has none), so a follow-up
        setFont is only needed when you want a different font from the section's existing style.
        All coordinates are twips (1440 = 1 inch).
        Layout only: database connections, SQL, formulas, parameters, record selection and
        grouping cannot be changed and any attempt is rejected. The whole plan is validated
        before anything is written, so a rejected plan leaves no output file behind.
        On success the result includes the refreshed schema, so you do not need a follow-up
        read_report to see the new object names, positions or section heights.
        To inspect the result, call preview_report on outputPath (the file just written), not on
        the original reportPath - the source file is never modified, so previewing it will show
        the report unchanged and look like your edit had no effect. To iterate further, either
        pass outputPath as the next call's reportPath, or write each round to a fresh outputPath.
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
            removedObjects = response.RemovedObjects,
            outputPath = response.OutputPath,
            schema = response.Schema
        }, VibeyJson.Options);
    }

    [McpServerTool(Name = "preview_report", ReadOnly = true)]
    [Description("""
        Render the first page of a .rpt file and return it as an image so you can see the
        actual laid-out report. Use this after apply_layout to check for overlapping fields,
        uneven spacing, misaligned columns, oversized headings and inconsistent margins, then
        issue a corrected layout plan. Pass apply_layout's outputPath here, not the original
        reportPath - the source file is never modified, so previewing it instead would show the
        report unchanged and look like your edit had no effect.
        """)]
    public async Task<CallToolResult> PreviewReport(
        [Description("Absolute path to the .rpt file to render.")] string reportPath,
        [Description("Render resolution in DPI, 72-300. Defaults to 96, which is usually enough; use up to 300 for fine detail. Out-of-range values are clamped.")] int dpi = 96,
        CancellationToken cancellationToken = default)
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

        // round-1 fix F2: dpi was previously clamped only on the floor (dpi <= 0 -> 96), leaving it
        // unbounded on the high side. A Letter page at a few hundred dpi is already tens of
        // megabytes of bitmap, and PDFtoImage's own docs warn that an oversized render can produce
        // "corrupted images (e.g. missing text)" rather than throwing - the worst possible failure
        // mode for a tool whose entire purpose is letting the agent inspect layout, since it would
        // silently "see" missing text and try to fix a problem that does not exist.
        var effectiveDpi = Math.Clamp(dpi <= 0 ? 96 : dpi, 72, 300);

        byte[] png;
        try
        {
            png = PdfRasterizer.FirstPageToPng(Convert.FromBase64String(response.PdfBase64), effectiveDpi);
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
                new TextContentBlock { Text = $"Page 1 of {Path.GetFileName(reportPath)} at {effectiveDpi} dpi." },
                ImageContentBlock.FromBytes(png, "image/png")
            ]
        };
    }

    private static string Fail(string error, List<ValidationError>? validationErrors = null) =>
        JsonSerializer.Serialize(new { ok = false, error, validationErrors }, VibeyJson.Options);
}
