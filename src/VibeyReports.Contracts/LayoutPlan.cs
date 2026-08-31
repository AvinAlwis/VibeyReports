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

    public static readonly string[] All =
    {
        Move, Resize, SetFont, SetFontSize, SetBold,
        SetAlignment, AddText, AddLine, AddBox, ResizeSection
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

    /// <summary>Existing object name. Required for move/resize/setFont/setFontSize/setBold/setAlignment.</summary>
    public string? Target { get; set; }

    /// <summary>Section name. Required for addText/addLine/addBox/resizeSection.</summary>
    public string? Section { get; set; }

    /// <summary>Name to give a newly created object. Required for addText/addLine/addBox.</summary>
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
}
