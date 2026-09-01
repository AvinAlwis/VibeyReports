using System.Collections.Generic;

namespace VibeyReports.Contracts;

public sealed class ReportSchema
{
    public string ReportPath { get; set; } = "";
    public PageInfo Page { get; set; } = new PageInfo();
    public List<SectionInfo> Sections { get; set; } = new List<SectionInfo>();
    /// <summary>Database fields the report's data source exposes. Used by addField as the allowed fieldRef set.</summary>
    public List<FieldInfo> AvailableFields { get; set; } = new List<FieldInfo>();
}

public sealed class PageInfo
{
    public int WidthTwips { get; set; }
    public int HeightTwips { get; set; }
    public int MarginLeftTwips { get; set; }
    public int MarginRightTwips { get; set; }
    public int MarginTopTwips { get; set; }
    public int MarginBottomTwips { get; set; }
    /// <summary>"Portrait" or "Landscape".</summary>
    public string Orientation { get; set; } = "Portrait";
}

public sealed class SectionInfo
{
    /// <summary>RAS section name, e.g. "Section3". This is the stable identifier.</summary>
    public string Name { get; set; } = "";
    /// <summary>Human-readable band: ReportHeader, PageHeader, GroupHeader, Details, GroupFooter, ReportFooter, PageFooter.</summary>
    public string Kind { get; set; } = "";
    public int HeightTwips { get; set; }
    /// <summary>Set by apply_layout's setSuppress (section form). ISCRSectionFormat.EnableSuppress.</summary>
    public bool Suppressed { get; set; }
    /// <summary>Start a new page before this section prints. Set by setSectionBreak.</summary>
    public bool NewPageBefore { get; set; }
    /// <summary>Start a new page after this section prints. Set by setSectionBreak.</summary>
    public bool NewPageAfter { get; set; }
    /// <summary>Hide this section when every object in it is blank. Set by setSuppress's suppressIfBlank.</summary>
    public bool SuppressIfBlank { get; set; }
    /// <summary>"#RRGGBB", or null when the section has no background colour set.</summary>
    public string? BackgroundColorHex { get; set; }
    public List<ObjectInfo> Objects { get; set; } = new List<ObjectInfo>();
}

public sealed class ObjectInfo
{
    /// <summary>
    /// RAS object name. Unique within the report. The target for every operation EXCEPT
    /// setSubreportLink, which is keyed by <see cref="SubreportName"/> instead -- see that
    /// property for why the two are different strings for a Subreport object.
    /// </summary>
    public string Name { get; set; } = "";
    /// <summary>Field, Text, Line, Box, Subreport, Picture, Chart, Crosstab, FieldHeading, Other.</summary>
    public string Kind { get; set; } = "";
    public int LeftTwips { get; set; }
    public int TopTwips { get; set; }
    public int WidthTwips { get; set; }
    public int HeightTwips { get; set; }

    public string? FontName { get; set; }
    public float? FontSizePt { get; set; }
    public bool? Bold { get; set; }
    public bool? Italic { get; set; }
    public bool? Underline { get; set; }
    /// <summary>Left, Right, Centre, Justified, Default.</summary>
    public string? Alignment { get; set; }

    /// <summary>Formula/field expression for Field objects, e.g. "{Customer.Name}". Read-only to the AI.</summary>
    public string? DataSource { get; set; }
    /// <summary>Literal text for Text objects.</summary>
    public string? Text { get; set; }

    /// <summary>"#RRGGBB" text colour for Text/Field/FieldHeading objects; null for any other kind.</summary>
    public string? TextColorHex { get; set; }
    /// <summary>"#RRGGBB" fill colour for Box objects; null when not applicable, or when the box has no fill set.</summary>
    public string? FillColorHex { get; set; }
    /// <summary>"#RRGGBB" outline colour for Line/Box objects; null when not applicable.</summary>
    public string? LineColorHex { get; set; }

    /// <summary>
    /// Populated only for Kind == "Subreport". Null otherwise. The embedded sub-report's OWN
    /// name, which is a different string from <see cref="Name"/>: Crystal auto-numbers the placed
    /// container object ("Subreport1", "Subreport2", ...) and stores the name the sub-report was
    /// imported under here. setSubreportLink is keyed by THIS name, not by <see cref="Name"/>.
    /// </summary>
    public string? SubreportName { get; set; }

    /// <summary>Populated only for Kind == "Subreport". Null otherwise.</summary>
    public List<SubreportLinkInfo>? SubreportLinks { get; set; }

    /// <summary>
    /// The object grows vertically to fit its content instead of clipping it. Set by setCanGrow.
    /// ISCRObjectFormat.EnableCanGrow, which every report object carries.
    /// </summary>
    public bool CanGrow { get; set; }

    /// <summary>The object is hidden. Set by setSuppress (object form). ISCRObjectFormat.EnableSuppress.</summary>
    public bool Suppressed { get; set; }

    /// <summary>
    /// Numeric display format, populated only for Kind == "Field" and null for every other kind
    /// (a Text object has no ISCRFieldFormat at all -- measured). Set by setNumberFormat.
    /// Also left null when the format cannot be read, rather than failing the whole report read.
    /// </summary>
    public NumberFormatInfo? NumberFormat { get; set; }
}

/// <summary>
/// How a Field object renders a number, as written by setNumberFormat and read back here.
/// The three properties are exactly the three the operation writes, so a round-trip assertion
/// cannot pass by checking something the applier never touched.
/// </summary>
public sealed class NumberFormatInfo
{
    public int DecimalPlaces { get; set; }
    public bool ThousandsSeparator { get; set; }
    public bool SuppressIfZero { get; set; }

    /// <summary>
    /// True while Crystal is formatting this field from the system/locale defaults, in which case
    /// <see cref="DecimalPlaces"/> and <see cref="ThousandsSeparator"/> are stored values that do
    /// NOT describe what renders. Measured: with this on, Crystal discards writes to those two
    /// properties entirely. setNumberFormat therefore turns it off, and it is reported here
    /// because every property an operation writes must be readable back -- otherwise a report
    /// showing "decimalPlaces: 2" would be telling the reader something untrue.
    /// </summary>
    public bool SystemDefault { get; set; }
}

/// <summary>One main-report-to-subreport field link, as set by setSubreportLink.</summary>
public sealed class SubreportLinkInfo
{
    public string MainReportFieldName { get; set; } = "";
    public string SubreportFieldName { get; set; } = "";
    public string LinkedParameterName { get; set; } = "";
}

public sealed class FieldInfo
{
    /// <summary>Raw field name, e.g. "stage_name".</summary>
    public string Name { get; set; } = "";
    /// <summary>The bindable expression, e.g. "{Command.stage_name}". Use THIS as addField's fieldRef.</summary>
    public string FormulaForm { get; set; } = "";
    public string TableAlias { get; set; } = "";
    /// <summary>String, Number, Currency, DateTime, Date, Time, Boolean, Blob, Other.</summary>
    public string ValueType { get; set; } = "";
    /// <summary>Crystal's default heading for this field, useful as addText content.</summary>
    public string HeadingText { get; set; } = "";
}
