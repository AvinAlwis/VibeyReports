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

    // F3 (final review): FieldHeadingObjectClass implements ISCRTextObject and therefore does
    // carry a font -- ReportReader already reports fontName/fontSizePt/bold for it, and
    // LayoutApplier.WithFont's ISCRTextObject case already handles it. Only this allowlist was
    // out of sync, wrongly refusing font changes on wizard-generated column headings (which
    // Crystal places as FieldHeading objects) with a message claiming they have no font.
    private static readonly HashSet<string> FontableKinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Text", "Field", "FieldHeading" };

    public static ValidationResult Validate(LayoutPlan plan, ReportSchema schema)
    {
        var result = new ValidationResult { IsValid = true };
        if (plan == null) throw new ArgumentNullException(nameof(plan));
        if (schema == null) throw new ArgumentNullException(nameof(schema));

        // F5: a validator that gates model-generated JSON must return IsValid = false on
        // malformed input, never throw. A null "operations" array is malformed input.
        if (plan.Operations == null)
        {
            result.Errors.Add(new ValidationError { OperationIndex = -1, Message = "Plan \"operations\" is null." });
            result.IsValid = false;
            return result;
        }

        // F5: a schema with no page information cannot be checked against; that is an
        // error to report, not a crash to throw.
        if (schema.Page == null)
        {
            result.Errors.Add(new ValidationError { OperationIndex = -1, Message = "Schema has no page information." });
            result.IsValid = false;
            return result;
        }

        var sections = schema.Sections ?? new List<SectionInfo>();

        // Mutable simulation state: sectionName -> height, objectName -> (section, l, t, w, h, kind)
        // F7: build defensively with an indexer, not ToDictionary, so a duplicate or
        // case-variant section name cannot crash the validator (last one wins).
        var sectionHeights = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sections)
            sectionHeights[s.Name] = s.HeightTwips;

        var objects = new Dictionary<string, SimObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in sections)
            foreach (var o in s.Objects)
                objects[o.Name] = new SimObject
                {
                    Section = s.Name, Left = o.LeftTwips, Top = o.TopTwips,
                    Width = o.WidthTwips, Height = o.HeightTwips, Kind = o.Kind
                };

        var printableWidth = schema.Page.WidthTwips - schema.Page.MarginLeftTwips - schema.Page.MarginRightTwips;
        var printableHeight = schema.Page.HeightTwips - schema.Page.MarginTopTwips - schema.Page.MarginBottomTwips;

        // addField's security boundary: fieldRef must exactly match a formulaForm already
        // exposed by the report's data source. This is the only allowlist for addField.
        var availableFieldRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in schema.AvailableFields ?? new List<FieldInfo>())
            if (!string.IsNullOrWhiteSpace(f.FormulaForm)) availableFieldRefs.Add(f.FormulaForm);

        for (var i = 0; i < plan.Operations.Count; i++)
        {
            var op = plan.Operations[i];
            // F5: a null element in the operations array is a validation error at that
            // index, not a crash.
            if (op == null)
            {
                result.Errors.Add(new ValidationError { OperationIndex = i, Message = "Operation is null." });
                continue;
            }

            var errors = ValidateOne(op, i, objects, sectionHeights, printableWidth, printableHeight, availableFieldRefs);
            result.Errors.AddRange(errors);
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    private static List<ValidationError> ValidateOne(
        LayoutOperation op, int index,
        Dictionary<string, SimObject> objects,
        Dictionary<string, int> sectionHeights,
        int printableWidth, int printableHeight,
        HashSet<string> availableFieldRefs)
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
            or LayoutActions.AddBox or LayoutActions.ResizeSection or LayoutActions.AddField;

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

                // F2: compare in long so a huge leftTwips/topTwips cannot wrap the sum
                // negative and slip past the bound.
                // F4: "don't make it worse" — real legacy reports routinely already have
                // objects whose edge sits past the printable margin/section height, and a
                // move that keeps that edge no worse than it already was must be allowed,
                // even though a resize or a brand-new object still gets the hard check.
                var oldRight = (long)target!.Left + target.Width;
                var oldBottom = (long)target.Top + target.Height;
                var newRight = (long)left + target.Width;
                var newBottom = (long)top + target.Height;

                if (newRight > printableWidth && newRight > oldRight)
                    Err($"Moving \"{op.Target}\" to leftTwips {left} puts its right edge at {newRight}, past the printable width of {printableWidth}.");
                if (newBottom > sectionHeights[target.Section] && newBottom > oldBottom)
                    Err($"Moving \"{op.Target}\" to topTwips {top} puts its bottom edge at {newBottom}, past the height of section \"{target.Section}\" ({sectionHeights[target.Section]}).");
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
                // F3: zero is a legal width (a vertical rule). Only negative is rejected,
                // matching the height rule and the add branch below.
                if (w < 0) Err("\"widthTwips\" must not be negative.");
                if (h < 0) Err("\"heightTwips\" must not be negative.");
                if (errs.Count > 0) return errs;

                // F1: RAS only supports horizontal or vertical lines; a both-axes line is
                // rejected at the COM boundary, so a resize that would make a Line diagonal
                // must be caught here instead of throwing a raw COMException in the applier.
                if (target!.Kind == "Line" && w != 0 && h != 0)
                    Err($"A line must be horizontal or vertical: set widthTwips or heightTwips to 0 (got {w} x {h}).");
                if (errs.Count > 0) return errs;

                // F2: long arithmetic so a huge width/height cannot wrap the sum negative.
                var newRight = (long)target!.Left + w;
                var newBottom = (long)target.Top + h;
                if (newRight > printableWidth)
                    Err($"Resizing \"{op.Target}\" to width {w} puts its right edge at {newRight}, past the printable width of {printableWidth}.");
                if (newBottom > sectionHeights[target.Section])
                    Err($"Resizing \"{op.Target}\" to height {h} puts its bottom edge at {newBottom}, past the height of section \"{target.Section}\" ({sectionHeights[target.Section]}).");
                if (errs.Count == 0) { target.Width = w; target.Height = h; }
                break;
            }

            case LayoutActions.SetFont:
                // F1: a Line/Box has no font to change; catch it here instead of letting
                // it pass validation and blow up Task 6's applier mid-plan.
                if (!IsFontable(target!)) { Err($"Object \"{op.Target}\" is a {target!.Kind} and has no font to change."); break; }
                if (string.IsNullOrWhiteSpace(op.FontName)) Err("\"setFont\" requires a non-empty \"fontName\".");
                break;

            case LayoutActions.SetFontSize:
                if (!IsFontable(target!)) { Err($"Object \"{op.Target}\" is a {target!.Kind} and has no font to change."); break; }
                if (op.FontSizePt is null) Err("\"setFontSize\" requires \"fontSizePt\".");
                else if (op.FontSizePt < MinFontPt || op.FontSizePt > MaxFontPt)
                    Err($"\"fontSizePt\" must be between {MinFontPt} and {MaxFontPt}; got {op.FontSizePt}.");
                break;

            case LayoutActions.SetBold:
                if (!IsFontable(target!)) { Err($"Object \"{op.Target}\" is a {target!.Kind} and has no font to change."); break; }
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
            case LayoutActions.AddField:
            {
                if (string.IsNullOrWhiteSpace(op.NewName)) { Err($"\"{action}\" requires \"newName\"."); return errs; }
                if (objects.ContainsKey(op.NewName!)) { Err($"An object named \"{op.NewName}\" already exists in the report."); return errs; }
                if (action == LayoutActions.AddText && string.IsNullOrEmpty(op.Text)) Err("\"addText\" requires \"text\".");

                if (action == LayoutActions.AddField)
                {
                    if (string.IsNullOrWhiteSpace(op.FieldRef))
                    {
                        Err("\"addField\" requires \"fieldRef\".");
                        return errs;
                    }
                    if (!availableFieldRefs.Contains(op.FieldRef!))
                    {
                        Err($"\"{op.FieldRef}\" is not a field in this report's data source. " +
                            "Use one of the formulaForm values from the report schema's availableFields.");
                        return errs;
                    }
                }

                if (op.LeftTwips is null || op.TopTwips is null || op.WidthTwips is null || op.HeightTwips is null)
                { Err($"\"{action}\" requires leftTwips, topTwips, widthTwips and heightTwips."); return errs; }

                var l = op.LeftTwips.Value; var t = op.TopTwips.Value;
                var w = op.WidthTwips.Value; var h = op.HeightTwips.Value;
                if (l < 0 || t < 0) Err("Coordinates must not be negative.");
                if (w < 0 || h < 0) Err("Sizes must not be negative.");
                if (errs.Count > 0) return errs;

                // F1: RAS only supports horizontal or vertical lines; a both-axes line is
                // rejected at the COM boundary, so this must be caught here instead of
                // throwing a raw COMException in the applier.
                if (action == LayoutActions.AddLine && w != 0 && h != 0)
                    Err($"A line must be horizontal or vertical: set widthTwips or heightTwips to 0 (got {w} x {h}).");
                if (errs.Count > 0) return errs;

                // F2: long arithmetic so a huge left/top/width/height cannot wrap negative.
                var right = (long)l + w;
                var bottom = (long)t + h;
                if (right > printableWidth)
                    Err($"\"{op.NewName}\" would end at {right}, past the printable width of {printableWidth}.");
                if (bottom > sectionHeights[op.Section!])
                    Err($"\"{op.NewName}\" would end at {bottom}, past the height of section \"{op.Section}\" ({sectionHeights[op.Section!]}).");

                if (errs.Count == 0)
                    objects[op.NewName!] = new SimObject
                    {
                        Section = op.Section!, Left = l, Top = t, Width = w, Height = h,
                        Kind = KindForAdd(action)
                    };
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
                    // Shrinking must not orphan an object that is already placed lower down
                    // (including one added earlier in this same plan).
                    foreach (var kv in objects.Where(k => string.Equals(k.Value.Section, op.Section, StringComparison.OrdinalIgnoreCase)))
                    {
                        // F2: long arithmetic so a huge Top/Height on a simulated object
                        // cannot wrap the sum negative and dodge the clip check.
                        var bottom = (long)kv.Value.Top + kv.Value.Height;
                        if (bottom > h)
                            Err($"Shrinking section \"{op.Section}\" to {h} would clip \"{kv.Key}\", which ends at {bottom}.");
                    }

                    if (errs.Count == 0) sectionHeights[op.Section!] = h;
                }
                break;
            }

            default:
                // F6: LayoutActions.All is the allowlist and this switch is the
                // enforcement. An eleventh constant added to All without a matching case
                // must not pass validation with zero checks — it must fail loud here,
                // not silently, before it ever reaches Task 6's own throw.
                Err($"\"{action}\" is allowlisted but has no validation rule.");
                break;
        }

        return errs;
    }

    private static bool IsFontable(SimObject o) =>
        FontableKinds.Contains(o.Kind ?? "");

    private static string KindForAdd(string action) =>
        action == LayoutActions.AddText  ? "Text"
      : action == LayoutActions.AddLine  ? "Line"
      : action == LayoutActions.AddField ? "Field"
      : "Box";

    private sealed class SimObject
    {
        public string Section = "";
        public string Kind = "";
        public int Left, Top, Width, Height;
    }
}
