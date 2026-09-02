using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>
    /// setCanGrow only makes sense on an object that holds flowing content it could be clipping.
    /// ISCRObjectFormat.EnableCanGrow is structurally present on every report object (a Line and a
    /// Box carry one too), so this restriction is semantic, not structural.
    /// <para>
    /// Widened from the original { Text, Field }, which was too narrow and produced a real defect:
    /// a Subreport placed in a host report clips its own contents at the container height unless
    /// the CONTAINER can grow too, and this list refused to let it. FieldHeading is included for
    /// the same reason - FieldHeadingObjectClass implements ISCRTextObject and holds real text.
    /// The project made the mirror-image mistake once before, when FontableKinds omitted
    /// FieldHeading and font changes on wizard-generated column headings were rejected with a
    /// message claiming they had no font.
    /// </para>
    /// </summary>
    private static readonly HashSet<string> CanGrowKinds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "Text", "Field", "FieldHeading", "Subreport" };

    /// <summary>
    /// addSpecialField's allowlist. An unknown specialType must be rejected here, not passed
    /// through to a COM call that would fail with a message naming a Crystal enum the caller
    /// never wrote.
    /// </summary>
    private static readonly HashSet<string> SpecialTypes =
        new HashSet<string>(SpecialFieldTypes.All, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// addGroup/addSort's direction allowlist. Everything else -- including the four TopN
    /// variants CrSortDirectionEnum also carries -- is rejected here by name, because neither
    /// operation has a field that could express the N a TopN sort needs.
    /// </summary>
    private static readonly HashSet<string> Directions =
        new HashSet<string>(SortDirections.All, StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Measured: a Group Header / Group Footer section Crystal creates for a new group starts at
    /// 250 twips, on every fixture tried. The simulation needs SOME height for the new sections so
    /// that an addText into one in the same plan can be bounds-checked; getting it wrong can only
    /// make the validator's bounds check slightly wrong (Crystal itself does not bounds-check an
    /// added object against its section), never cause a mid-plan COM failure. Pair addGroup with a
    /// resizeSection in the same plan to place taller content.
    /// </summary>
    private const int NewGroupSectionHeight = 250;

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

        // F2: a Subreport lives in TWO name-spaces at once. The placed container object carries
        // ro.Name ("Subreport1", auto-numbered by Crystal), which is what every geometry/format
        // operation targets; the embedded sub-report itself carries SubreportName, which is the
        // ONLY name SubreportController.GetSubreportLinks/SetSubreportLinks -- and therefore
        // setSubreportLink -- resolve by. Keeping a second map means setSubreportLink can be
        // validated against the right name-space, so linking a sub-report embedded by an EARLIER
        // plan (or already present in the source .rpt) works, instead of being unreachable.
        var subreportsByName = new Dictionary<string, SimObject>(StringComparer.OrdinalIgnoreCase);

        foreach (var s in sections)
            foreach (var o in s.Objects)
            {
                var sim = new SimObject
                {
                    Section = s.Name, Left = o.LeftTwips, Top = o.TopTwips,
                    Width = o.WidthTwips, Height = o.HeightTwips, Kind = o.Kind,
                    SubreportName = o.SubreportName,
                    // removeTable's safety check reads this. A Subreport's DataSource is null --
                    // it carries its OWN data source and is never bound to a main-report table --
                    // so embedding a sub-report can never make its host's tables unremovable.
                    DataSource = o.DataSource
                };
                objects[o.Name] = sim;
                if (string.Equals(o.Kind, "Subreport", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(o.SubreportName))
                    subreportsByName[o.SubreportName!] = sim;
            }

        // removeObject tracking: names removed earlier in this plan give a specific "removed by
        // this plan" message instead of the generic "does not exist" one a target that never
        // existed gets. A name is cleared from this set the moment it is reused by a later
        // addText/addLine/addBox/addField, so removeObject X followed by addText newName=X stays
        // reusable, and a *second* removeObject X after that re-add reports correctly again.
        var removedByPlan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var printableWidth = schema.Page.WidthTwips - schema.Page.MarginLeftTwips - schema.Page.MarginRightTwips;
        var printableHeight = schema.Page.HeightTwips - schema.Page.MarginTopTwips - schema.Page.MarginBottomTwips;

        // addField's security boundary: fieldRef must exactly match a formulaForm already
        // exposed by the report's data source. This is the only allowlist for addField.
        var availableFieldRefs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // The data-source table name-space, simulated cumulatively alongside the object one:
        // addTable makes an alias known to a later removeTable, and removeTable makes it unknown
        // again (and takes its fields out of addField's allowlist with it).
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fieldRefsByTable = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in schema.AvailableFields ?? new List<FieldInfo>())
        {
            if (string.IsNullOrWhiteSpace(f.FormulaForm)) continue;
            availableFieldRefs.Add(f.FormulaForm);
            if (string.IsNullOrWhiteSpace(f.TableAlias)) continue;
            tables.Add(f.TableAlias);
            if (!fieldRefsByTable.TryGetValue(f.TableAlias, out var refs))
                fieldRefsByTable[f.TableAlias] = refs = new List<string>();
            refs.Add(f.FormulaForm);
        }

        // ReportReader deliberately CLEARS AvailableFields when field enumeration fails (no
        // database connection -- a normal, supported state), so an empty list is "unknown", not
        // "this report has no tables". Absence cannot be proven from it, so the existence checks
        // below are skipped entirely rather than rejecting every table operation. The
        // still-bound-object check is unaffected: it reads the report's own objects, not this list.
        var tablesKnown = tables.Count > 0;
        var removedTablesByPlan = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // The same "empty means unknown, not absent" reasoning as tablesKnown, captured BEFORE the
        // loop so that a removeTable emptying the set part-way through does not silently turn
        // addGroup/addSort's fieldRef check off for the rest of the plan.
        var fieldRefsKnown = availableFieldRefs.Count > 0;

        var groupsAndSorts = new GroupSortSim(schema);

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

            var errors = ValidateOne(op, i, objects, subreportsByName, sectionHeights, printableWidth, printableHeight,
                availableFieldRefs, removedByPlan, tables, tablesKnown, fieldRefsByTable, removedTablesByPlan,
                fieldRefsKnown, groupsAndSorts);
            result.Errors.AddRange(errors);
        }

        result.IsValid = result.Errors.Count == 0;
        return result;
    }

    private static List<ValidationError> ValidateOne(
        LayoutOperation op, int index,
        Dictionary<string, SimObject> objects,
        Dictionary<string, SimObject> subreportsByName,
        Dictionary<string, int> sectionHeights,
        int printableWidth, int printableHeight,
        HashSet<string> availableFieldRefs,
        HashSet<string> removedByPlan,
        HashSet<string> tables,
        bool tablesKnown,
        Dictionary<string, List<string>> fieldRefsByTable,
        HashSet<string> removedTablesByPlan,
        bool fieldRefsKnown,
        GroupSortSim groupsAndSorts)
    {
        var errs = new List<ValidationError>();
        void Err(string m) => errs.Add(new ValidationError { OperationIndex = index, Message = m });

        var action = op.Action ?? "";
        if (!LayoutActions.All.Contains(action, StringComparer.Ordinal))
        {
            Err($"\"{action}\" is not a supported action. Supported: {string.Join(", ", LayoutActions.All)}.");
            return errs;
        }

        // setSuppress is the one operation whose HOST is chosen by the caller: EnableSuppress
        // exists on both ISCRSectionFormat and ISCRObjectFormat, so it is one operation taking
        // either an object or a section -- not two operations. (Contrast setFillColor /
        // setSectionBackground, which are two operations because they write two DIFFERENT
        // properties, FillColor and BackgroundColor.) Which one was supplied decides whether this
        // operation joins needsTarget or needsSection below, so it has to be settled first, and
        // an ambiguous or empty choice is rejected before either lookup is attempted.
        var suppressOnObject = false;
        var suppressOnSection = false;
        if (action == LayoutActions.SetSuppress)
        {
            suppressOnObject = !string.IsNullOrWhiteSpace(op.Target);
            suppressOnSection = !string.IsNullOrWhiteSpace(op.Section);

            if (suppressOnObject && suppressOnSection)
            { Err("\"setSuppress\" takes exactly one of \"target\" (an object) or \"section\", not both."); return errs; }
            if (!suppressOnObject && !suppressOnSection)
            { Err("\"setSuppress\" requires exactly one of \"target\" (an object) or \"section\"."); return errs; }
            if (suppressOnObject && op.SuppressIfBlank is not null)
            {
                Err("\"suppressIfBlank\" is valid only with \"section\": it is a section property " +
                    "(ISCRSectionFormat.EnableSuppressIfBlank) with no counterpart on a report object. " +
                    "Drop it, or target a section instead.");
                return errs;
            }
        }

        var needsTarget = action is LayoutActions.Move or LayoutActions.Resize or LayoutActions.SetFont
            or LayoutActions.SetFontSize or LayoutActions.SetBold or LayoutActions.SetAlignment
            or LayoutActions.RemoveObject
            or LayoutActions.SetTextColor or LayoutActions.SetFillColor or LayoutActions.SetLineColor
            or LayoutActions.SetNumberFormat or LayoutActions.SetCanGrow
            || suppressOnObject;
        // F2: setSubreportLink is deliberately NOT in needsTarget -- its target is resolved
        // against subreportsByName (the SubreportName name-space), not `objects` (the report
        // object name-space). Resolving it here would look the name up in the wrong namespace.
        //
        // The three data-source operations are excluded for exactly the same reason: their
        // "target" is a TABLE ALIAS, a third name-space again. Putting them in needsTarget (as a
        // literal reading of the brief would) would resolve "sp_perf_goal_align_detail;1" against
        // the report-object dictionary and reject every table operation with "Object ... does not
        // exist in the report." They still REQUIRE a non-empty target -- that is checked here --
        // it is only the lookup that differs.
        var needsTableTarget = action is LayoutActions.RemoveTable or LayoutActions.AddTable
            or LayoutActions.SetTableLocation;
        if (needsTableTarget && string.IsNullOrWhiteSpace(op.Target))
        { Err($"\"{action}\" requires \"target\"."); return errs; }

        var needsSection = action is LayoutActions.AddText or LayoutActions.AddLine
            or LayoutActions.AddBox or LayoutActions.ResizeSection or LayoutActions.AddField
            or LayoutActions.SetSectionBackground or LayoutActions.AddSubreport
            or LayoutActions.SetSectionBreak or LayoutActions.AddSpecialField
            || suppressOnSection;

        SimObject? target = null;
        if (needsTarget)
        {
            if (string.IsNullOrWhiteSpace(op.Target)) { Err($"\"{action}\" requires \"target\"."); return errs; }
            if (!objects.TryGetValue(op.Target!, out target))
            {
                Err(removedByPlan.Contains(op.Target!)
                    ? $"Object \"{op.Target}\" was removed earlier in this plan."
                    : $"Object \"{op.Target}\" does not exist in the report.");
                return errs;
            }

            // F1: a sub-report added earlier in THIS plan is registered under addSubreport's
            // newName, but Crystal never gives the placed object that name -- it auto-numbers the
            // container itself ("Subreport1", "Subreport2", ...) at import time, and every
            // operation below resolves through LayoutApplier.FindObject, which matches on the
            // object's own Name. Letting these through would validate clean, apply the
            // addSubreport to the live document, then throw "was not found" on the next
            // operation, faulting the session and losing the whole plan. Reject here, with the
            // reason, while nothing has been written.
            if (target!.AddedInPlan && string.Equals(target.Kind, "Subreport", StringComparison.OrdinalIgnoreCase))
            {
                Err($"\"{action}\" cannot target \"{op.Target}\": that is the newName of a sub-report " +
                    "added earlier in this same plan, and Crystal gives the placed sub-report object its own " +
                    "auto-numbered name (e.g. \"Subreport1\") at import time, so no object by that name exists " +
                    "to operate on. addSubreport already places the sub-report at the leftTwips/topTwips/" +
                    "widthTwips/heightTwips you gave it, so no geometry is lost; to move, resize, realign or " +
                    "remove it afterwards, run a second plan against the saved report and use the object name " +
                    "read_report reports for it. Only setSubreportLink can address a sub-report by this name.");
                return errs;
            }
        }

        if (needsSection)
        {
            if (string.IsNullOrWhiteSpace(op.Section)) { Err($"\"{action}\" requires \"section\"."); return errs; }

            // An addGroup earlier in this plan whose predicted section name COLLIDED with a
            // section already in the report cannot be placed into safely: the name resolves to
            // two different sections and there is no way to tell which one the caller meant.
            // Rare (it needs two same-named fields from different tables, or a pre-existing
            // group on the same field), but silently writing into the wrong section is the one
            // outcome worth refusing outright.
            if (groupsAndSorts.AmbiguousSections.Contains(op.Section!))
            {
                Err($"Section \"{op.Section}\" is ambiguous: an addGroup earlier in this plan creates a " +
                    "section whose name collides with one already in the report, so this plan cannot say " +
                    "which is meant. Run this plan without the placement, then read_report and place into " +
                    "the real section name in a second plan.");
                return errs;
            }

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

            case LayoutActions.SetTextColor:
                if (!IsFontable(target!)) { Err($"Object \"{op.Target}\" is a {target!.Kind} and has no text colour to change."); break; }
                CheckColor(op.Color, Err);
                break;

            case LayoutActions.SetFillColor:
                if (target!.Kind != "Box") { Err($"Object \"{op.Target}\" is a {target.Kind}, not a Box, and has no fill colour to change."); break; }
                CheckColor(op.Color, Err);
                break;

            case LayoutActions.SetLineColor:
                if (target!.Kind != "Line" && target.Kind != "Box")
                { Err($"Object \"{op.Target}\" is a {target.Kind}, not a Line or Box, and has no line colour to change."); break; }
                CheckColor(op.Color, Err);
                break;

            case LayoutActions.SetSectionBackground:
                // Section existence was already checked by the shared needsSection block above.
                CheckColor(op.Color, Err);
                break;

            case LayoutActions.SetSectionBreak:
                // Section existence was already checked by the shared needsSection block above.
                // Both booleans optional, but an operation supplying NEITHER is a no-op, and a
                // silent no-op is worse than a plan error: the caller believes a page break was
                // inserted and only finds out from the rendered PDF.
                if (op.NewPageBefore is null && op.NewPageAfter is null)
                    Err("\"setSectionBreak\" requires \"newPageBefore\", \"newPageAfter\", or both.");
                break;

            case LayoutActions.SetNumberFormat:
            {
                // Kind, and ONLY kind. Deliberately NOT a check that the field holds a number:
                // a field's value type is knowable only from schema.AvailableFields, which
                // ReportReader CLEARS whenever the data source cannot be enumerated (no database
                // connection -- a normal, supported state). Rejecting on the empty list would make
                // this operation fail whenever the VPN is down, which is exactly the trap
                // removeTable's existence check already sidesteps. Crystal itself carries a
                // NumericFormat on every Field object regardless of value type (measured: a String
                // field on SampleReport.rpt reports NDecimalPlaces=2), so writing one to a
                // non-numeric field is harmless and simply does not render.
                if (!string.Equals(target!.Kind, "Field", StringComparison.OrdinalIgnoreCase))
                {
                    Err($"Object \"{op.Target}\" is a {target.Kind}, not a Field, and has no number format. " +
                        "setNumberFormat applies to Field objects only.");
                    break;
                }

                if (op.DecimalPlaces is null && op.ThousandsSeparator is null && op.SuppressIfZero is null)
                {
                    Err("\"setNumberFormat\" requires at least one of \"decimalPlaces\", " +
                        "\"thousandsSeparator\" or \"suppressIfZero\".");
                    break;
                }

                if (op.DecimalPlaces is not null && (op.DecimalPlaces < 0 || op.DecimalPlaces > 10))
                    Err($"\"decimalPlaces\" must be between 0 and 10; got {op.DecimalPlaces}.");
                break;
            }

            case LayoutActions.SetCanGrow:
                if (!CanGrowKinds.Contains(target!.Kind ?? ""))
                {
                    Err($"Object \"{op.Target}\" is a {target.Kind}; \"canGrow\" applies to objects that " +
                        "hold text (Text or Field), which are the only ones with content to grow for.");
                    break;
                }
                if (op.CanGrow is null) Err("\"setCanGrow\" requires \"canGrow\".");
                break;

            case LayoutActions.SetSuppress:
                // The target/section exclusivity, the suppressIfBlank-needs-a-section rule and the
                // existence of whichever host was named have all been settled above; only the
                // required value itself is left.
                if (op.Suppress is null) Err("\"setSuppress\" requires \"suppress\".");
                break;

            case LayoutActions.AddGroup:
            case LayoutActions.AddSort:
            {
                // Neither joins needsTarget nor needsSection: they address a FIELD, not a report
                // object and not a section. addGroup CREATES sections rather than placing into one.
                if (string.IsNullOrWhiteSpace(op.FieldRef))
                { Err($"\"{action}\" requires \"fieldRef\"."); return errs; }

                if (action == LayoutActions.AddSort && string.IsNullOrWhiteSpace(op.Direction))
                { Err("\"addSort\" requires \"direction\" (\"ascending\" or \"descending\")."); return errs; }

                if (!string.IsNullOrWhiteSpace(op.Direction) && !Directions.Contains(op.Direction!))
                {
                    Err($"\"{op.Direction}\" is not a supported direction; use \"ascending\" or \"descending\". " +
                        "Crystal's topN, bottomN, topNPercentage and bottomNPercentage orders are deliberately " +
                        "NOT supported: each needs an N that this operation has no field to express.");
                    return errs;
                }

                // Skipped entirely when the field list is unknown -- ReportReader CLEARS
                // AvailableFields whenever the data source cannot be enumerated (no database
                // connection), which is a normal supported state, and rejecting on an empty list
                // would break both operations whenever the VPN is down. Same trap removeTable's
                // existence check and setNumberFormat's missing type check already sidestep.
                // Unlike addField this is not a security boundary: neither operation binds new
                // data into the report, so there is nothing to fail closed about.
                if (fieldRefsKnown && !availableFieldRefs.Contains(op.FieldRef!))
                {
                    Err($"\"{op.FieldRef}\" is not a field in this report's data source. " +
                        "Use one of the formulaForm values from the report schema's availableFields.");
                    return errs;
                }

                if (action == LayoutActions.AddGroup)
                {
                    // Measured: Crystal answers a second group on an already-grouped field with
                    // COMException "The grouping already exists." Catch it here, where nothing has
                    // been written yet, rather than mid-plan.
                    if (groupsAndSorts.GroupFields.Contains(op.FieldRef!))
                    {
                        Err($"This report is already grouped on \"{op.FieldRef}\". Crystal refuses a second " +
                            "group on the same field (\"The grouping already exists.\").");
                        return errs;
                    }
                    if (!CheckIndex(op.GroupIndex, groupsAndSorts.GroupCount, "groupIndex", "groups", Err)) return errs;
                    groupsAndSorts.AddGroup(op.GroupIndex, op.FieldRef!, sectionHeights, NewGroupSectionHeight);
                }
                else
                {
                    // Measured: a second sort on an already-sorted field is COMException "The
                    // sorting already exists". Every group carries a sort of its own, so this also
                    // covers addSort against a field the report (or this plan) groups on.
                    if (groupsAndSorts.SortFields.Contains(op.FieldRef!))
                    {
                        Err($"This report is already sorted on \"{op.FieldRef}\", and Crystal refuses a second " +
                            "sort on the same field (\"The sorting already exists\"). Note that every group " +
                            "carries a sort on its own field, so a grouped field is already sorted; use " +
                            "addGroup's \"direction\" to choose a group's order.");
                        return errs;
                    }
                    if (!CheckIndex(op.SortIndex, groupsAndSorts.SortCount, "sortIndex", "sorts", Err)) return errs;
                    groupsAndSorts.AddSort(op.SortIndex, op.FieldRef!);
                }
                break;
            }

            case LayoutActions.AddText:
            case LayoutActions.AddLine:
            case LayoutActions.AddBox:
            case LayoutActions.AddField:
            case LayoutActions.AddSubreport:
            // addSpecialField shares this whole block deliberately: it is an ADD, so it needs the
            // same newName-required, newName-unique and geometry-bounds checks every other add
            // gets, and KindForAdd registers it as a "Field" so setFontSize/setBold on a special
            // field placed earlier in the same plan validate rather than being refused as
            // unfontable.
            case LayoutActions.AddSpecialField:
            {
                if (string.IsNullOrWhiteSpace(op.NewName)) { Err($"\"{action}\" requires \"newName\"."); return errs; }
                if (objects.ContainsKey(op.NewName!)) { Err($"An object named \"{op.NewName}\" already exists in the report."); return errs; }
                // F5: addSubreport's newName lands in the SubreportName name-space, not the
                // report-object one, so the check above looks at the wrong set for it. A source
                // report that already embeds a sub-report named "GoalDetail" would otherwise
                // collide invisibly -- and setSubreportLink, which resolves through that same
                // name-space, could then no longer say which of the two it meant.
                if (action == LayoutActions.AddSubreport && subreportsByName.ContainsKey(op.NewName!))
                { Err($"A sub-report named \"{op.NewName}\" is already embedded in this report."); return errs; }
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

                if (action == LayoutActions.AddSpecialField)
                {
                    if (string.IsNullOrWhiteSpace(op.SpecialType))
                    {
                        Err($"\"addSpecialField\" requires \"specialType\". Supported: {string.Join(", ", SpecialFieldTypes.All)}.");
                        return errs;
                    }
                    if (!SpecialTypes.Contains(op.SpecialType!))
                    {
                        Err($"\"{op.SpecialType}\" is not a supported specialType. " +
                            $"Supported: {string.Join(", ", SpecialFieldTypes.All)}.");
                        return errs;
                    }
                }

                if (action == LayoutActions.AddSubreport)
                {
                    // Pure check only: does NOT touch the filesystem. The applier checks the file
                    // actually exists and throws a clear message naming the path.
                    if (string.IsNullOrWhiteSpace(op.ReportPath))
                    {
                        Err("\"addSubreport\" requires \"reportPath\".");
                        return errs;
                    }
                    if (!op.ReportPath!.EndsWith(".rpt", StringComparison.OrdinalIgnoreCase))
                    {
                        Err($"\"reportPath\" must end in \".rpt\"; got \"{op.ReportPath}\".");
                        return errs;
                    }
                    // F7: the tool contract promises an absolute path, and nothing checked it. A
                    // relative path is not an error the applier can report usefully either -- it
                    // resolves against the WORKER process's working directory, which the agent has
                    // no visibility of, so "file not found" would name a path the agent never
                    // wrote. Path.IsPathRooted is pure string arithmetic (no file I/O), so it
                    // belongs here beside the suffix check rather than in the applier.
                    if (!Path.IsPathRooted(op.ReportPath!))
                    {
                        Err($"\"reportPath\" must be an absolute path; got \"{op.ReportPath}\", which would be " +
                            "resolved against the worker process's own working directory.");
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
                {
                    var added = new SimObject
                    {
                        Section = op.Section!, Left = l, Top = t, Width = w, Height = h,
                        Kind = KindForAdd(action), AddedInPlan = true,
                        // Only addField binds to a table. Carrying the fieldRef here is what makes
                        // "addField from table T, then removeTable T" reject in the same plan.
                        // addSpecialField deliberately leaves this null even though it also adds a
                        // Field: a special field is computed by Crystal (page number, print date)
                        // and belongs to no table, so it must never make a table unremovable.
                        DataSource = action == LayoutActions.AddField ? op.FieldRef : null
                    };
                    objects[op.NewName!] = added;
                    // F1/F2: registering the added sub-report in BOTH maps keeps newName
                    // uniqueness honest against later adds, while AddedInPlan makes every
                    // non-setSubreportLink operation on it fail at validation time with the
                    // reason (see the needsTarget block above). Only setSubreportLink, which
                    // resolves through this second map, can address it.
                    if (action == LayoutActions.AddSubreport)
                    {
                        added.SubreportName = op.NewName;
                        subreportsByName[op.NewName!] = added;
                    }
                }
                break;
            }

            case LayoutActions.RemoveObject:
                // Target existence/removed-earlier was already checked by the shared needsTarget
                // block above. Deleting the entry frees the name for a later addText/addLine/
                // addBox/addField, and drops it from any section-height shrink check that runs
                // afterwards since that check only walks what remains in `objects`.
                // F2: removing a Subreport's container object takes the embedded sub-report with
                // it, so drop it from the SubreportName map too -- otherwise a later
                // setSubreportLink would still resolve against a sub-report this plan deleted.
                if (!string.IsNullOrWhiteSpace(target!.SubreportName)) subreportsByName.Remove(target.SubreportName!);
                objects.Remove(op.Target!);
                removedByPlan.Add(op.Target!);
                break;

            case LayoutActions.SetSubreportLink:
            {
                // F2: resolved against subreportsByName, NOT `objects`. SetSubreportLinks is keyed
                // by the sub-report's own SubreportName; the placed container object's Name is a
                // different, Crystal-auto-numbered string, and passing it through fails at the COM
                // boundary with the unhelpful "This value is write-only." (measured). This map
                // holds both sub-reports already embedded in the source .rpt (so cross-plan
                // linking works) and any added earlier in this same plan.
                if (string.IsNullOrWhiteSpace(op.Target)) { Err("\"setSubreportLink\" requires \"target\"."); return errs; }

                if (!subreportsByName.ContainsKey(op.Target!))
                {
                    // The single most likely mistake, and the one whose default message is most
                    // misleading: the agent passed the object Name from read_report instead of the
                    // subreportName beside it. Say so, and name the string that would have worked.
                    if (objects.TryGetValue(op.Target!, out var byObjectName)
                        && string.Equals(byObjectName.Kind, "Subreport", StringComparison.OrdinalIgnoreCase)
                        && !string.IsNullOrWhiteSpace(byObjectName.SubreportName))
                    {
                        Err($"\"{op.Target}\" is the placed object name of a sub-report, which setSubreportLink " +
                            $"cannot resolve. Use its \"subreportName\" instead: \"{byObjectName.SubreportName}\".");
                    }
                    else if (objects.TryGetValue(op.Target!, out var other))
                    {
                        Err($"Object \"{op.Target}\" is a {other.Kind}, not a Subreport, and cannot take a subreport link.");
                    }
                    else
                    {
                        Err(removedByPlan.Contains(op.Target!)
                            ? $"Sub-report \"{op.Target}\" was removed earlier in this plan."
                            : $"No sub-report named \"{op.Target}\" is embedded in this report. " +
                              "Use a Subreport object's \"subreportName\" from read_report, or the \"newName\" " +
                              "of an addSubreport earlier in this plan.");
                    }
                    return errs;
                }

                if (string.IsNullOrWhiteSpace(op.MainReportField)) Err("\"setSubreportLink\" requires \"mainReportField\".");
                if (string.IsNullOrWhiteSpace(op.SubreportField)) Err("\"setSubreportLink\" requires \"subreportField\".");
                // F4: "linkedParameter" is deliberately OPTIONAL. Measured against the installed
                // 11.5 RAS: Crystal DISCARDS whatever LinkedParameterName is written and
                // substitutes its own "{?Pm-<mainReportField>}". Requiring a value with no effect
                // only invites the agent to invent a stored-procedure parameter name and believe
                // it was wired up. See docs/sdk-notes.md.
                break;
            }

            case LayoutActions.RemoveTable:
            {
                if (!TableExists(op.Target!, tables, tablesKnown, removedTablesByPlan, action, Err)) return errs;

                // THE rule this operation exists for. Crystal itself does NOT protect against
                // this: measured against out/reports/PMSV10_GoalAlignCascade.rpt, RemoveTable
                // removed "sp_perf_goal_align_detail;1" cleanly while five Field objects (DR0-DR4)
                // were still bound to it, leaving a report full of unresolvable fields. The
                // validator is the only gate.
                //
                // A field object's binding is its ObjectInfo.DataSource in formula form, e.g.
                // "{sp_perf_goal_align_detail;1.goal_id}". Matched as the prefix "{" + alias + "."
                // ordinally and case-insensitively -- NOT with Contains, which would let alias
                // "foo;1" match "{other_foo;1.x}" and refuse a legal removal.
                var prefix = "{" + op.Target + ".";
                var bound = new List<string>();
                foreach (var kv in objects)
                    if (kv.Value.DataSource != null
                        && kv.Value.DataSource.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        bound.Add(kv.Key);

                if (bound.Count > 0)
                {
                    bound.Sort(StringComparer.OrdinalIgnoreCase);
                    Err($"Table \"{op.Target}\" cannot be removed while {bound.Count} object(s) are still bound " +
                        $"to it: {string.Join(", ", bound)}. Remove those objects (removeObject) earlier in this " +
                        "same plan, then removeTable will be accepted.");
                    return errs;
                }

                tables.Remove(op.Target!);
                removedTablesByPlan.Add(op.Target!);
                // The table's fields leave addField's allowlist with it, so a later addField
                // referencing the removed table is rejected instead of binding to nothing.
                if (fieldRefsByTable.TryGetValue(op.Target!, out var goneRefs))
                    foreach (var r in goneRefs) availableFieldRefs.Remove(r);
                break;
            }

            case LayoutActions.AddTable:
            {
                // "target" is the alias of an EXISTING table whose ConnectionInfo is cloned. That
                // clone is how addTable stays credential-free: no server, database, user name or
                // password is ever expressed in a plan.
                if (!TableExists(op.Target!, tables, tablesKnown, removedTablesByPlan, action, Err)) return errs;
                if (string.IsNullOrWhiteSpace(op.TableName)) { Err("\"addTable\" requires \"tableName\"."); return errs; }
                if (string.IsNullOrWhiteSpace(op.NewName)) { Err("\"addTable\" requires \"newName\"."); return errs; }
                if (tables.Contains(op.NewName!))
                { Err($"A table with the alias \"{op.NewName}\" is already in this report's data source."); return errs; }

                tables.Add(op.NewName!);
                // Deliberately NO entry in availableFieldRefs: the plan cannot know which fields
                // the added table exposes (they come from the server), so an addField against it
                // in the same plan stays rejected. Re-read the report after the apply.
                break;
            }

            case LayoutActions.SetTableLocation:
            {
                if (!TableExists(op.Target!, tables, tablesKnown, removedTablesByPlan, action, Err)) return errs;
                if (string.IsNullOrWhiteSpace(op.TableName)) { Err("\"setTableLocation\" requires \"tableName\"."); return errs; }
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

    /// <summary>
    /// Existence check for a data-source table alias, skipped entirely when the alias set is
    /// unknown (see tablesKnown at the call site). Returns false when an error was reported.
    /// </summary>
    private static bool TableExists(
        string alias, HashSet<string> tables, bool tablesKnown,
        HashSet<string> removedTablesByPlan, string action, Action<string> err)
    {
        if (tables.Contains(alias)) return true;

        if (removedTablesByPlan.Contains(alias))
        {
            err($"Table \"{alias}\" was removed earlier in this plan.");
            return false;
        }

        // Not known to be absent -- only known not to be listed, and the list may simply be
        // unavailable. Let it through; the applier reports what the document actually holds.
        if (!tablesKnown) return true;

        err($"\"{action}\": no table with the alias \"{alias}\" is in this report's data source. " +
            $"Known aliases: {string.Join(", ", Sorted(tables))}.");
        return false;
    }

    /// <summary>
    /// Bounds check for addGroup's groupIndex / addSort's sortIndex: 0-based, and equal to the
    /// current count means "append", so the count itself is legal. Counted against the CUMULATIVE
    /// simulation, so two addGroups in one plan index correctly. Returns false when an error was
    /// reported.
    /// </summary>
    private static bool CheckIndex(int? index, int count, string field, string what, Action<string> err)
    {
        if (index is null) return true;
        if (index.Value < 0) { err($"\"{field}\" must not be negative; got {index.Value}."); return false; }
        if (index.Value > count)
        {
            err($"\"{field}\" {index.Value} is past the end: this report has {count} {what}, so the " +
                $"largest accepted value is {count} (which appends).");
            return false;
        }
        return true;
    }

    /// <summary>
    /// The group and sort name-spaces, simulated cumulatively alongside the object, section and
    /// table ones. Groups and sorts are keyed by the FIELD they act on, which is a fourth
    /// name-space again -- Crystal identifies a group by its condition field, not by a name.
    /// </summary>
    private sealed class GroupSortSim
    {
        private readonly List<string> _groups = new List<string>();
        private readonly List<string> _sorts = new List<string>();

        public HashSet<string> GroupFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> SortFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Predicted group-section names that collided with a section already in the report, and
        /// so cannot be placed into by this plan. See the needsSection block.
        /// </summary>
        public HashSet<string> AmbiguousSections { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public GroupSortSim(ReportSchema schema)
        {
            foreach (var g in schema.Groups ?? new List<GroupInfo>())
            {
                if (g == null || string.IsNullOrWhiteSpace(g.FieldRef)) continue;
                _groups.Add(g.FieldRef);
                GroupFields.Add(g.FieldRef);
            }
            foreach (var s in schema.Sorts ?? new List<SortInfo>())
            {
                if (s == null || string.IsNullOrWhiteSpace(s.FieldRef)) continue;
                _sorts.Add(s.FieldRef);
                SortFields.Add(s.FieldRef);
            }
        }

        public int GroupCount => _groups.Count;
        public int SortCount => _sorts.Count;

        public void AddGroup(int? index, string fieldRef, Dictionary<string, int> sectionHeights, int newHeight)
        {
            Insert(_groups, index, fieldRef);
            GroupFields.Add(fieldRef);

            // Measured: GroupController.Add creates a sort for the group's field at the same
            // time -- unless the field already carried one, which it then adopts (keeping that
            // sort's existing direction). Either way the field is sorted afterwards, which is
            // what a later addSort on it has to be judged against.
            if (SortFields.Add(fieldRef)) Insert(_sorts, index, fieldRef);

            // THE reason this simulation exists: registering the sections Crystal will create
            // means [addGroup, addText into the new group header] validates as ONE plan. The
            // names are predicted, never invented -- GroupSectionNaming carries the measured rule.
            RegisterSection(GroupSectionNaming.HeaderSection(fieldRef), sectionHeights, newHeight);
            RegisterSection(GroupSectionNaming.FooterSection(fieldRef), sectionHeights, newHeight);
        }

        public void AddSort(int? index, string fieldRef)
        {
            Insert(_sorts, index, fieldRef);
            SortFields.Add(fieldRef);
        }

        private void RegisterSection(string name, Dictionary<string, int> sectionHeights, int newHeight)
        {
            if (sectionHeights.ContainsKey(name)) AmbiguousSections.Add(name);
            else sectionHeights[name] = newHeight;
        }

        private static void Insert(List<string> list, int? index, string value)
        {
            if (index.HasValue && index.Value >= 0 && index.Value <= list.Count) list.Insert(index.Value, value);
            else list.Add(value);
        }
    }

    private static IEnumerable<string> Sorted(HashSet<string> values)
    {
        var list = new List<string>(values);
        list.Sort(StringComparer.OrdinalIgnoreCase);
        return list;
    }

    private static bool IsFontable(SimObject o) =>
        FontableKinds.Contains(o.Kind ?? "");

    /// <summary>
    /// The only input the AI can get wrong in a hundred ways ("red", "rgb(255,0,0)", "#F00",
    /// "1F2A37" with no hash, trailing whitespace, ...), so the rejection message states the
    /// exact expected form rather than a generic "invalid colour".
    /// </summary>
    private static void CheckColor(string? color, Action<string> err)
    {
        if (!ColorRef.IsValidHex(color))
            err($"\"{color}\" is not a valid colour; expected the form \"#RRGGBB\" (e.g. \"#1F2A37\").");
    }

    private static string KindForAdd(string action) =>
        action == LayoutActions.AddText          ? "Text"
      : action == LayoutActions.AddLine          ? "Line"
      : action == LayoutActions.AddField         ? "Field"
      // A special field IS a field object -- ReportReader classifies the placed object as
      // Kind "Field" (measured: SampleReport's PrintDate1/PageNumber1 are
      // crReportObjectKindField), so the simulation must agree or a setFontSize on one added
      // earlier in the same plan would be judged against the wrong kind.
      : action == LayoutActions.AddSpecialField  ? "Field"
      : action == LayoutActions.AddSubreport     ? "Subreport"
      : "Box";

    private sealed class SimObject
    {
        public string Section = "";
        public string Kind = "";
        public int Left, Top, Width, Height;

        /// <summary>Kind == "Subreport" only: the sub-report's own name, setSubreportLink's key.</summary>
        public string? SubreportName;

        /// <summary>
        /// The object's binding in formula form, e.g. "{sp_x;1.goal_id}", or null when it has
        /// none (Text/Line/Box/Picture -- and Subreport, which carries its own data source).
        /// Seeded from ObjectInfo.DataSource and, for an addField earlier in the same plan, from
        /// that operation's fieldRef. removeTable's bound-object check is the only reader.
        /// </summary>
        public string? DataSource;

        /// <summary>
        /// True for an object this plan adds. Only consulted for Subreport, where the name the
        /// plan chose is never the name Crystal gives the placed object -- see F1 in the
        /// needsTarget block.
        /// </summary>
        public bool AddedInPlan;
    }
}
