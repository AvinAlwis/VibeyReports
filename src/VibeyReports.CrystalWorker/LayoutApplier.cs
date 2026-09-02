using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CrystalDecisions.ReportAppServer.ClientDoc;
using CrystalDecisions.ReportAppServer.Controllers;
using CrystalDecisions.ReportAppServer.DataDefModel;
using CrystalDecisions.ReportAppServer.ReportDefModel;
using VibeyReports.Contracts;

// F1 now rejects a both-axes-non-zero line at validation time, which makes it impossible to drive
// LayoutApplier.Apply's own mid-plan COMException/faulted-session path through the public API with a
// validator-legal-but-applier-hostile operation. ApplyOperationsWithoutValidation below is the
// deliberate seam the round-2 fix report documents for exercising that path directly in tests.
[assembly: InternalsVisibleTo("VibeyReports.CrystalWorker.Tests")]

namespace VibeyReports.CrystalWorker
{
    public static class LayoutApplier
    {
        /// <summary>
        /// What <see cref="Apply"/> did, so removals travel back to the caller alongside the
        /// count. An AI agent driving apply_layout needs to see exactly which named objects it
        /// just destroyed -- OperationsApplied alone cannot distinguish "moved 3 things" from
        /// "removed 3 things", and a removeObject deletes data from the report permanently.
        /// </summary>
        public sealed class ApplyResult
        {
            public int OperationsApplied { get; set; }
            public List<string> RemovedObjects { get; set; } = new List<string>();

            /// <summary>
            /// Aliases deleted by a "removeTable" operation. Same reasoning as RemovedObjects: a
            /// destructive operation must say what it destroyed, and removing a table takes every
            /// field it exposed out of the report with it.
            /// </summary>
            public List<string> RemovedTables { get; set; } = new List<string>();
        }

        public sealed class InvalidPlanException : Exception
        {
            public InvalidPlanException(ValidationResult result)
                : base("Layout plan failed validation: " +
                       string.Join(" | ", result.Errors.Select(e => $"[op {e.OperationIndex}] {e.Message}")))
            {
                Result = result;
            }

            public ValidationResult Result { get; }
        }

