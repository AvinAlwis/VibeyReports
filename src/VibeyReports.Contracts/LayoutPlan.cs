using System.Collections.Generic;

namespace VibeyReports.Contracts;

public static class LayoutActions
{
    public const string Move = "move";
    public const string Resize = "resize";
    public const string SetFont = "setFont";
    public const string SetFontSize = "setFontSize";
    public const string SetBold = "setBold";
    public const string SetAlignment = "setAlignment";
    public const string AddText = "addText";
    public const string AddLine = "addLine";
    public const string AddBox = "addBox";
    public const string ResizeSection = "resizeSection";
    public const string AddField = "addField";
    public const string RemoveObject = "removeObject";
    public const string SetTextColor = "setTextColor";
    public const string SetFillColor = "setFillColor";
    public const string SetLineColor = "setLineColor";
    public const string SetSectionBackground = "setSectionBackground";
    public const string AddSubreport = "addSubreport";
    public const string SetSubreportLink = "setSubreportLink";
    public const string RemoveTable = "removeTable";
    public const string AddTable = "addTable";
    public const string SetTableLocation = "setTableLocation";
    public const string SetSectionBreak = "setSectionBreak";
    public const string AddSpecialField = "addSpecialField";
    public const string SetNumberFormat = "setNumberFormat";
    public const string SetCanGrow = "setCanGrow";
    public const string SetSuppress = "setSuppress";

    public static readonly string[] All =
    {
        Move, Resize, SetFont, SetFontSize, SetBold,
        SetAlignment, AddText, AddLine, AddBox, ResizeSection, AddField, RemoveObject,
        SetTextColor, SetFillColor, SetLineColor, SetSectionBackground, AddSubreport, SetSubreportLink,
        RemoveTable, AddTable, SetTableLocation,
        SetSectionBreak, AddSpecialField, SetNumberFormat, SetCanGrow, SetSuppress
    };
}

/// <summary>
/// The friendly <see cref="LayoutOperation.SpecialType"/> vocabulary accepted by
/// <see cref="LayoutActions.AddSpecialField"/>, and the single place it is defined.
///
/// Deliberately a short allowlist rather than a pass-through of CrSpecialFieldTypeEnum's
/// twenty-two members: a caller writes "pageNOfM", not "crSpecialFieldTypePageNOfM", and an
/// unrecognised string must be rejected by the validator rather than reaching COM. Adding a
/// member later is cheap; accepting an unknown one and failing at the COM boundary is not.
/// </summary>
public static class SpecialFieldTypes
{
    public const string PageNumber = "pageNumber";
    public const string PageNOfM = "pageNOfM";
    public const string TotalPageCount = "totalPageCount";
    public const string PrintDate = "printDate";
    public const string PrintTime = "printTime";
    public const string ReportTitle = "reportTitle";
    public const string RecordNumber = "recordNumber";

    public static readonly string[] All =
    {
        PageNumber, PageNOfM, TotalPageCount, PrintDate, PrintTime, ReportTitle, RecordNumber
    };
}

public sealed class LayoutPlan
{
    public int PlanVersion { get; set; } = 1;
    public List<LayoutOperation> Operations { get; set; } = new List<LayoutOperation>();
}

/// <summary>
/// One layout change. Flat by design: which fields are required depends on
/// <see cref="Action"/>, and that rule lives in LayoutPlanValidator.
/// </summary>
public sealed class LayoutOperation
{
    public string Action { get; set; } = "";

    /// <summary>
    /// Existing object name. Required for move/resize/setFont/setFontSize/setBold/setAlignment/removeObject.
    /// For setSubreportLink this is a sub-report's SubreportName instead, and for
    /// removeTable/addTable/setTableLocation it is a data-source TABLE ALIAS (one of the
    /// distinct tableAlias values in ReportSchema.AvailableFields) - three separate name-spaces.
    /// </summary>
    public string? Target { get; set; }

    /// <summary>Section name. Required for addText/addLine/addBox/resizeSection/addField.</summary>
    public string? Section { get; set; }

