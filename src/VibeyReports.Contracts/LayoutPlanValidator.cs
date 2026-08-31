using System;
using System.Collections.Generic;
using System.Linq;

namespace VibeyReports.Contracts;

public static class LayoutPlanValidator
{
    private const float MinFontPt = 4f;
    private const float MaxFontPt = 72f;

    private static readonly HashSet<string> Alignments =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Left", "Right", "Centre", "Center", "Justified" };

    public static ValidationResult Validate(LayoutPlan plan, ReportSchema schema)
    {
        var result = new ValidationResult { IsValid = true };
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (schema == null) throw new ArgumentNullException(nameof(schema));

        // Mutable simulation state: sectionName -> height, objectName -> (section, l, t, w, h)
        var sectionHeights = schema.Sections.ToDictionary(s => s.Name, s => s.HeightTwips, StringComparer.OrdinalIgnoreCase);
        var objects = new Dictionary<string, SimObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in schema.Sections)
            foreach (var o in s.Objects)
                objects[o.Name] = new SimObject
                {
                    Section = s.Name, Left = o.LeftTwips, Top = o.TopTwips,
                    Width = o.WidthTwips, Height = o.HeightTwips
                };

        var printableWidth = schema.Page.WidthTwips - schema.Page.MarginLeftTwips - schema.Page.MarginRightTwips;
        var printableHeight = schema.Page.HeightTwips - schema.Page.MarginTopTwips - schema.Page.MarginBottomTwips;

