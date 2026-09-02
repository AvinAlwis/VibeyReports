using System.Collections.Generic;

namespace VibeyReports.Contracts;

public sealed class ReportSchema
{
    public string ReportPath { get; set; } = "";
    public PageInfo Page { get; set; } = new PageInfo();
    public List<SectionInfo> Sections { get; set; } = new List<SectionInfo>();
    /// <summary>Database fields the report's data source exposes. Used by addField as the allowed fieldRef set.</summary>
    public List<FieldInfo> AvailableFields { get; set; } = new List<FieldInfo>();
    /// <summary>The report's groups, outermost first. Empty when the report has none. Set by addGroup.</summary>
    public List<GroupInfo> Groups { get; set; } = new List<GroupInfo>();
    /// <summary>
    /// The report's record sort order. Set by addSort -- and by addGroup, which makes Crystal
    /// create a sort of its own for the group's field (measured), so a grouped report has one
    /// entry here per group before any addSort is issued.
    /// </summary>
    public List<SortInfo> Sorts { get; set; } = new List<SortInfo>();
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

/// <summary>
/// One group, as created by addGroup. Reports back exactly what addGroup writes -- the field and
/// the direction -- plus the two things Crystal decides for itself and the caller cannot know
/// otherwise: the names of the Group Header and Group Footer sections it created.
/// </summary>
public sealed class GroupInfo
{
    /// <summary>The grouped field in formula form, e.g. "{sp_perf_ind_perf_overview;1.emp_number}".</summary>
    public string FieldRef { get; set; } = "";

    /// <summary>
    /// "ascending" or "descending". Read from the entry Crystal keeps in <see cref="ReportSchema.Sorts"/>
    /// for this group's field -- ISCRGroupOptions carries no direction of its own (measured), so a
    /// group's order genuinely IS a sort. Empty when no matching sort could be found.
    /// </summary>
    public string Direction { get; set; } = "";

    /// <summary>
    /// The name Crystal gave this group's Group Header section, e.g. "CardCodeHeaderSection1" --
    /// the identifier addText/addField/resizeSection need. Null if the report somehow has fewer
    /// group-header areas than groups. When a group area holds several sections, this is the first.
    /// </summary>
    public string? HeaderSection { get; set; }

    /// <summary>The name Crystal gave this group's Group Footer section. See <see cref="HeaderSection"/>.</summary>
    public string? FooterSection { get; set; }
}

/// <summary>
/// One entry of the report's record sort order, as set by addSort -- or created by Crystal for a
/// group (see <see cref="ReportSchema.Sorts"/>).
/// </summary>
public sealed class SortInfo
{
    /// <summary>The sorted field in formula form.</summary>
    public string FieldRef { get; set; } = "";
    /// <summary>"ascending" or "descending". A direction Crystal reports that this tool cannot set
    /// (the four TopN variants) is reported verbatim as its Crystal enum name.</summary>
    public string Direction { get; set; } = "";
}

/// <summary>One main-report-to-subreport field link, as set by setSubreportLink.</summary>
public sealed class SubreportLinkInfo
{
    public string MainReportFieldName { get; set; } = "";
    public string SubreportFieldName { get; set; } = "";
    public string LinkedParameterName { get; set; } = "";
}

/// <summary>
/// How Crystal names the Group Header and Group Footer sections it creates for a group, derived
/// from the grouped field. MEASURED against the installed 11.5 RAS, not assumed -- see
/// docs/sdk-notes.md. Adding a group on "{Command.CardCode}" produces the area "CardCodeHeader"
/// holding the section "CardCodeHeaderSection1" (and "CardCodeFooterSection1"); adding one on
/// "{sp_perf_ind_perf_overview;1.overall_comment}" produces "overallcommentHeaderSection1" --
/// note the UNDERSCORES ARE STRIPPED, which is exactly the sort of detail that makes guessing
/// this rule instead of measuring it a mistake.
///
/// This lives in Contracts because <see cref="LayoutPlanValidator"/> needs it to register the new
/// sections in its cumulative simulation, so that [addGroup, addText into the new group header]
/// validates as one plan. The worker never uses it: ReportReader reports the names Crystal
/// actually assigned, so the schema is always the truth and this is only ever a prediction.
/// </summary>
public static class GroupSectionNaming
{
    public static string HeaderSection(string? fieldRef) => Predict(fieldRef, "Header");
    public static string FooterSection(string? fieldRef) => Predict(fieldRef, "Footer");

    private static string Predict(string? fieldRef, string band)
    {
        var raw = (fieldRef ?? "").Trim();
        if (raw.StartsWith("{")) raw = raw.Substring(1);
        if (raw.EndsWith("}")) raw = raw.Substring(0, raw.Length - 1);

        // "sp_perf_ind_perf_overview;1.overall_comment" -> "overall_comment". The table part can
        // itself contain dots (catalog.schema.name), so the LAST dot is the separator.
        var dot = raw.LastIndexOf('.');
        if (dot >= 0 && dot < raw.Length - 1) raw = raw.Substring(dot + 1);

        var name = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
            if (char.IsLetterOrDigit(c)) name.Append(c);

        return name.ToString() + band + "Section1";
    }
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
