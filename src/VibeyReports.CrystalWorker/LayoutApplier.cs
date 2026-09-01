using System;
using System.Collections.Generic;
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
                            RemoveObject(doc, op.Target);
                            result.RemovedObjects.Add(op.Target!);
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
        /// caused it.
        /// </summary>
        private static void RemoveObject(ISCDReportClientDocument doc, string objectName)
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
    }
}