        /// <summary>
        /// Validates the plan against the report's current layout, then applies every operation.
        /// Throws <see cref="InvalidPlanException"/> before touching the report if validation fails.
        /// </summary>
        public static ApplyResult Apply(CrystalSession session, LayoutPlan plan)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));
            if (plan == null) throw new ArgumentNullException(nameof(plan));

            var schema = ReportReader.Read(session);
            var validation = LayoutPlanValidator.Validate(plan, schema);
            if (!validation.IsValid) throw new InvalidPlanException(validation);

            return ApplyOperationsWithoutValidation(session, plan);
        }

        /// <summary>
        /// The mutation loop, with no call to LayoutPlanValidator.Validate. Test-only seam: F1 means
        /// no validator-legal plan can any longer reach an applier-level failure (e.g. a diagonal
        /// line's raw COMException) through the public <see cref="Apply"/> entry point, so the
        /// round-2 fix-report's F2 regression test (faulted session refuses to save) calls this
        /// internal method directly with a plan that Validate would now reject. Production code must
        /// always go through <see cref="Apply"/>.
        /// </summary>
        internal static ApplyResult ApplyOperationsWithoutValidation(CrystalSession session, LayoutPlan plan)
        {
            var doc = session.Document;
            var result = new ApplyResult();

            foreach (var op in plan.Operations)
            {
                try
                {
                    switch (op.Action)
                    {
                        case LayoutActions.Move:
                            ModifyObject(doc, op.Target, o =>
                            {
                                o.Left = op.LeftTwips.Value;
                                o.Top = op.TopTwips.Value;
                                SyncEndpoints(o);
                            });
                            break;

                        case LayoutActions.Resize:
                            ModifyObject(doc, op.Target, o =>
                            {
                                o.Width = op.WidthTwips.Value;
                                o.Height = op.HeightTwips.Value;
                                SyncEndpoints(o);
                            });
                            break;

                        case LayoutActions.SetFont:
                            ModifyObject(doc, op.Target, o => WithFont(o, f => f.Name = op.FontName));
                            break;

                        case LayoutActions.SetFontSize:
                            ModifyObject(doc, op.Target, o => WithFont(o, f => f.Size = (decimal)op.FontSizePt.Value));
                            break;

                        case LayoutActions.SetBold:
                            ModifyObject(doc, op.Target, o => WithFont(o, f => f.Bold = op.Bold.Value));
                            break;

                        case LayoutActions.SetAlignment:
                            ModifyObject(doc, op.Target, o =>
                            {
                                if (o.Format == null)
                                    throw new InvalidOperationException($"Object \"{o.Name}\" has no format object to align.");
                                o.Format.HorizontalAlignment = ParseAlignment(op.Alignment);
                            });
                            break;

                        case LayoutActions.AddText:
                            AddText(doc, op);
                            break;

                        case LayoutActions.AddLine:
                            AddLine(doc, op);
                            break;

                        case LayoutActions.AddBox:
                            AddBox(doc, op);
                            break;

                        case LayoutActions.ResizeSection:
                            ResizeSection(doc, op.Section, op.HeightTwips.Value);
                            break;

                        case LayoutActions.AddField:
                            AddField(doc, op);
                            break;

                        case LayoutActions.RemoveObject:
                            result.RemovedObjects.Add(RemoveObject(doc, op.Target));
                            break;

                        case LayoutActions.SetTextColor:
                            ModifyObject(doc, op.Target, o => WithFontColor(o, fc => fc.Color = ColorRef.FromHex(op.Color!)));
                            break;

                        case LayoutActions.SetFillColor:
                            ModifyObject(doc, op.Target, o =>
                            {
                                if (o is not ISCRBoxObject box)
                                    throw new InvalidOperationException($"Object \"{o.Name}\" ({o.Kind}) is not a Box; has no fill colour.");
                                box.FillColor = ColorRef.FromHex(op.Color!);
                            });
                            break;

                        case LayoutActions.SetLineColor:
                            ModifyObject(doc, op.Target, o =>
                            {
                                switch (o)
                                {
                                    case ISCRLineObject line: line.LineColor = ColorRef.FromHex(op.Color!); break;
                                    case ISCRBoxObject box: box.LineColor = ColorRef.FromHex(op.Color!); break;
                                    default:
                                        throw new InvalidOperationException($"Object \"{o.Name}\" ({o.Kind}) is not a Line or Box; has no line colour.");
                                }
                            });
                            break;

                        case LayoutActions.SetSectionBackground:
                            SetSectionBackground(doc, op.Section, op.Color!);
                            break;

                        case LayoutActions.AddSubreport:
                            AddSubreport(doc, op);
                            break;

                        case LayoutActions.SetSubreportLink:
                            SetSubreportLink(doc, op);
                            break;

                        case LayoutActions.RemoveTable:
                            result.RemovedTables.Add(RemoveTable(doc, op.Target));
                            break;

                        case LayoutActions.AddTable:
                            AddTable(doc, op);
                            break;

                        case LayoutActions.SetTableLocation:
                            SetTableLocation(doc, op);
                            break;

                        case LayoutActions.SetSectionBreak:
                            ModifySectionFormat(doc, op.Section, LayoutActions.SetSectionBreak, f =>
                            {
                                if (op.NewPageBefore.HasValue) f.EnableNewPageBefore = op.NewPageBefore.Value;
                                if (op.NewPageAfter.HasValue) f.EnableNewPageAfter = op.NewPageAfter.Value;
                            });
                            break;

                        case LayoutActions.AddSpecialField:
                            AddSpecialField(doc, op);
                            break;

                        case LayoutActions.SetNumberFormat:
                            ModifyObject(doc, op.Target, o => SetNumberFormat(o, op));
                            break;

                        case LayoutActions.SetCanGrow:
                            ModifyObject(doc, op.Target, o =>
                                RequireObjectFormat(o, LayoutActions.SetCanGrow).EnableCanGrow = op.CanGrow.Value);
                            break;

                        case LayoutActions.SetSuppress:
                            // One operation, two hosts: EnableSuppress lives on ISCRSectionFormat
                            // AND ISCRObjectFormat. The validator has already guaranteed exactly
                            // one of section/target is present, so this branch cannot be ambiguous.
                            if (!string.IsNullOrWhiteSpace(op.Section))
                                ModifySectionFormat(doc, op.Section, LayoutActions.SetSuppress, f =>
                                {
                                    f.EnableSuppress = op.Suppress.Value;
                                    if (op.SuppressIfBlank.HasValue) f.EnableSuppressIfBlank = op.SuppressIfBlank.Value;
                                });
                            else
                                ModifyObject(doc, op.Target, o =>
                                    RequireObjectFormat(o, LayoutActions.SetSuppress).EnableSuppress = op.Suppress.Value);
                            break;

                        case LayoutActions.SetBorder:
                            ModifyObject(doc, op.Target, o => SetBorder(o, op));
                            break;

                        case LayoutActions.AddGroup:
                            AddGroup(doc, op);
                            break;

                        case LayoutActions.AddSort:
                            AddSort(doc, op);
                            break;

                        default:
                            throw new InvalidOperationException($"Unhandled action \"{op.Action}\" reached the applier; the validator should have rejected it.");
                    }
                }
                catch
                {
                    // Operations 0..applied-1 are already on the live document and cannot be rolled
                    // back. Mark the session so SaveAs refuses to persist a half-applied document.
                    session.MarkFaulted();
                    throw;
                }

                result.OperationsApplied++;
            }

            return result;
        }

        // --- object mutation -------------------------------------------------

        /// <summary>
        /// RAS objects cannot be mutated in place. Clone, mutate the clone, then Modify(old, new).
        /// </summary>
        private static void ModifyObject(
            ISCDReportClientDocument doc,
            string objectName,
            Action<ISCRReportObject> mutate)
        {
            var existing = FindObject(doc, objectName);
            var clone = (ISCRReportObject)existing.Clone(true);
            mutate(clone);
            doc.ReportDefController.ReportObjectController.Modify(existing, clone);
        }

        private static ISCRReportObject FindObject(
            ISCDReportClientDocument doc,
            string objectName)
        {
            var all = doc.ReportDefController.ReportObjectController.GetAllReportObjects();
            for (var i = 0; i < all.Count; i++)
            {
                var ro = (ISCRReportObject)all[i];
                if (string.Equals(ro.Name, objectName, StringComparison.OrdinalIgnoreCase)) return ro;
            }
            throw new InvalidOperationException($"Object \"{objectName}\" was not found; the validator should have rejected it.");
        }

        /// <summary>
        /// Applies a mutation to whichever object kinds carry a font.
        /// ISCRFont is a mutable RAS object (Name/Size(decimal)/Bold/Italic/Underline/...), NOT
        /// System.Drawing.Font, so we set properties directly on it rather than constructing a
        /// replacement. See task-6-supplement.md C1.
        /// </summary>
        private static void WithFont(ISCRReportObject obj, Action<ISCRFont> mutate)
        {
            ISCRFontColor fontColor;
            switch (obj)
            {
                case ISCRFieldObject field: fontColor = field.FontColor; break;
                case ISCRTextObject text: fontColor = text.FontColor; break;
                default:
                    throw new InvalidOperationException($"Object \"{obj.Name}\" ({obj.Kind}) has no font to change.");
            }

            if (fontColor?.Font == null)
                throw new InvalidOperationException($"Object \"{obj.Name}\" has no font object.");

            mutate(fontColor.Font);
        }

        /// <summary>
        /// Same object-kind resolution as <see cref="WithFont"/>, but hands the mutation the
        /// ISCRFontColor itself rather than its nested ISCRFont -- setTextColor writes
        /// FontColor.Color, a property WithFont's signature has no way to reach.
        /// </summary>
        private static void WithFontColor(ISCRReportObject obj, Action<ISCRFontColor> mutate)
        {
            ISCRFontColor fontColor;
            switch (obj)
            {
                case ISCRFieldObject field: fontColor = field.FontColor; break;
                case ISCRTextObject text: fontColor = text.FontColor; break;
                default:
                    throw new InvalidOperationException($"Object \"{obj.Name}\" ({obj.Kind}) has no text colour to change.");
            }

            if (fontColor == null)
                throw new InvalidOperationException($"Object \"{obj.Name}\" has no font colour object.");

            mutate(fontColor);
        }

        /// <summary>
        /// ISCRLineObject and ISCRBoxObject carry Right/Bottom endpoint geometry in addition to
        /// Left/Top/Width/Height. Move and Resize must keep them in sync or the object renders at
        /// its old position/size. See task-6-supplement.md C3.
        /// </summary>
        private static void SyncEndpoints(ISCRReportObject obj)
        {
            switch (obj)
            {
                case ISCRLineObject line:
                    line.Right = line.Left + line.Width;
                    line.Bottom = line.Top + line.Height;
                    break;
                case ISCRBoxObject box:
                    box.Right = box.Left + box.Width;
                    box.Bottom = box.Top + box.Height;
                    break;
            }
        }

        /// <summary>
        /// Deletes a report object outright. Unlike Move/Resize/SetFont (clone-mutate-Modify),
        /// there is no clone here -- ISCRReportObjectController.Remove takes the live object
        /// directly (verified by reflection against the installed 11.5 assemblies). Any object
        /// kind may be removed, including Field/Subreport/Chart/Crosstab; removing a Field
        /// deletes data from the generated report, which is accepted by design (the source .rpt
        /// is never touched). Wrapped the same way AddReportObject/AddField wrap Add: a
        /// validator-legal removeObject can still be rejected by RAS itself, and an unwrapped
        /// COMException would reach the caller with no indication of which object or action
        /// caused it. Returns the object's canonical Name (not the caller-supplied objectName,
        /// which FindObject matches case-insensitively) so RemovedObjects reports the report's
        /// actual name rather than echoing whatever casing the caller used.
        /// </summary>
        private static string RemoveObject(ISCDReportClientDocument doc, string objectName)
        {
            var existing = FindObject(doc, objectName);
            try
            {
                doc.ReportDefController.ReportObjectController.Remove(existing);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected \"removeObject\" for \"{objectName}\": {ex.Message.Trim()}", ex);
            }

            return existing.Name;
        }

        private static CrAlignmentEnum ParseAlignment(string alignment)
        {
            switch ((alignment ?? "").ToUpperInvariant())
            {
                case "LEFT": return CrAlignmentEnum.crAlignmentLeft;
                case "RIGHT": return CrAlignmentEnum.crAlignmentRight;
                case "CENTRE":
                case "CENTER": return CrAlignmentEnum.crAlignmentHorizontalCenter;
                case "JUSTIFIED": return CrAlignmentEnum.crAlignmentJustified;
                default: throw new InvalidOperationException($"Unsupported alignment \"{alignment}\".");
            }
        }

        // --- object creation -------------------------------------------------

        private static Section FindSection(
            ISCDReportClientDocument doc,
            string sectionName)
        {
            // Sections live on Areas — ISCRReportDefinition has no Sections property.
            var areas = doc.ReportDefController.ReportDefinition.Areas;
            for (var a = 0; a < areas.Count; a++)
            {
                var sections = areas[a].Sections;
                for (var i = 0; i < sections.Count; i++)
                {
                    var s = sections[i];
                    if (string.Equals(s.Name, sectionName, StringComparison.OrdinalIgnoreCase)) return s;
                }
            }
            throw new InvalidOperationException($"Section \"{sectionName}\" was not found; the validator should have rejected it.");
        }

        /// <summary>
        /// Newly constructed RAS objects come back with FontColor == null, unlike objects already present
        /// in an .rpt, which makes them unfontable until one is supplied. Inherit the section's existing
        /// look where possible so an added object matches its neighbours, rather than imposing an arbitrary
        /// default the caller then has to override.
        /// </summary>
        private static FontColorClass DefaultFontColorFor(ISCDReportClientDocument doc, string sectionName)
        {
            var section = FindSection(doc, sectionName);
            var objects = section.ReportObjects;
            for (var i = 0; i < objects.Count; i++)
            {
                ISCRFontColor fontColor;
                switch (objects[i])
                {
                    case ISCRFieldObject field: fontColor = field.FontColor; break;
                    case ISCRTextObject text: fontColor = text.FontColor; break;
                    default: continue;
                }

                if (fontColor?.Font == null) continue;

                var f = fontColor.Font;
                return new FontColorClass
                {
                    Font = new FontClass
                    {
                        Name = f.Name,
                        Size = f.Size,
                        Bold = f.Bold,
                        Italic = f.Italic,
                        Underline = f.Underline,
                        Strikethrough = f.Strikethrough,
                        Weight = f.Weight,
                        Charset = f.Charset
                    }
                };
            }

            return new FontColorClass { Font = new FontClass { Name = "Arial", Size = 10m } };
        }

        private static void AddText(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            // Measured: a text object's rendered/persisted font is actually carried by its
            // ParagraphTextElementClass run, not by TextObjectClass.FontColor. Setting only the
            // outer TextObjectClass.FontColor (as AddField does for FieldObjectClass) is silently
            // discarded by Add() -- the run's own (unset, RAS-default "MS Shell Dlg") font wins --
            // so the paragraph element needs its own FontColor too. A pre-existing Text object in
            // an .rpt always has both set to the same font; this matches that shape instead of
            // relying on undocumented Add()-time behaviour. Two independent calls (rather than
            // sharing one instance between the two objects) so text and element each hold their
            // own FontClass, per DefaultFontColorFor's no-shared-state contract.
            var text = new TextObjectClass
            {
                Name = op.NewName,
                Left = op.LeftTwips.Value,
                Top = op.TopTwips.Value,
                Width = op.WidthTwips.Value,
                Height = op.HeightTwips.Value,
                FontColor = DefaultFontColorFor(doc, op.Section)
            };

            var element = new ParagraphTextElementClass { Text = op.Text, FontColor = DefaultFontColorFor(doc, op.Section) };
            var paragraph = new ParagraphClass();
            paragraph.ParagraphElements.Add(element);
            text.Paragraphs.Add(paragraph);

            AddReportObject(doc, section, text, LayoutActions.AddText, op.NewName);
        }

        private static void AddLine(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            var line = new LineObjectClass
            {
                Name = op.NewName,
                Left = op.LeftTwips.Value,
                Top = op.TopTwips.Value,
                Width = op.WidthTwips.Value,
                Height = op.HeightTwips.Value,
                Right = op.LeftTwips.Value + op.WidthTwips.Value,
                Bottom = op.TopTwips.Value + op.HeightTwips.Value,
                LineThickness = 15,
                LineStyle = CrLineStyleEnum.crLineStyleSingle,
                EndSectionName = section.Name
            };

            AddReportObject(doc, section, line, LayoutActions.AddLine, op.NewName);
        }

        private static void AddBox(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            var box = new BoxObjectClass
            {
                Name = op.NewName,
                Left = op.LeftTwips.Value,
                Top = op.TopTwips.Value,
                Width = op.WidthTwips.Value,
                Height = op.HeightTwips.Value,
                Right = op.LeftTwips.Value + op.WidthTwips.Value,
                Bottom = op.TopTwips.Value + op.HeightTwips.Value,
                LineThickness = 15,
                LineStyle = CrLineStyleEnum.crLineStyleSingle,
                EndSectionName = section.Name
            };

            AddReportObject(doc, section, box, LayoutActions.AddBox, op.NewName);
        }

        /// <summary>
        /// Wraps ReportObjectController.Add for a newly constructed object with the same
        /// COMException -&gt; InvalidOperationException translation AddField already had (round-1
        /// fix report F2). A validator-legal add can still be rejected by RAS itself - the
        /// diagonal-line case is one known trigger the validator now closes, but that is not proof
        /// no others exist - and an unwrapped COMException would reach the caller with no
        /// indication of which operation, object or section caused it.
        /// </summary>
        private static void AddReportObject(
            ISCDReportClientDocument doc,
            Section section,
            ISCRReportObject obj,
            string action,
            string newName)
        {
            try
            {
                doc.ReportDefController.ReportObjectController.Add(obj, section, -1);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected \"{action}\" for \"{newName}\" in section \"{section.Name}\": {ex.Message.Trim()}", ex);
            }
        }

        /// <summary>
        /// Construct an ISCRFieldObject directly and bind it via DataSource, the same
        /// "construct then Add(obj, section, -1)" idiom AddText/AddLine/AddBox use.
        ///
        /// Round-1 fix history (task-6b-findings-round1.md F1/F2/F3), kept here because it is the
        /// reason this method has no fallback:
        /// - F1: a bare DataSource string alone is not enough -- ISCRFieldObject.FieldValueType
        ///   defaults to a value RAS rejects with COMException "The field value type is not
        ///   valid.". The type must be resolved from the matching ISCRDBField up front, and if it
        ///   cannot be resolved that is thrown immediately and precisely, before ever touching
        ///   RAS, rather than surfacing as a confusing downstream failure.
        /// - F2: an AddByName fallback was tried and removed. Reflection confirms
        ///   ISCRReportObjectController's entire surface is Add, Remove, Modify,
        ///   GetAllReportObjects, GetReportObjectsByKind, AddByName, ImportPicture -- there is no
        ///   Rename/SetName method anywhere, so a field AddByName creates can never be retitled to
        ///   op.NewName. AddByName also creates a second (FieldHeading) object RAS positions on
        ///   its own, and there is no way to give it a name safely. It is not a fallback; it is a
        ///   longer, lossier way to fail after already mutating the live document. If Add throws
        ///   here, that COMException is the real, actionable diagnostic and is propagated with
        ///   full context instead.
        /// - F3: the validator matches fieldRef case-insensitively, but the bound DataSource must
        ///   be the report's own canonical FormulaForm, not the caller's string, or a case-variant
        ///   fieldRef can bind to a DataSource RAS accepts but cannot resolve, rendering blank.
        /// </summary>
        private static void AddField(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            var dbField = ResolveDbField(doc, op.FieldRef);
            if (dbField == null)
                throw new InvalidOperationException(
                    $"Could not resolve a field value type for \"{op.FieldRef}\" in section \"{op.Section}\". " +
                    "The field passed validation against the report's available fields but was not found in the " +
                    "live database tables, so it cannot be bound.");

            var field = new FieldObjectClass
            {
                Name = op.NewName,
                DataSource = dbField.FormulaForm,
                FieldValueType = dbField.Type,
                Left = op.LeftTwips.Value,
                Top = op.TopTwips.Value,
                Width = op.WidthTwips.Value,
                Height = op.HeightTwips.Value,
                // Measured (post-round-1, discovered by T4): a freshly constructed FieldObjectClass
                // comes back from Add() with FontColor == null, unlike a field already present in an
                // .rpt. WithFont's ISCRFieldObject arm requires FontColor.Font to be non-null, so any
                // setFont/setFontSize/setBold on a field addField itself just created would otherwise
                // fail with "has no font object." See DefaultFontColorFor: rather than imposing a
                // fixed default, this inherits the look of whatever Text/Field object already sits in
                // the target section, so an added field matches its neighbours instead of standing out
                // and needing an explicit setFont to fix it, falling back to Arial 10pt only when the
                // section has no existing fontable object to copy from.
                FontColor = DefaultFontColorFor(doc, op.Section)
            };

            try
            {
                doc.ReportDefController.ReportObjectController.Add(field, section, -1);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected the field \"{op.FieldRef}\" added to section \"{op.Section}\" " +
                    $"as \"{op.NewName}\": {ex.Message.Trim()}", ex);
            }
        }

        /// <summary>
        /// Same table walk ReportReader.Read uses for AvailableFields, returning the matching
        /// ISCRDBField itself so the caller can read both its value type (F1) and its canonical
        /// FormulaForm (F3) rather than trusting the caller's possibly case-variant string.
        /// </summary>
        private static ISCRDBField ResolveDbField(ISCDReportClientDocument doc, string fieldRef)
        {
            var tables = doc.DatabaseController.Database.Tables;
            for (var t = 0; t < tables.Count; t++)
            {
                var fields = tables[t].DataFields;
                for (var f = 0; f < fields.Count; f++)
                {
                    var dbField = (ISCRDBField)fields[f];
                    if (string.Equals(dbField.FormulaForm, fieldRef, StringComparison.OrdinalIgnoreCase))
                        return dbField;
                }
            }
            return null;
        }

        private static void ResizeSection(
            ISCDReportClientDocument doc,
            string sectionName,
            int heightTwips)
        {
            var section = FindSection(doc, sectionName);
            doc.ReportDefController.ReportSectionController.SetProperty(
                section,
                CrReportSectionPropertyEnum.crReportSectionPropertyHeight,
                heightTwips);
        }

        /// <summary>
        /// Measured (reflection against the installed 11.5 Controllers assembly): unlike
        /// crReportSectionPropertyHeight, CrReportSectionPropertyEnum has no dedicated
        /// BackgroundColor member -- only Name, Format and Height. BackgroundColor lives on
        /// ISCRSectionFormat, which is itself get/set on ISCRSection but (per the same
        /// clone-mutate-commit idiom ModifyObject uses for report objects, and ISCRSectionFormat
        /// exposing its own Clone(bool)) must be cloned, mutated, and pushed back via
        /// SetProperty(section, crReportSectionPropertyFormat, clone) rather than assigned
        /// in place, or the change does not persist to the live document. This is how the AI
        /// reaches a full-width "dark banner band" design without drawing a Box over the section
        /// -- see ReportTools' apply_layout description.
        /// </summary>
        private static void SetSectionBackground(
            ISCDReportClientDocument doc,
            string sectionName,
            string colorHex)
        {
            ModifySectionFormat(doc, sectionName, LayoutActions.SetSectionBackground,
                f => f.BackgroundColor = ColorRef.FromHex(colorHex));
        }

        /// <summary>
        /// The section-format equivalent of <see cref="ModifyObject"/>: clone the section's
        /// ISCRSectionFormat, mutate the clone, and push it back through
        /// SetProperty(crReportSectionPropertyFormat). Assigning to section.Format in place does
        /// NOT persist -- the same reason report objects go through Clone/Modify. Every
        /// section-level format operation (setSectionBackground, setSectionBreak and setSuppress's
        /// section form) shares this one implementation so the idiom cannot drift between them.
        /// </summary>
        private static void ModifySectionFormat(
            ISCDReportClientDocument doc,
            string sectionName,
            string action,
            Action<ISCRSectionFormat> mutate)
        {
            var section = FindSection(doc, sectionName);
            if (section.Format == null)
                throw new InvalidOperationException(
                    $"Section \"{sectionName}\" has no format object, so \"{action}\" cannot be applied to it.");

            var clone = section.Format.Clone(true);
            mutate(clone);

            try
            {
                doc.ReportDefController.ReportSectionController.SetProperty(
                    section,
                    CrReportSectionPropertyEnum.crReportSectionPropertyFormat,
                    clone);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected \"{action}\" for section \"{sectionName}\": {ex.Message.Trim()}", ex);
            }
        }

        /// <summary>
        /// ISCRObjectFormat carries EnableCanGrow and EnableSuppress. Every report object has one,
        /// but the reader already treats a null Format as possible (it guards the alignment read
        /// that way), so this guards rather than dereferencing blind -- an NRE mid-plan would
        /// fault the session and lose the whole plan for no diagnosable reason.
        /// </summary>
        private static ISCRObjectFormat RequireObjectFormat(ISCRReportObject obj, string action)
        {
            if (obj.Format == null)
                throw new InvalidOperationException(
                    $"Object \"{obj.Name}\" ({obj.Kind}) has no format object, so \"{action}\" cannot be applied to it.");
            return obj.Format;
        }

        /// <summary>
        /// Writes the three numeric display properties setNumberFormat exposes. This is the
        /// "goal_id renders as 10,311.00" fix: decimalPlaces 0 plus thousandsSeparator false.
        ///
        /// MEASURED, and writing this the obvious way produces a SILENT NO-OP. Two separate
        /// findings, both established by save-reopen-read rather than by reasoning:
        ///
        /// 1. ISCRCommonFieldFormat.EnableSystemDefault is a GATE over the whole numeric format.
        ///    While it is true -- and it is true on every field of every fixture here -- Crystal
        ///    formats the field from the system/locale defaults and DISCARDS NDecimalPlaces and
        ///    ThousandsSeparator on save. Setting them alone reported ok and changed nothing: the
        ///    reopened report read back exactly the values it had before, with no exception
        ///    anywhere. Turning EnableSystemDefault off is what makes the explicit format take
        ///    effect, so this method always does, for any of the three properties. That is also
        ///    what the Crystal Designer does the moment you set an explicit number format by hand,
        ///    and it is why the values this freezes are the ones the field was already displaying
        ///    -- nothing changes visually except what the caller asked to change.
        ///    (EnableSuppressIfZero is the exception that proves the rule: it persisted even with
        ///    the gate on, which is exactly how a half-working operation hides.)
        ///
        /// 2. NDecimalPlaces alone is not enough to control the displayed precision, because
        ///    RoundingFormat rounds the value independently. Measured mapping: RoundToUnit (11) is
        ///    0 decimals, RoundToTenth (10) is 1, RoundToHundredth (9) is 2 -- i.e. the enum value
        ///    is 11 - decimalPlaces across the 0-10 range the validator allows. Setting
        ///    NDecimalPlaces = 3 while leaving RoundingFormat at RoundToUnit persists both, but
        ///    renders "10311.000" -- three decimal places of a value already rounded to a whole
        ///    number. So rounding is kept in step with the requested precision, as the Designer
        ///    keeps them in step.
        ///
        /// Everything is mutated in place on the clone ModifyObject already deep-copied, the same
        /// shape WithFont uses for ISCRFont and setAlignment uses for ObjectFormat. Assigning a
        /// separately cloned FieldFormat back onto the object clone was also tried, and Modify does
        /// not carry it.
        ///
        /// Each of the three is written only when the caller supplied it, so a plan that sets just
        /// decimalPlaces cannot silently reset the field's existing thousands separator. The
        /// validator guarantees at least one is present, so this can never be a no-op.
        /// </summary>
        private static void SetNumberFormat(ISCRReportObject obj, LayoutOperation op)
        {
            if (obj is not ISCRFieldObject field)
                throw new InvalidOperationException(
                    $"Object \"{obj.Name}\" ({obj.Kind}) is not a Field and has no number format; " +
                    "the validator should have rejected it.");

            if (field.FieldFormat == null)
                throw new InvalidOperationException(
                    $"Field \"{obj.Name}\" has no field format object to set a number format on.");

            var numeric = field.FieldFormat.NumericFormat;
            if (numeric == null)
                throw new InvalidOperationException(
                    $"Field \"{obj.Name}\" has a field format but no numeric format object to write to.");

            // Finding 1: without this the other three writes are discarded on save.
            if (field.FieldFormat.CommonFormat == null)
                throw new InvalidOperationException(
                    $"Field \"{obj.Name}\" has a field format but no common format object, so the " +
                    "explicit number format could not be enabled and would silently not apply.");
            field.FieldFormat.CommonFormat.EnableSystemDefault = false;

            if (op.DecimalPlaces.HasValue)
            {
                numeric.NDecimalPlaces = op.DecimalPlaces.Value;
                // Finding 2: keep rounding in step, or the extra digits are rendered as zeroes.
                numeric.RoundingFormat = RoundingFor(op.DecimalPlaces.Value);
            }
            if (op.ThousandsSeparator.HasValue) numeric.ThousandsSeparator = op.ThousandsSeparator.Value;
            if (op.SuppressIfZero.HasValue) numeric.EnableSuppressIfZero = op.SuppressIfZero.Value;
        }

        /// <summary>
        /// Writes the four border edges and the border colour setBorder exposes.
        ///
        /// A border is the only ruling that survives a growing cell. A Line object has no
        /// can-grow, so a table ruled with Lines loses its grid the moment setCanGrow lets a cell
        /// get taller; ISCRBorder belongs to the object and is drawn around whatever height the
        /// object ends up with.
        ///
        /// Everything is mutated in place on the clone ModifyObject already deep-copied -- the
        /// same shape SetNumberFormat uses for the nested ISCRFieldFormat and WithFont uses for
        /// ISCRFont. See docs/sdk-notes.md for what was measured before settling on it.
        ///
        /// Each side is written only when the caller supplied it, so adding a bottom rule cannot
        /// silently clear an existing left divider. The validator guarantees at least one side or
        /// the colour is present, so this can never be a no-op.
        /// </summary>
        private static void SetBorder(ISCRReportObject obj, LayoutOperation op)
        {
            var border = obj.Border;
            if (border == null)
                throw new InvalidOperationException(
                    $"Object \"{obj.Name}\" ({obj.Kind}) has no border object, so \"setBorder\" " +
                    "cannot be applied to it.");

            if (!string.IsNullOrWhiteSpace(op.Left)) border.LeftLineStyle = ParseLineStyle(op.Left);
            if (!string.IsNullOrWhiteSpace(op.Right)) border.RightLineStyle = ParseLineStyle(op.Right);
            if (!string.IsNullOrWhiteSpace(op.Top)) border.TopLineStyle = ParseLineStyle(op.Top);
            if (!string.IsNullOrWhiteSpace(op.Bottom)) border.BottomLineStyle = ParseLineStyle(op.Bottom);

            // Reuses ColorRef, the single COLORREF converter, exactly as setTextColor/setFillColor
            // do. The validator has already checked the "#RRGGBB" form with the same rule.
            if (op.Color != null) border.BorderColor = ColorRef.FromHex(op.Color);
        }

        /// <summary>
        /// The friendly BorderStyles vocabulary -> CrLineStyleEnum. The validator's allowlist is
        /// the contract, so anything reaching here is one of the five; a sixth would be a
        /// validator bug and is reported as one rather than silently becoming "no line".
        /// </summary>
        private static CrLineStyleEnum ParseLineStyle(string style)
        {
            switch ((style ?? "").Trim().ToLowerInvariant())
            {
                case BorderStyles.None: return CrLineStyleEnum.crLineStyleNoLine;
                case BorderStyles.Single: return CrLineStyleEnum.crLineStyleSingle;
                case BorderStyles.Double: return CrLineStyleEnum.crLineStyleDouble;
                case BorderStyles.Dashed: return CrLineStyleEnum.crLineStyleDashed;
                case BorderStyles.Dotted: return CrLineStyleEnum.crLineStyleDotted;
                default:
                    throw new InvalidOperationException(
                        $"\"{style}\" is not a border style; the validator should have rejected it.");
            }
        }

        /// <summary>
        /// CrRoundingTypeEnum, as a rounding step matching a decimal-place count. Measured against
        /// the installed 11.5 enum: crRoundingTypeRoundToUnit = 11 (0 decimals),
        /// crRoundingTypeRoundToTenth = 10 (1), crRoundingTypeRoundToHundredth = 9 (2), down to
        /// crRoundingTypeRoundToTenBillionth = 1 (10). The validator caps decimalPlaces at 0-10,
        /// which keeps this inside the enum's defined range.
        /// </summary>
        private static CrRoundingTypeEnum RoundingFor(int decimalPlaces) =>
            (CrRoundingTypeEnum)(CrRoundingTypeEnum.crRoundingTypeRoundToUnit - decimalPlaces);

        /// <summary>
        /// Places a Crystal Special Field -- a page number, print date, "Page 1 of N" and so on.
        ///
        /// MEASURED, and it is what makes this method as short as it is: a bare
        /// <c>SpecialFieldClass</c> derives everything else from <c>SpecialType</c> on its own,
        /// with no report open, no COM server and no database contact. Setting SpecialType to
        /// crSpecialFieldTypePageNumber immediately yields FormulaForm "PageNumber", Name
        /// "PageNumber", Type crFieldValueTypeInt32uField and Length 4. So there is no lookup table
        /// to maintain and no chance of the value type disagreeing with the data source.
        ///
        /// That value type matters. AddField had to learn the hard way (sdk-notes.md note 5) that a
        /// freshly constructed FieldObjectClass leaves FieldValueType at its CLR default and Add
        /// rejects it outright with COMException "The field value type is not valid." A special
        /// field is the same kind of object and would hit the same wall, which is why Type is taken
        /// from the SpecialField rather than left unset or guessed.
        ///
        /// Confirmed against a real report rather than inferred: SampleReport.rpt's own PrintDate1
        /// and PageNumber1 objects are ordinary crReportObjectKindField objects whose DataSource is
        /// the bare, UNBRACED string "PrintDate" / "PageNumber" -- byte for byte what
        /// SpecialFieldClass.FormulaForm returns. A database field's DataSource is braced
        /// ("{Command.CardCode}"); a special field's is not.
        ///
        /// FontColor is supplied for the same reason AddField supplies it: a newly constructed
        /// FieldObjectClass has none, and a setFontSize/setBold on the field this operation just
        /// created would otherwise fail with "has no font object."
        /// </summary>
        private static void AddSpecialField(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            var special = new SpecialFieldClass { SpecialType = ParseSpecialType(op.SpecialType) };

            var field = new FieldObjectClass
            {
                Name = op.NewName,
                DataSource = special.FormulaForm,
                FieldValueType = special.Type,
                Left = op.LeftTwips.Value,
                Top = op.TopTwips.Value,
                Width = op.WidthTwips.Value,
                Height = op.HeightTwips.Value,
                FontColor = DefaultFontColorFor(doc, op.Section)
            };

            AddReportObject(doc, section, field, LayoutActions.AddSpecialField, op.NewName);
        }

        /// <summary>
        /// Maps the friendly specialType vocabulary onto CrSpecialFieldTypeEnum. The validator's
        /// allowlist (SpecialFieldTypes.All) is the contract and is checked before anything reaches
        /// here, so an unrecognised value at this point is a bug, not caller input -- reported the
        /// same way ParseAlignment reports one.
        /// </summary>
        private static CrSpecialFieldTypeEnum ParseSpecialType(string specialType)
        {
            switch ((specialType ?? "").ToUpperInvariant())
            {
                case "PAGENUMBER": return CrSpecialFieldTypeEnum.crSpecialFieldTypePageNumber;
                case "PAGENOFM": return CrSpecialFieldTypeEnum.crSpecialFieldTypePageNOfM;
                case "TOTALPAGECOUNT": return CrSpecialFieldTypeEnum.crSpecialFieldTypeTotalPageCount;
                case "PRINTDATE": return CrSpecialFieldTypeEnum.crSpecialFieldTypePrintDate;
                case "PRINTTIME": return CrSpecialFieldTypeEnum.crSpecialFieldTypePrintTime;
                case "REPORTTITLE": return CrSpecialFieldTypeEnum.crSpecialFieldTypeReportTitle;
                case "RECORDNUMBER": return CrSpecialFieldTypeEnum.crSpecialFieldTypeRecordNumber;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported specialType \"{specialType}\"; the validator should have rejected it.");
            }
        }

        /// <summary>
        /// Adds a group on an existing field, which is the ONLY way this tool can create a section:
        /// ISCRReportDefController's whole Add/Modify/Remove surface is for chart objects, so a
        /// report's band structure is otherwise fixed (measured, docs/sdk-notes.md). Crystal creates
        /// a Group Header and a Group Footer area for the new group and names both itself.
        ///
        /// The field is resolved explicitly through FindFieldByFormulaForm rather than going through
        /// GroupController.AddByName, so an unknown fieldRef fails here with a message naming it
        /// instead of inside COM. See the report for the AddByName comparison.
        /// </summary>
        private static void AddGroup(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var field = ResolveGroupableField(doc, op.FieldRef, LayoutActions.AddGroup);

            var group = new GroupClass { ConditionField = field };

            // -1 appends. GroupController.Add's index is 0-based and outermost-first, matching
            // DataDefinition.Groups (measured: inserting at 0 puts the new group's header area
            // ABOVE the existing one and its footer area BELOW, i.e. it becomes the outer group).
            var index = op.GroupIndex ?? -1;

            try
            {
                doc.DataDefController.GroupController.Add(index, group);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected \"addGroup\" on \"{op.FieldRef}\": {ex.Message.Trim()}", ex);
            }

            // Measured: ISCRGroupOptions carries NO direction -- a group's order genuinely is a
            // sort, and GroupController.Add creates one on the group's field for us (ascending),
            // or adopts an existing sort on that field and keeps its direction. So a direction is
            // applied by modifying that sort, and is only touched when the caller asked for one:
            // an addGroup with no direction must not silently flip an order the report already had.
            if (!string.IsNullOrWhiteSpace(op.Direction))
                SetSortDirectionFor(doc, op.FieldRef, ParseDirection(op.Direction), LayoutActions.AddGroup);
        }

        /// <summary>
        /// Adds a record sort. Crystal refuses a second sort on a field that already carries one
        /// ("The sorting already exists" -- measured), and every group carries a sort on its own
        /// field, so the validator rejects both cases before anything is written; the COMException
        /// wrapper here is for whatever it did not foresee.
        /// </summary>
        private static void AddSort(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var field = ResolveGroupableField(doc, op.FieldRef, LayoutActions.AddSort);

            var sort = new SortClass
            {
                SortField = field,
                Direction = ParseDirection(op.Direction)
            };

            try
            {
                doc.DataDefController.SortController.Add(op.SortIndex ?? -1, sort);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected \"addSort\" on \"{op.FieldRef}\": {ex.Message.Trim()}", ex);
            }
        }

        /// <summary>
        /// Resolves a fieldRef to the report's own ISCRField. Deliberately does NOT walk
        /// DatabaseController.Database.Tables the way AddField's ResolveDbField does: that walk is
        /// the one Crystal call measured to BLOCK rather than fail when the database is unreachable
        /// (post-merge finding PM1), and grouping needs no value type, only the field itself.
        /// </summary>
        private static ISCRField ResolveGroupableField(
            ISCDReportClientDocument doc,
            string fieldRef,
            string action)
        {
            object raw;
            try
            {
                raw = doc.DataDefController.FindFieldByFormulaForm(fieldRef);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal could not resolve the field \"{fieldRef}\" for \"{action}\": {ex.Message.Trim()}", ex);
            }

            if (raw == null)
                throw new InvalidOperationException(
                    $"\"{action}\" could not resolve the field \"{fieldRef}\" in this report. Use one of the " +
                    "formulaForm values read_report lists under availableFields.");

            return (ISCRField)raw;
        }

        /// <summary>
        /// Points the sort that exists for <paramref name="fieldRef"/> at a direction.
        /// SortController.ModifySortDirection takes the live sort object, so the sort is located by
        /// walking DataDefinition.Sorts and matching on FormulaForm rather than trusting
        /// FindSort to match a field instance we constructed elsewhere.
        /// </summary>
        private static void SetSortDirectionFor(
            ISCDReportClientDocument doc,
            string fieldRef,
            CrSortDirectionEnum direction,
            string action)
        {
            var sorts = doc.DataDefController.DataDefinition.Sorts;
            for (var i = 0; i < sorts.Count; i++)
            {
                var sort = (ISCRSort)sorts[i];
                if (sort.SortField == null ||
                    !string.Equals(sort.SortField.FormulaForm, fieldRef, StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    doc.DataDefController.SortController.ModifySortDirection(sort, direction);
                }
                catch (COMException ex)
                {
                    throw new InvalidOperationException(
                        $"Crystal rejected the sort direction for \"{action}\" on \"{fieldRef}\": {ex.Message.Trim()}", ex);
                }
                return;
            }

            throw new InvalidOperationException(
                $"\"{action}\" added a group on \"{fieldRef}\" but Crystal created no sort for it, so the " +
                "requested direction could not be applied.");
        }

        /// <summary>
        /// The validator's allowlist (SortDirections.All) is the contract and is checked before
        /// anything reaches here, so an unrecognised value at this point is a bug, not caller
        /// input -- reported the same way ParseAlignment and ParseSpecialType report one. The four
        /// TopN variants are deliberately unreachable: none can be expressed without an N.
        /// </summary>
        private static CrSortDirectionEnum ParseDirection(string direction)
        {
            switch ((direction ?? "").ToUpperInvariant())
            {
                case "ASCENDING": return CrSortDirectionEnum.crSortDirectionAscendingOrder;
                case "DESCENDING": return CrSortDirectionEnum.crSortDirectionDescendingOrder;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported direction \"{direction}\"; the validator should have rejected it.");
            }
        }

        /// <summary>
        /// Imports an existing .rpt as a sub-report object. The validator checks reportPath is
        /// non-empty and ends in ".rpt" but deliberately does no file I/O (it must stay pure), so
        /// the existence check -- with a message naming the actual path -- lives here.
        /// </summary>
        private static void AddSubreport(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            if (!File.Exists(op.ReportPath))
                throw new InvalidOperationException(
                    $"Crystal rejected \"addSubreport\" for \"{op.NewName}\": sub-report file not found: \"{op.ReportPath}\".");

            try
            {
                doc.SubreportController.ImportSubreportEx(
                    op.NewName, op.ReportPath, section,
                    op.LeftTwips.Value, op.TopTwips.Value, op.WidthTwips.Value, op.HeightTwips.Value);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected \"addSubreport\" for \"{op.NewName}\" in section \"{section.Name}\": {ex.Message.Trim()}", ex);
            }
        }

        /// <summary>
        /// Sets one main-report-to-subreport field link. SubreportController.SetSubreportLinks
        /// replaces the ENTIRE link collection for the named sub-report, not just adds to it -- a
        /// Goal Alignment report needs two links (evaluation cycle and employee number), added by
        /// two separate setSubreportLink calls, and a naive "build one link and set it" would let
        /// the second call silently discard the first. So this fetches the sub-report's current
        /// links first, appends the new one, and only then calls SetSubreportLinks with the whole
        /// (now-longer) collection. GetSubreportLinks is wrapped defensively: a sub-report with no
        /// links yet may throw (or return null) rather than return an empty collection, in which
        /// case this starts from a fresh SubreportLinksClass instead of failing the whole
        /// operation. F3: that tolerance is NARROW by construction -- starting fresh is only
        /// correct when there was genuinely nothing to preserve, so whenever the fallback is used
        /// the links are re-read after the set and required to number exactly one. Anything else
        /// (more links present, or a read-back that fails) means the fallback may have replaced
        /// real links with a single new one, and that must surface as a failed operation rather
        /// than as silent data loss reported ok.
        /// </summary>
        private static void SetSubreportLink(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            // Measured (live round-trip against the installed 11.5 assemblies), NOT staleness:
            // ImportSubreportEx's "Name" argument does not become the placed report OBJECT's own
            // Name -- Crystal auto-numbers that container object itself ("Subreport1",
            // "Subreport2", ...; confirmed both immediately after Add and after save/reopen). The
            // "Name" argument instead becomes ISCRSubreportObject.SubreportName, a second,
            // separate identifier that SubreportController.GetSubreportLinks/SetSubreportLinks are
            // keyed by (not object-identity based like ReportObjectController.Modify). So "target"
            // for setSubreportLink is addSubreport's newName -- the SubreportName -- and must be
            // used as-is here, NOT resolved through FindObject(doc, op.Target), which looks up by
            // the (different, auto-assigned) container object Name and would throw "was not
            // found" for a sub-report added earlier in this same plan. ReportReader now REPORTS
            // that SubreportName on ObjectInfo, so the agent can also target a sub-report embedded
            // by an earlier plan or already present in the source .rpt -- not only one added in
            // this same plan.
            var subreportName = op.Target;

            // GetSubreportLinks/SetSubreportLinks are COM-typed to the marker interface
            // SubreportLinks, but Add/Count/Item live on the "ISCR" dual interface the same
            // concrete SubreportLinksClass also implements -- verified by reflecting the
            // installed 11.5 ReportDefModel assembly. Cast to reach them.
            // F3 guard: resolve the name against the controller's own registry FIRST. Passing a
            // name SubreportController does not know (the placed object's auto-numbered Name, say)
            // otherwise reaches SetSubreportLinks and fails with COM "This value is write-only."
            // -- measured, and useless to the agent. This also canonicalises the casing, since the
            // validator matches names case-insensitively but the COM API does not.
            subreportName = ResolveSubreportName(doc, subreportName, op.Target);

            var readFailed = false;
            ISCRSubreportLinks links = null;
            try
            {
                links = (ISCRSubreportLinks)doc.SubreportController.GetSubreportLinks(subreportName);
            }
            catch (COMException)
            {
                readFailed = true;
            }

            // F3: null is a real possibility here, not just a throw -- and links.Add below would
            // then raise a bare NullReferenceException the COMException catch above cannot see.
            // Fold it into the same "could not read" path so it gets the same proof obligation.
            if (links == null)
            {
                readFailed = true;
                links = new SubreportLinksClass();
            }

            links.Add(new SubreportLinkClass
            {
                MainReportFieldName = op.MainReportField,
                SubreportFieldName = op.SubreportField,
                LinkedParameterName = op.LinkedParameter
            });

            try
            {
                doc.SubreportController.SetSubreportLinks(subreportName, (SubreportLinks)links);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected \"setSubreportLink\" for \"{op.Target}\": {ex.Message.Trim()}", ex);
            }

            // F3: SetSubreportLinks REPLACES the whole collection. If the read above failed and we
            // started from an empty collection, any links the sub-report already had have just
            // been thrown away -- silently, and reported as success. Tolerating that throw is only
            // safe when the sub-report genuinely had no links, so prove it: re-read and require
            // exactly the one link we appended. A count of more than one means the fallback was
            // wrong and existing links were discarded; a re-read that itself fails means we cannot
            // tell, which is not good enough to report ok.
            if (!readFailed) return;

            int afterCount;
            try
            {
                var after = (ISCRSubreportLinks)doc.SubreportController.GetSubreportLinks(subreportName);
                if (after == null) throw new InvalidOperationException("GetSubreportLinks returned null.");
                afterCount = after.Count;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"\"setSubreportLink\" for \"{op.Target}\": this sub-report's existing links could not be read " +
                    "before the link was added, and cannot be read back afterwards to confirm none were discarded " +
                    $"(SetSubreportLinks replaces the whole collection): {ex.Message.Trim()}", ex);
            }

            if (afterCount != 1)
                throw new InvalidOperationException(
                    $"\"setSubreportLink\" for \"{op.Target}\": this sub-report's existing links could not be read " +
                    $"before the link was added, so it was set from an empty collection, but {afterCount} links are " +
                    "present afterwards -- pre-existing links may have been discarded. The report was not saved.");
        }

        // --- data source tables ----------------------------------------------
        //
        // The boundary these three operations sit on: report METADATA may be written, the database
        // is only ever READ. Nothing here executes SQL or changes a connection's server, database
        // or user. addTable CLONES the ConnectionInfo of a table already in the report and
        // setTableLocation keeps the table's own ConnectionInfo untouched, so no server, database
        // or user name is ever expressed in a plan -- plans are JSON files on disk and get quoted
        // in documentation.
        //
        // The one exception, and its rules: addTable and setTableLocation make Crystal contact the
        // database server, and a report persists its connection's UserName but never its Password
        // (measured; docs/sdk-notes.md). Those two operations therefore set Password on the CLONED
        // ConnectionInfo immediately before the call, from the VIBEY_DB_PASSWORD environment
        // variable (see DatabasePassword). The password is still NOT part of the contract: it is
        // not a LayoutOperation field, not a LayoutPlan field, and nothing in
        // VibeyReports.Contracts knows it exists. It is never read back out of a ConnectionInfo,
        // and every message these two methods can throw is scrubbed of it first, because Crystal's
        // own COM text is composed by the database driver and travels into WorkerResponse.Error.
        // removeTable neither reads the variable nor touches ConnectionInfo at all -- it is fully
        // offline and stays that way.

        /// <summary>
        /// Finds a data-source table by alias, matched case-insensitively the way the validator
        /// matches it. Returns null when absent; every caller turns that into a message listing
        /// the aliases that DO exist, because the validator's own existence check is deliberately
        /// skipped when ReportSchema.AvailableFields is empty (no database connection) and absence
        /// cannot be proven from an empty list.
        /// </summary>
        private static ISCRTable FindTable(ISCDReportClientDocument doc, string alias)
        {
            var tables = doc.DatabaseController.Database.Tables;
            for (var i = 0; i < tables.Count; i++)
            {
                var t = (ISCRTable)tables[i];
                if (string.Equals(t.Alias, alias, StringComparison.OrdinalIgnoreCase)) return t;
            }
            return null;
        }

        private static string KnownAliases(ISCDReportClientDocument doc)
        {
            var tables = doc.DatabaseController.Database.Tables;
            var names = new List<string>();
            for (var i = 0; i < tables.Count; i++) names.Add(((ISCRTable)tables[i]).Alias);
            return names.Count == 0 ? "(this report has no data-source tables)" : string.Join(", ", names);
        }

        private static ISCRTable RequireTable(ISCDReportClientDocument doc, string alias, string action)
        {
            var table = FindTable(doc, alias);
            if (table == null)
                throw new InvalidOperationException(
                    $"\"{action}\": no table with the alias \"{alias}\" is in this report's data source. " +
                    $"Aliases present: {KnownAliases(doc)}.");
            return table;
        }

        /// <summary>
        /// Deletes a table from the report's data source. Returns the table's canonical alias (not
        /// the caller's possibly case-variant string) so RemovedTables reports the report's own
        /// spelling.
        ///
        /// Measured against the installed 11.5 assemblies, because two plausible assumptions about
        /// this call turned out to be wrong:
        ///
        /// - RemoveTable does NOT refuse a table that report objects are still bound to.
        ///   PMSV10_GoalAlignCascade.rpt has five Field objects bound to
        ///   "sp_perf_goal_align_detail;1" and the table was removed cleanly anyway, leaving those
        ///   fields unresolvable. LayoutPlanValidator is the only thing standing between a model
        ///   and that outcome.
        /// - A table that participates in a TableLink does NOT need the link removed first.
        ///   Measured directly: AddTableLink(cascade -&gt; detail) then RemoveTable("...detail;1")
        ///   succeeded, and Database.TableLinks went 1 -&gt; 0 on its own. So there is deliberately
        ///   no RemoveTableLink pre-step here. The converse was measured too: on
        ///   tests/fixtures/Documents.rpt, RemoveTable("Lines") throws "Unable to remove table
        ///   'Lines'." and removing its one TableLink first does NOT help -- and "CompanyInfo",
        ///   which has no links at all, is refused identically. Crystal's refusal is driven by
        ///   other in-report references (that report binds its fields through formulas), not by
        ///   links, so a speculative link-removal step would only destroy real links without ever
        ///   rescuing the removal.
        ///
        /// Needs no database connection: this rewrites the report's own binding metadata only.
        /// </summary>
        private static string RemoveTable(ISCDReportClientDocument doc, string alias)
        {
            var table = RequireTable(doc, alias, LayoutActions.RemoveTable);
            var canonical = table.Alias;

            try
            {
                doc.DatabaseController.RemoveTable(canonical);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected \"removeTable\" for \"{alias}\": {ex.Message.Trim()} " +
                    "Crystal refuses to remove a table that something else in the report still " +
                    "refers to -- most often a formula, a record-selection formula, a group or a " +
                    "sort. Those are outside what this tool can edit, so such a table has to be " +
                    "detached in the Crystal Designer.", ex);
            }

            return canonical;
        }

        /// <summary>
        /// Adds a table or stored procedure to the report's data source, cloning the connection of
        /// a table already present so no server, database or user name is ever expressed in a plan.
        ///
        /// MEASURED, and it is what shapes this method: AddTable always contacts the database
        /// server. Against out/reports/PMSV10_GoalAlignCascade.rpt it threw COMException "Logon
        /// failed. Unable to connect: incorrect log on parameters." on every attempt -- because
        /// Crystal persists a connection's UserName (there, "sgdev01db01_devlogin", i.e. SQL Server
        /// authentication, not integrated security) but never its Password. That was measured
        /// across every variation that could plausibly matter (ProcedureClass and TableClass; a
        /// three-part qualified name, a bare one, and the source table's verbatim; parameters
        /// cloned from the source and left empty; and a full src.Clone(true) carrying the source's
        /// DataFields), and the failure was identical every time -- the call never gets far enough
        /// for the table's shape to matter.
        ///
        /// So the missing password is supplied here, from the VIBEY_DB_PASSWORD environment
        /// variable, set on the CLONE immediately before the call. It is read at the point of use
        /// and never stored, never returned, and never allowed into a message (see
        /// DatabasePassword). A report whose saved connection logs on unattended (integrated
        /// security) still works with the variable unset -- but the driving reports here do not,
        /// which is why absence is an error rather than a silent attempt.
        /// </summary>
        private static void AddTable(ISCDReportClientDocument doc, LayoutOperation op)
        {
            var source = RequireTable(doc, op.Target, LayoutActions.AddTable);

            if (FindTable(doc, op.NewName) != null)
                throw new InvalidOperationException(
                    $"\"addTable\": a table with the alias \"{op.NewName}\" is already in this report's data source.");

            if (source.ConnectionInfo == null)
                throw new InvalidOperationException(
                    $"\"addTable\": the table \"{op.Target}\" has no saved connection to clone, so there is " +
                    "nothing to attach the new table to.");

            // Read at the point of use, not at process start: nothing here assumes the worker is
            // short-lived. Throws a message naming VIBEY_DB_PASSWORD when it is unset, BEFORE any
            // COM call, so "variable not set" can never be mistaken for a server rejection.
            var password = DatabasePassword.Require(LayoutActions.AddTable);

            // ProcedureClass when the source is a stored procedure, TableClass otherwise: mirror
            // the shape the report already uses rather than picking one. Every table in the
            // driving reports is a Procedure (ClassName "CrystalReports.Procedure").
            ISCRTable table = source is ISCRProcedure ? (ISCRTable)new ProcedureClass() : new TableClass();

            // The ONLY thing taken from the source table. Clone(true) is a deep copy, so the new
            // table does not alias the existing one's connection object -- which matters twice
            // over now: the password below is set on the CLONE, so the table already in the report
            // never has one written into it.
            var connection = (ConnectionInfo)source.ConnectionInfo.Clone(true);
            connection.Password = password;
            table.ConnectionInfo = connection;
            table.Name = op.TableName;
            table.Alias = op.NewName;
            table.QualifiedName = QualifiedNameFor(source, op.TableName);

            // Parameters are left unset. A stored procedure's declared parameters come from the
            // server, and nothing measurable says they must be supplied up front -- the logon
            // failure above happens before any parameter handling could be reached, so this is
            // recorded as unmeasured rather than as established fact.

            try
            {
                // RelatedTableLinks is null: this adds an UNLINKED table. Measured that Crystal
                // maintains TableLinks itself when a table is removed, and nothing suggests a link
                // is required to add one.
                doc.DatabaseController.AddTable(table, null);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected \"addTable\" for \"{op.NewName}\" ({op.TableName}): " +
                    $"{DatabasePassword.Scrub(ex.Message, password).Trim()} " +
                    "Adding a table makes Crystal connect to the database to verify it. The connection " +
                    $"cloned from \"{op.Target}\" carries the report's saved server, database and user name " +
                    $"({DescribeUser(source)}), and the password was supplied from the {DatabasePassword.EnvVarName} " +
                    "environment variable, which IS set -- so the server was reached and refused the logon, " +
                    "or refused the object. Check that the variable holds the current password for that user " +
                    "and that the named object exists and is visible to it. To combine a second data source " +
                    "with no logon at all, embed it as a sub-report (addSubreport), which brings its own " +
                    "connection with it.",
                    ScrubbedCopy(ex, password));
            }
        }

        /// <summary>
        /// Repoints an existing table at a different database object on the SAME connection: the
        /// clone keeps the original ConnectionInfo untouched, so server, database and credentials
        /// cannot change.
        ///
        /// Carries the same measured logon requirement as AddTable -- SetTableLocation against
        /// out/reports/PMSV10_GoalAlignCascade.rpt throws COMException "Logon failed." for the same
        /// reason, and takes its password from VIBEY_DB_PASSWORD the same way. See
        /// docs/sdk-notes.md.
        /// </summary>
        private static void SetTableLocation(ISCDReportClientDocument doc, LayoutOperation op)
        {
            var current = RequireTable(doc, op.Target, LayoutActions.SetTableLocation);

            if (current.ConnectionInfo == null)
                throw new InvalidOperationException(
                    $"\"setTableLocation\": the table \"{op.Target}\" has no saved connection, so there is " +
                    "nothing to repoint it on.");

            // Read at the point of use, before any COM call, exactly as AddTable does.
            var password = DatabasePassword.Require(LayoutActions.SetTableLocation);

            // Clone(true) copies the ConnectionInfo along with everything else, so the repointed
            // table keeps exactly the connection it had. Only Name/QualifiedName change; Alias is
            // deliberately left alone, since every report object binds through the alias.
            var replacement = current.Clone(true);
            replacement.Name = op.TableName;
            replacement.QualifiedName = QualifiedNameFor(current, op.TableName);

            // The password goes on a FRESH clone assigned to the replacement, so the table
            // currently in the document is never mutated -- if this call throws, nothing in the
            // live document has been given a password, and the session is faulted so nothing is
            // saved either. Server, database and user name are unchanged: only the missing
            // password is added.
            var connection = (ConnectionInfo)current.ConnectionInfo.Clone(true);
            connection.Password = password;
            replacement.ConnectionInfo = connection;

            try
            {
                doc.DatabaseController.SetTableLocation(current, replacement);
            }
            catch (COMException ex)
            {
                throw new InvalidOperationException(
                    $"Crystal rejected \"setTableLocation\" for \"{op.Target}\" -> \"{op.TableName}\": " +
                    $"{DatabasePassword.Scrub(ex.Message, password).Trim()} Repointing a table makes Crystal " +
                    "connect to the database to verify the new object. The report's saved connection " +
                    $"({DescribeUser(current)}) was used with the password from the {DatabasePassword.EnvVarName} " +
                    "environment variable, which IS set -- so the server was reached and refused the logon, or " +
                    "refused the object. Check that the variable holds the current password for that user and " +
                    "that the named object exists and is visible to it.",
                    ScrubbedCopy(ex, password));
            }
        }

        /// <summary>
        /// Names the database user a connection logs on as, for diagnosis. A user name is not a
        /// credential (it is already persisted in the .rpt and printed in this project's docs) --
        /// the password is, and it is never read back out of a ConnectionInfo anywhere.
        /// </summary>
        private static string DescribeUser(ISCRTable table)
        {
            string user = null;
            try { user = table.ConnectionInfo?.UserName; } catch (COMException) { /* diagnosis only */ }
            return string.IsNullOrEmpty(user)
                ? "no user name saved, so the connection expects integrated security"
                : "user \"" + user + "\"";
        }

        /// <summary>
        /// A same-type, same-HRESULT copy of a COM exception with the password removed from its
        /// message. The ORIGINAL is deliberately not kept as the inner exception: Program.cs writes
        /// ex.ToString() to stderr, and ToString() walks the inner chain, so an unscrubbed inner
        /// would put the credential in the worker's log output even though the response itself was
        /// clean. The cost is the original's stack trace, which for a COM logon rejection carries
        /// no information the message does not.
        /// </summary>
        private static COMException ScrubbedCopy(COMException ex, string password)
        {
            return new COMException(DatabasePassword.Scrub(ex.Message, password), ex.ErrorCode);
        }

        /// <summary>
        /// Builds the new table's QualifiedName by keeping the source table's catalog/schema prefix
        /// and substituting the new object name. Measured shape on the driving reports:
        /// Name and Alias are both "sp_perf_goal_align_cascade;1" and QualifiedName is
        /// "hrmmain_philippinesdev.PeoplesHR.sp_perf_goal_align_cascade;1" -- i.e. the qualified
        /// name ENDS WITH the name, ";1" overload suffix included. Replacing that suffix (rather
        /// than splitting on the last '.') is what keeps the ";1" intact.
        /// </summary>
        private static string QualifiedNameFor(ISCRTable source, string tableName)
        {
            var qualified = source.QualifiedName ?? "";
            var name = source.Name ?? "";

            if (name.Length > 0 && qualified.EndsWith(name, StringComparison.OrdinalIgnoreCase))
                return qualified.Substring(0, qualified.Length - name.Length) + tableName;

            var dot = qualified.LastIndexOf('.');
            return dot >= 0 ? qualified.Substring(0, dot + 1) + tableName : tableName;
        }

        /// <summary>
        /// Maps setSubreportLink's target onto the exact name SubreportController is keyed by,
        /// using the (previously unused) GetSubreportNames registry. The validator already checks
        /// the name against the schema's subreportName values, so a miss here means the document
        /// and the schema disagree -- worth a message that lists what does exist rather than a raw
        /// COM failure.
        /// </summary>
        private static string ResolveSubreportName(
            ISCDReportClientDocument doc, string requested, string reportedTarget)
        {
            var names = doc.SubreportController.GetSubreportNames();
            var known = new List<string>();
            for (var i = 0; i < names.Count; i++)
            {
                var name = names[i] as string ?? names[i]?.ToString() ?? "";
                known.Add(name);
                if (string.Equals(name, requested, StringComparison.OrdinalIgnoreCase)) return name;
            }

            throw new InvalidOperationException(
                $"\"setSubreportLink\": \"{reportedTarget}\" is not a sub-report in this report. " +
                (known.Count == 0
                    ? "The report embeds no sub-reports."
                    : $"Embedded sub-reports: {string.Join(", ", known)}.") +
                " Use a Subreport object's \"subreportName\" from read_report, not its object \"name\".");
        }
    }
}
