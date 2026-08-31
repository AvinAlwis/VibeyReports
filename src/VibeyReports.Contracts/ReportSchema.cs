using System.Collections.Generic;

namespace VibeyReports.Contracts;

public sealed class ReportSchema
{
    public string ReportPath { get; set; } = "";
    public PageInfo Page { get; set; } = new PageInfo();
    public List<SectionInfo> Sections { get; set; } = new List<SectionInfo>();
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
    public bool Suppressed { get; set; }
    public List<ObjectInfo> Objects { get; set; } = new List<ObjectInfo>();
}

public sealed class ObjectInfo
{
    /// <summary>RAS object name. Unique within the report. Used as the operation target.</summary>
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
}