    /// <summary>Name to give a newly created object. Required for addText/addLine/addBox/addField.</summary>
    public string? NewName { get; set; }

    public int? LeftTwips { get; set; }
    public int? TopTwips { get; set; }
    public int? WidthTwips { get; set; }
    public int? HeightTwips { get; set; }

    public string? FontName { get; set; }
    public float? FontSizePt { get; set; }
    public bool? Bold { get; set; }
    /// <summary>Left, Right, Centre, Justified.</summary>
    public string? Alignment { get; set; }

    /// <summary>Literal text for addText.</summary>
    public string? Text { get; set; }

    /// <summary>
    /// Bindable field expression for addField, e.g. "{Command.stage_name}".
    /// Must exactly match a ReportSchema.AvailableFields[].FormulaForm.
    /// </summary>
    public string? FieldRef { get; set; }

    /// <summary>Colour as "#RRGGBB", e.g. "#1F2A37". Required by the colour operations.</summary>
    public string? Color { get; set; }

    /// <summary>Absolute path to an existing .rpt to import as a sub-report. Required for addSubreport.</summary>
    public string? ReportPath { get; set; }

    /// <summary>Main-report field to link FROM, e.g. "{sp_x;1.performance_cycle_id}". Required for setSubreportLink.</summary>
    public string? MainReportField { get; set; }

    /// <summary>Matching field inside the sub-report to link TO. Required for setSubreportLink.</summary>
    public string? SubreportField { get; set; }

    /// <summary>Sub-report parameter that receives the linked value, e.g. "@performance_cycle_id". Required for setSubreportLink.</summary>
    public string? LinkedParameter { get; set; }

    /// <summary>
    /// Database object name for addTable / setTableLocation, e.g. "sp_perf_goal_align_detail;1".
    /// NEVER carries a server, database, user name or password: addTable clones the connection of
    /// an existing table (<see cref="Target"/>) and setTableLocation keeps the table's own
    /// connection untouched. No layout operation accepts a credential of any kind.
    /// </summary>
    public string? TableName { get; set; }

    /// <summary>
    /// setSectionBreak: start a new page BEFORE this section prints. Optional, but at least one
    /// of <see cref="NewPageBefore"/>/<see cref="NewPageAfter"/> must be supplied.
    /// </summary>
    public bool? NewPageBefore { get; set; }

    /// <summary>setSectionBreak: start a new page AFTER this section prints. Optional; see <see cref="NewPageBefore"/>.</summary>
    public bool? NewPageAfter { get; set; }

    /// <summary>
    /// addSpecialField: which Crystal special field to place. One of
    /// <see cref="SpecialFieldTypes.All"/> - the validator's allowlist is the contract.
    /// </summary>
    public string? SpecialType { get; set; }

    /// <summary>setNumberFormat: digits after the decimal point, 0-10. Optional.</summary>
    public int? DecimalPlaces { get; set; }

    /// <summary>setNumberFormat: group digits with a thousands separator. Optional.</summary>
    public bool? ThousandsSeparator { get; set; }

    /// <summary>setNumberFormat: print nothing when the value is zero. Optional.</summary>
    public bool? SuppressIfZero { get; set; }

    /// <summary>setCanGrow: let the object grow vertically to fit its content. Required for setCanGrow.</summary>
    public bool? CanGrow { get; set; }

    /// <summary>
    /// setSuppress: hide the object (<see cref="Target"/>) or the section (<see cref="Section"/>).
    /// Required for setSuppress. EnableSuppress is one Crystal concept with two hosts, which is
    /// why setSuppress takes either -- exactly one of target or section, never both.
    /// </summary>
    public bool? Suppress { get; set; }

    /// <summary>
    /// setSuppress: also hide the section when every object in it is blank. Optional, and valid
    /// ONLY with <see cref="Section"/> -- EnableSuppressIfBlank exists on ISCRSectionFormat and
    /// has no counterpart on ISCRObjectFormat, so supplying it alongside a target is rejected
    /// rather than silently ignored.
    /// </summary>
    public bool? SuppressIfBlank { get; set; }
}
