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
        public static int Apply(CrystalSession session, LayoutPlan plan)
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
        internal static int ApplyOperationsWithoutValidation(CrystalSession session, LayoutPlan plan)
        {
            var doc = session.Document;
            var applied = 0;

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

                applied++;
            }

            return applied;
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

        private static void AddText(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            var text = new TextObjectClass
            {
                Name = op.NewName,
                Left = op.LeftTwips.Value,
                Top = op.TopTwips.Value,
                Width = op.WidthTwips.Value,
                Height = op.HeightTwips.Value
            };

            var element = new ParagraphTextElementClass { Text = op.Text };
            var paragraph = new ParagraphClass();
            paragraph.ParagraphElements.Add(element);
            text.Paragraphs.Add(paragraph);

            doc.ReportDefController.ReportObjectController.Add(text, section, -1);
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

            doc.ReportDefController.ReportObjectController.Add(line, section, -1);
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

            doc.ReportDefController.ReportObjectController.Add(box, section, -1);
        }

        /// <summary>
        /// Primary path: construct an ISCRFieldObject directly and bind it via DataSource, the
        /// same "construct then Add(obj, section, -1)" idiom AddText/AddLine/AddBox use.
        /// Measured on this machine: RAS can reject this with COMException "The field value type
        /// is not valid." -- a bare DataSource string apparently lacks the field-kind/value-type
        /// metadata RAS normally derives from the database field object itself. When that happens,
        /// fall back to ReportObjectController.AddByName, which goes through RAS's own field
        /// resolution instead of a hand-built FieldObjectClass. See task-6b-report.md for which
        /// path fired on which fixture.
        /// </summary>
        private static void AddField(
            ISCDReportClientDocument doc,
            LayoutOperation op)
        {
            var section = FindSection(doc, op.Section);

            var field = new FieldObjectClass
            {
                Name = op.NewName,
                DataSource = op.FieldRef,
                Left = op.LeftTwips.Value,
                Top = op.TopTwips.Value,
                Width = op.WidthTwips.Value,
                Height = op.HeightTwips.Value
            };

            // Measured: a bare DataSource string alone gets "The field value type is not valid."
            // from RAS -- ISCRFieldObject.FieldValueType defaults to a value RAS won't accept, and
            // it must be set to match the actual database field's type, resolved via the same
            // table walk ReportReader.Read uses for AvailableFields.
            var dbFieldType = ResolveFieldValueType(doc, op.FieldRef);
            if (dbFieldType.HasValue) field.FieldValueType = dbFieldType.Value;

            try
            {
                doc.ReportDefController.ReportObjectController.Add(field, section, -1);
                return;
            }
            catch (COMException)
            {
                // Fall through to the AddByName fallback below.
            }

            AddFieldByName(doc, op);
        }

        /// <summary>
        /// Same table walk ReportReader.Read uses for AvailableFields, but returning the raw
        /// CrFieldValueTypeEnum instead of the already-classified string.
        /// </summary>
        private static CrFieldValueTypeEnum? ResolveFieldValueType(ISCDReportClientDocument doc, string fieldRef)
        {
            var tables = doc.DatabaseController.Database.Tables;
            for (var t = 0; t < tables.Count; t++)
            {
                var fields = tables[t].DataFields;
                for (var f = 0; f < fields.Count; f++)
                {
                    var dbField = (ISCRDBField)fields[f];
                    if (string.Equals(dbField.FormulaForm, fieldRef, StringComparison.OrdinalIgnoreCase))
                        return dbField.Type;
                }
            }
            return null;
        }

        /// <summary>
        /// Fallback documented in task-6b-brief.md. AddByName resolves the field itself (it picks
        /// its own section and position, which is why it is not the primary path -- it cannot
        /// satisfy a positioned layout plan on its own), so after calling it we locate the object
        /// it created via GetAllReportObjects() and apply the plan's requested name/position with
        /// the existing ModifyObject helper.
        /// </summary>
        private static void AddFieldByName(ISCDReportClientDocument doc, LayoutOperation op)
        {
            var controller = doc.ReportDefController.ReportObjectController;

            var beforeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var before = controller.GetAllReportObjects();
            for (var i = 0; i < before.Count; i++)
                beforeNames.Add(((ISCRReportObject)before[i]).Name);

            // Measured: AddByName rejects ISCRDBField.Name ("Database Field Not Found") but
            // accepts the FormulaForm expression itself, e.g. "{Table.Field}".
            controller.AddByName(op.FieldRef, op.NewName);

            var after = controller.GetAllReportObjects();
            ISCRReportObject created = null;
            for (var i = 0; i < after.Count; i++)
            {
                var ro = (ISCRReportObject)after[i];
                if (ro is ISCRFieldObject && !beforeNames.Contains(ro.Name))
                {
                    created = ro;
                    break;
                }
            }

            if (created == null)
                throw new InvalidOperationException("AddByName fallback did not create a new field object.");

            var createdName = created.Name;
            try
            {
                ModifyObject(doc, createdName, o =>
                {
                    o.Name = op.NewName;
                    o.Left = op.LeftTwips.Value;
                    o.Top = op.TopTwips.Value;
                    o.Width = op.WidthTwips.Value;
                    o.Height = op.HeightTwips.Value;
                });
            }
            catch (COMException)
            {
                // Measured: RAS refuses to rename a report object via Modify() ("Cannot change
                // report object name.") -- there is no Rename/SetName elsewhere on
                // ISCRReportObjectController either (reflected directly: Add, Remove, Modify,
                // GetAllReportObjects, GetReportObjectsByKind, AddByName, ImportPicture). Still
                // apply the requested position under the RAS-assigned name, then surface the
                // mismatch explicitly instead of silently leaving an object that later operations
                // targeting op.NewName could never find.
                ModifyObject(doc, createdName, o =>
                {
                    o.Left = op.LeftTwips.Value;
                    o.Top = op.TopTwips.Value;
                    o.Width = op.WidthTwips.Value;
                    o.Height = op.HeightTwips.Value;
                });
                throw new InvalidOperationException(
                    $"AddByName fallback created field \"{createdName}\" but RAS does not allow " +
                    $"renaming it to \"{op.NewName}\"; it was positioned in place under its " +
                    "original name instead.");
            }
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
