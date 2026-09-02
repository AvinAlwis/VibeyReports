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
        margins in twips, every section with its height, backgroundColorHex and its
        suppressed/suppressIfBlank/newPageBefore/newPageAfter flags, and every object
        with its name, kind, position, size, font, alignment, canGrow, suppressed, and (where
        applicable) numberFormat (decimalPlaces/thousandsSeparator/suppressIfZero, for Field
        objects) and
        textColorHex/fillColorHex/lineColorHex/subreportName/subreportLinks (a Subreport
        object's own name plus its list of mainReportFieldName/subreportFieldName/
        linkedParameterName links, set by apply_layout's setSubreportLink). A Subreport's
        "subreportName" is a DIFFERENT string from its "name": Crystal auto-numbers the placed
        object ("Subreport1", ...) and keeps the embedded report's own name separately, and
        setSubreportLink is keyed by subreportName while every other action uses name.
        All colours are "#RRGGBB"; a null colour field
        means that property has never been explicitly set (not "black" or "white"). Also returns
        availableFields: the database fields the report's data source exposes, each with a
        formulaForm you can pass as fieldRef to apply_layout's addField action. Object and
        section names in this result are the identifiers you must use in a layout plan.
        Also returns groups (each with fieldRef, direction, and the headerSection/footerSection
        names Crystal assigned to that group's two sections - use those names to place objects into
        a group band) and sorts (the record sort order, fieldRef plus direction). A grouped report
        always lists one sort per group, because a group's order IS a sort in Crystal.
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
          setSubreportLink  target (a subreportName), mainReportField, subreportField
          removeTable       target (a table ALIAS)
          addTable          target (alias of an existing table whose connection is cloned),
                            tableName, newName
          setTableLocation  target (a table ALIAS), tableName
          setSectionBreak   section, and newPageBefore and/or newPageAfter (at least one)
          addSpecialField   section, newName, specialType, leftTwips, topTwips, widthTwips,
                            heightTwips
          setNumberFormat   target (a Field), and at least one of decimalPlaces (0-10),
                            thousandsSeparator, suppressIfZero
          setCanGrow        target (Text or Field), canGrow
          setSuppress       suppress, plus EXACTLY ONE of target (any object) or section;
                            suppressIfBlank is optional and valid only with section
          addGroup          fieldRef, and optionally direction (ascending | descending) and
                            groupIndex (0-based, outermost first; omitted appends)
          addSort           fieldRef, direction (ascending | descending), and optionally
                            sortIndex (0-based; omitted appends)

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
        brings its own data source with it, so no addTable step is needed - and unlike addTable it
        needs no database logon, which makes it the reliable choice. reportPath must be ABSOLUTE - a relative path is rejected, because it would
        otherwise resolve against the report worker process's own working directory.
        setSubreportLink adds a Crystal FIELD LINK: it filters the sub-report to the rows whose
        subreportField matches the current main-report row's mainReportField (e.g. the current
        employee). Both must be real DATA fields - mainReportField a field of the main report,
        subreportField a field the sub-report's own result set actually RETURNS. It is NOT a way
        to feed a stored procedure's declared input parameter: parameter-style values for
        subreportField ("{?x}", "{?@x}", "@x") are all rejected by Crystal as "Invalid field
        name", and "linkedParameter" is optional and has NO effect - Crystal discards whatever you
        pass and substitutes its own auto-generated "{?Pm-<mainReportField>}" parameter. If the
        sub-report's procedure takes a value only as a parameter and never selects it back as a
        column, no field link can be built at all and the report must be finished in the Crystal
        Designer instead. Each link needs its own setSubreportLink call - a report needing both an
        evaluation-cycle link and an employee-number link issues two setSubreportLink operations
        against the same target, and both survive (links accumulate, they do not replace each
        other). setSubreportLink's target is a "subreportName", NOT an object "name": use the
        subreportName read_report reports for an already-embedded sub-report, or the newName you
        gave an addSubreport earlier in the same plan (which becomes that sub-report's
        subreportName). Passing the placed object's auto-numbered name (e.g. "Subreport1") is
        rejected. A sub-report added earlier in the SAME plan can ONLY be targeted by
        setSubreportLink - move/resize/setAlignment/removeObject against that newName are rejected,
        because Crystal names the placed object itself and that name is not known until the plan is
        saved; addSubreport's own leftTwips/topTwips/widthTwips/heightTwips already place it, and a
        later plan can address it by the name read_report then reports.
        removeTable / addTable / setTableLocation change the report's own data-source bindings.
        Their "target" is a TABLE ALIAS, not an object name: use one of the distinct tableAlias
        values from the schema's availableFields (e.g. "sp_perf_goal_align_detail;1"). None of
        them takes a server, database, user name or password as an operation field - a plan can
        never carry a credential - and none of them writes to the database: the database is only
        ever read.
        removeTable is the one to reach for when a report is bound to two unrelated tables or
        stored procedures with no link between them, which Crystal renders as a cartesian join
        (every row of one multiplied by every row of the other). Crystal itself does NOT stop you
        removing a table that objects are still bound to, so this tool refuses instead: remove the
        bound objects first (removeObject) in the SAME plan and the removeTable is then accepted.
        Removed aliases are listed in the response under "removedTables". Crystal can still refuse
        a removal of its own accord when a formula, record selection, group or sort refers to the
        table; that has to be undone in the Crystal Designer.
        addTable and setTableLocation MAKE CRYSTAL CONNECT TO THE DATABASE to verify the object,
        and the connection saved in a report carries its user name but not its password (Crystal
        does not persist one). The missing password is read from the VIBEY_DB_PASSWORD environment
        variable of the process running this tool - it is NOT part of a plan and there is no
        operation field for it, because plans are JSON files written to disk and must never record
        a credential. Never ask for it, never put it in a plan, and never repeat it if you somehow
        see it. If VIBEY_DB_PASSWORD is not set, these two operations fail immediately with an
        error saying so; tell the user to set it rather than retrying. If it is set and the server
        rejects it, the error says that instead, with the database user named. Reports whose saved
        connection uses integrated security need no variable. To combine a second data source with
        no logon at all, use addSubreport instead - a sub-report brings its own connection.
        addTable also cannot tell you the new table's fields; re-read the report afterwards
        before trying to addField from it.
        setSectionBreak is how you split a report across pages: newPageAfter on the section that
        should end a page, or newPageBefore on the one that should start a new one. Sections
        cannot be added or removed - Crystal's five-band shape is fixed through this SDK - so a
        page break is set on a section that already exists.
        addSpecialField places a value Crystal computes at print time rather than one from the
        data source, so it needs no fieldRef and no availableFields entry. specialType is one of:
        pageNumber, pageNOfM, totalPageCount, printDate, printTime, reportTitle, recordNumber.
        Anything else is rejected. Use these for report footers ("Page 1 of 12", a print date)
        instead of an addText you would have to keep up to date by hand. The placed object is an
        ordinary Field, so setFontSize, setBold, setAlignment and setTextColor all work on it.
        setNumberFormat fixes numbers that render wrong: an ID column showing "10,311.00" is
        decimalPlaces 0 plus thousandsSeparator false. It applies to Field objects only, and only
        the properties you supply are changed - setting decimalPlaces alone leaves the field's
        existing thousands separator as it was. The tool does NOT check that the field holds a
        number, because a field's value type is only knowable when the report's data source can be
        enumerated, which needs a database connection this tool does not require.
        setCanGrow lets a Text or Field object grow downwards to fit content instead of clipping
        it - the fix for a free-text comment column that cuts off mid-sentence.
        setSuppress hides something. It takes EITHER a target (any object) OR a section, never
        both, because Crystal's "suppress" is one property that exists on objects and on sections
        alike. On a section, suppressIfBlank additionally hides the section only when everything in
        it is blank, which is how you stop an empty sub-report leaving a gap; suppressIfBlank is
        rejected alongside an object target, since objects have no such property.
        All five of these read back through read_report, so you can confirm what you set:
        sections report newPageBefore/newPageAfter/suppressed/suppressIfBlank, and objects report
        canGrow/suppressed plus numberFormat for Field objects.
        A newly added text or field object inherits the font of existing objects already in its
        target section (falling back to Arial 10pt if the section has none), so a follow-up
        setFont is only needed when you want a different font from the section's existing style.
        addGroup is the ONLY action that creates sections. Crystal's band structure is otherwise
        fixed, so a page that must repeat once per employee cannot live in the Report Header (which
        prints once for the whole report) - group the report on the employee's field and that
        content becomes a Group Header, which repeats per group. addGroup creates a Group Header and
        a Group Footer section and CRYSTAL NAMES THEM: the name is the field's own name with every
        non-alphanumeric character removed, plus "HeaderSection1" / "FooterSection1", so a group on
        "{sp_perf_ind_perf_overview;1.emp_number}" gives "empnumberHeaderSection1" and
        "empnumberFooterSection1" - note the underscores are dropped. You may place objects into
        those names in the SAME plan as the addGroup that creates them. They start 250 twips tall,
        so pair the addGroup with a resizeSection when the content needs more room. read_report
        reports each group's real headerSection/footerSection, so you never have to rely on the
        rule above.
        addSort makes the record order a property of the report instead of leaving it to whatever
        order the database happens to return. Only ascending and descending are supported; Crystal's
        TopN/BottomN orders are rejected. Every group carries a sort on its own field, so a grouped
        field is ALREADY sorted - use addGroup's own direction to change a group's order, and expect
        addSort on that field to be rejected. Crystal refuses a second group or a second sort on the
        same field, and this tool rejects both before writing anything.
        Groups and sorts read back through read_report as "groups" and "sorts".
        All coordinates are twips (1440 = 1 inch).
        Layout, data-source bindings, grouping and sorting only: database connections, SQL,
        formulas, parameters and record selection cannot be changed and any attempt is rejected.
        The whole plan is validated
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
            removedTables = response.RemovedTables,
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
