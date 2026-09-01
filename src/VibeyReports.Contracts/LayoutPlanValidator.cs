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
                availableFieldRefs, removedByPlan, tables, tablesKnown, fieldRefsByTable, removedTablesByPlan);
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
        HashSet<string> removedTablesByPlan)
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
            or LayoutActions.SetFontSize or LayoutActions.SetBold or LayoutActions.SetAlignment
            or LayoutActions.RemoveObject
            or LayoutActions.SetTextColor or LayoutActions.SetFillColor or LayoutActions.SetLineColor;
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
            or LayoutActions.SetSectionBackground or LayoutActions.AddSubreport;

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

            case LayoutActions.AddText:
            case LayoutActions.AddLine:
            case LayoutActions.AddBox:
            case LayoutActions.AddField:
            case LayoutActions.AddSubreport:
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
        action == LayoutActions.AddText       ? "Text"
      : action == LayoutActions.AddLine       ? "Line"
      : action == LayoutActions.AddField      ? "Field"
      : action == LayoutActions.AddSubreport  ? "Subreport"
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