        for (var i = 0; i < plan.Operations.Count; i++)
        {
            var op = plan.Operations[i];
            var errors = ValidateOne(op, i, objects, sectionHeights, printableWidth, printableHeight);
            result.Errors.AddRange(errors);
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    private static List<ValidationError> ValidateOne(
        LayoutOperation op, int index,
        Dictionary<string, SimObject> objects,
        Dictionary<string, int> sectionHeights,
        int printableWidth, int printableHeight)
    {
        var errs = new List<ValidationError>();
        void Err(string m) => errs.Add(new ValidationError { OperationIndex = index, Message = m });

        var action = op.Action ?? "";
        if (!LayoutActions.All.Contains(action, StringComparer.Ordinal))
        {
            Err($"\"{action}\" is not a supported action. Supported: {string.Join(", ", LayoutActions.All)}.");
            return errs;
        }

        var needsTarget = action is LayoutActions.Move or LayoutActions.Resize or LayoutActions.SetFont
            or LayoutActions.SetFontSize or LayoutActions.SetBold or LayoutActions.SetAlignment;
        var needsSection = action is LayoutActions.AddText or LayoutActions.AddLine
            or LayoutActions.AddBox or LayoutActions.ResizeSection;

        SimObject? target = null;
        if (needsTarget)
        {
            if (string.IsNullOrWhiteSpace(op.Target)) { Err($"\"{action}\" requires \"target\"."); return errs; }
            if (!objects.TryGetValue(op.Target!, out target))
            {
                Err($"Object \"{op.Target}\" does not exist in the report.");
                return errs;
            }
        }

        if (needsSection)
        {
            if (string.IsNullOrWhiteSpace(op.Section)) { Err($"\"{action}\" requires \"section\"."); return errs; }
            if (!sectionHeights.ContainsKey(op.Section!))
            {
                Err($"Section \"{op.Section}\" does not exist in the report.");
                return errs;
            }
        }

        switch (action)
        {
            case LayoutActions.Move:
            {
                if (op.LeftTwips is null) Err("\"move\" requires \"leftTwips\".");
                if (op.TopTwips is null) Err("\"move\" requires \"topTwips\".");
                if (errs.Count > 0) return errs;

                var left = op.LeftTwips!.Value;
                var top = op.TopTwips!.Value;
                if (left < 0) Err("\"leftTwips\" must not be negative.");
                if (top < 0) Err("\"topTwips\" must not be negative.");
                if (left + target!.Width > printableWidth)
                    Err($"Moving \"{op.Target}\" to leftTwips {left} puts its right edge at {left + target.Width}, past the printable width of {printableWidth}.");
                if (top + target.Height > sectionHeights[target.Section])
                    Err($"Moving \"{op.Target}\" to topTwips {top} puts its bottom edge at {top + target.Height}, past the height of section \"{target.Section}\" ({sectionHeights[target.Section]}).");
                if (errs.Count == 0) { target.Left = left; target.Top = top; }
                break;
            }

            case LayoutActions.Resize:
            {
                if (op.WidthTwips is null) Err("\"resize\" requires \"widthTwips\".");
                if (op.HeightTwips is null) Err("\"resize\" requires \"heightTwips\".");
                if (errs.Count > 0) return errs;

                var w = op.WidthTwips!.Value;
                var h = op.HeightTwips!.Value;
                if (w <= 0) Err("\"widthTwips\" must be greater than zero.");
                if (h < 0) Err("\"heightTwips\" must not be negative.");
                if (errs.Count > 0) return errs;

                if (target!.Left + w > printableWidth)
                    Err($"Resizing \"{op.Target}\" to width {w} puts its right edge at {target.Left + w}, past the printable width of {printableWidth}.");
                if (target.Top + h > sectionHeights[target.Section])
                    Err($"Resizing \"{op.Target}\" to height {h} puts its bottom edge at {target.Top + h}, past the height of section \"{target.Section}\" ({sectionHeights[target.Section]}).");
                if (errs.Count == 0) { target.Width = w; target.Height = h; }
                break;
            }

            case LayoutActions.SetFont:
                if (string.IsNullOrWhiteSpace(op.FontName)) Err("\"setFont\" requires a non-empty \"fontName\".");
                break;

            case LayoutActions.SetFontSize:
                if (op.FontSizePt is null) Err("\"setFontSize\" requires \"fontSizePt\".");
                else if (op.FontSizePt < MinFontPt || op.FontSizePt > MaxFontPt)
                    Err($"\"fontSizePt\" must be between {MinFontPt} and {MaxFontPt}; got {op.FontSizePt}.");
                break;

            case LayoutActions.SetBold:
                if (op.Bold is null) Err("\"setBold\" requires \"bold\".");
                break;

            case LayoutActions.SetAlignment:
                if (string.IsNullOrWhiteSpace(op.Alignment)) Err("\"setAlignment\" requires \"alignment\".");
                else if (!Alignments.Contains(op.Alignment!))
                    Err($"\"{op.Alignment}\" is not a valid alignment. Use Left, Right, Centre or Justified.");
                break;

            case LayoutActions.AddText:
            case LayoutActions.AddLine:
            case LayoutActions.AddBox:
            {
                if (string.IsNullOrWhiteSpace(op.NewName)) { Err($"\"{action}\" requires \"newName\"."); return errs; }
                if (objects.ContainsKey(op.NewName!)) { Err($"An object named \"{op.NewName}\" already exists in the report."); return errs; }
                if (action == LayoutActions.AddText && string.IsNullOrEmpty(op.Text)) Err("\"addText\" requires \"text\".");

                if (op.LeftTwips is null || op.TopTwips is null || op.WidthTwips is null || op.HeightTwips is null)
                { Err($"\"{action}\" requires leftTwips, topTwips, widthTwips and heightTwips."); return errs; }

                var l = op.LeftTwips.Value; var t = op.TopTwips.Value;
                var w = op.WidthTwips.Value; var h = op.HeightTwips.Value;
                if (l < 0 || t < 0) Err("Coordinates must not be negative.");
                if (w < 0 || h < 0) Err("Sizes must not be negative.");
                if (errs.Count > 0) return errs;

                if (l + w > printableWidth)
                    Err($"\"{op.NewName}\" would end at {l + w}, past the printable width of {printableWidth}.");
                if (t + h > sectionHeights[op.Section!])
                    Err($"\"{op.NewName}\" would end at {t + h}, past the height of section \"{op.Section}\" ({sectionHeights[op.Section!]}).");

                if (errs.Count == 0)
                    objects[op.NewName!] = new SimObject { Section = op.Section!, Left = l, Top = t, Width = w, Height = h };
                break;
            }

            case LayoutActions.ResizeSection:
            {
                if (op.HeightTwips is null) { Err("\"resizeSection\" requires \"heightTwips\"."); return errs; }
                var h = op.HeightTwips.Value;
                if (h < 0) { Err("\"heightTwips\" must not be negative."); return errs; }
                if (h > printableHeight)
                    Err($"Section height {h} exceeds the printable height of {printableHeight}.");
                else
                {
                    // Shrinking must not orphan an object that is already placed lower down.
                    foreach (var kv in objects.Where(k => string.Equals(k.Value.Section, op.Section, StringComparison.OrdinalIgnoreCase)))
                        if (kv.Value.Top + kv.Value.Height > h)
                            Err($"Shrinking section \"{op.Section}\" to {h} would clip \"{kv.Key}\", which ends at {kv.Value.Top + kv.Value.Height}.");

                    if (errs.Count == 0) sectionHeights[op.Section!] = h;
                }
                break;
            }
        }

        return errs;
    }

    private sealed class SimObject
    {
        public string Section = "";
        public int Left, Top, Width, Height;
    }
}
