using System;
using System.Collections.Generic;
using CrystalDecisions.ReportAppServer.ClientDoc;
using CrystalDecisions.ReportAppServer.DataDefModel;
using CrystalDecisions.ReportAppServer.ReportDefModel;
using VibeyReports.Contracts;

namespace VibeyReports.CrystalWorker
{
    public static class ReportReader
    {
        public static ReportSchema Read(CrystalSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            var doc = session.Document;
            var schema = new ReportSchema { ReportPath = session.SourcePath };

            var printOptions = doc.PrintOutputController.GetPrintOptions();
            var margins = printOptions.PageMargins;
            schema.Page = new PageInfo
            {
                // FIX (round 1, F1): PageContentWidth/Height are the PRINTABLE area (margins
                // already removed), NOT the full paper size — the brief's original code assigned
                // them straight to WidthTwips/HeightTwips, which double-subtracts margins because
                // LayoutPlanValidator (Task 3) computes printable = Width - MarginLeft - MarginRight
                // itself. ISCRPrintOptions exposes no direct paper-size-in-twips property (only a
                // PaperSize enum, which can't express custom sizes), so reconstruct the full paper
                // size by adding the margins back. Read margins once and reuse below so the two
                // cannot drift.
                WidthTwips = printOptions.PageContentWidth + margins.Left + margins.Right,
                HeightTwips = printOptions.PageContentHeight + margins.Top + margins.Bottom,
                // VERIFIED: ISCRPageMargins exposes Left/Right/Top/Bottom, not the
                // leftMargin/rightMargin/topMargin/bottomMargin field names the brief assumed.
                MarginLeftTwips = margins.Left,
                MarginRightTwips = margins.Right,
                MarginTopTwips = margins.Top,
                MarginBottomTwips = margins.Bottom,
                Orientation = printOptions.PaperOrientation == CrPaperOrientationEnum.crPaperOrientationLandscape
                    ? "Landscape"
                    : "Portrait"
            };

            // VERIFIED: ISCRReportDefinition has NO `Sections` property. Sections live on Areas,
            // and ISCRArea.Kind carries the band. Walk Areas, not Sections.
            var areas = doc.ReportDefController.ReportDefinition.Areas;
            for (var a = 0; a < areas.Count; a++)
            {
                var area = areas[a];
                var band = ClassifyArea(area.Kind);
                var sections = area.Sections;

                for (var i = 0; i < sections.Count; i++)
                {
                    var section = sections[i];
                    var info = new SectionInfo
                    {
                        Name = section.Name,
                        Kind = band,
                        HeightTwips = section.Height,
                        // Every property setSectionBreak and setSuppress's section form can write
                        // is read back here. A write-only property leaves the agent blind, and a
                        // round-trip test that asserts a DIFFERENT property than the one written
                        // can never fail -- this project has shipped that mistake twice.
                        Suppressed = section.Format != null && section.Format.EnableSuppress,
                        NewPageBefore = section.Format != null && section.Format.EnableNewPageBefore,
                        NewPageAfter = section.Format != null && section.Format.EnableNewPageAfter,
                        SuppressIfBlank = section.Format != null && section.Format.EnableSuppressIfBlank,
                        BackgroundColorHex = section.Format == null ? null : ColorRef.ToHex(section.Format.BackgroundColor)
                    };

                    var objects = section.ReportObjects;
                    for (var j = 0; j < objects.Count; j++)
                    {
                        info.Objects.Add(ReadObject(doc, (ISCRReportObject)objects[j]));
                    }

                    schema.Sections.Add(info);
                }
            }

            // Field enumeration must never require a live database connection. If the report's
            // data source needs a logon (or anything else goes wrong), leave AvailableFields empty
            // for this report rather than failing the whole read.
            try
            {
                var tables = doc.DatabaseController.Database.Tables;
                for (var t = 0; t < tables.Count; t++)
                {
                    var table = tables[t];
                    var fields = table.DataFields;
                    for (var f = 0; f < fields.Count; f++)
                    {
                        var dbField = (ISCRDBField)fields[f];
                        schema.AvailableFields.Add(new FieldInfo
                        {
                            Name = dbField.Name ?? "",
                            FormulaForm = dbField.FormulaForm ?? "",
                            TableAlias = dbField.TableAlias ?? "",
                            ValueType = ClassifyValueType(dbField.Type),
                            HeadingText = dbField.HeadingText ?? ""
                        });
                    }
                }
            }
            catch
            {
                // Deliberately bare: reading layout must never require a live database
                // connection, so any failure here (logon prompt, network error, driver
                // exception, or anything else) is swallowed rather than enumerated.
                schema.AvailableFields.Clear();
            }

            return schema;
        }

        private static ObjectInfo ReadObject(ISCDReportClientDocument doc, ISCRReportObject ro)
        {
            var info = new ObjectInfo
            {
                Name = ro.Name,
                Kind = ClassifyKind(ro.Kind),
                LeftTwips = ro.Left,
                TopTwips = ro.Top,
                WidthTwips = ro.Width,
                HeightTwips = ro.Height,
                Alignment = ro.Format == null ? null : ClassifyAlignment(ro.Format.HorizontalAlignment),
                // What setCanGrow and setSuppress's object form write. ISCRObjectFormat is carried
                // by every report object kind, so these are read for all of them, not just fields.
                CanGrow = ro.Format != null && ro.Format.EnableCanGrow,
                Suppressed = ro.Format != null && ro.Format.EnableSuppress
            };

            switch (ro)
            {
                case ISCRFieldObject field:
                    ApplyFont(info, field.FontColor?.Font);
                    info.DataSource = field.DataSource ?? "";
                    if (field.FontColor != null) info.TextColorHex = ColorRef.ToHex(field.FontColor.Color);
                    info.NumberFormat = ReadNumberFormat(field);
                    break;

                case ISCRTextObject text:
                    ApplyFont(info, text.FontColor?.Font);
                    info.Text = text.Text ?? "";
                    if (text.FontColor != null) info.TextColorHex = ColorRef.ToHex(text.FontColor.Color);
                    break;

                case ISCRBoxObject box:
                    info.FillColorHex = ColorRef.ToHex(box.FillColor);
                    info.LineColorHex = ColorRef.ToHex(box.LineColor);
                    break;

                case ISCRLineObject line:
                    info.LineColorHex = ColorRef.ToHex(line.LineColor);
                    break;

                case ISCRSubreportObject subreport:
                    // Measured (reflection + a live round-trip against the installed 11.5
                    // assemblies): the placed report OBJECT's Name (ro.Name, e.g. "Subreport1")
                    // is Crystal's own auto-numbered container name and is NOT the string
                    // addSubreport's newName supplied to ImportSubreportEx -- that string instead
                    // becomes ISCRSubreportObject.SubreportName, the separate identifier
                    // SubreportController.GetSubreportLinks/SetSubreportLinks are keyed by. Using
                    // ro.Name here (the container name) would ask GetSubreportLinks for a name
                    // that was never registered as a sub-report and silently read back nothing.
                    // F2: that split identity is exactly what setSubreportLink's target needs, so
                    // report it instead of consuming it privately here. Without this the agent can
                    // see only ro.Name -- the one name setSubreportLink does NOT accept -- and
                    // linking an already-embedded sub-report is impossible to express.
                    info.SubreportName = subreport.SubreportName ?? "";
                    info.SubreportLinks = ReadSubreportLinks(doc, subreport.SubreportName);
                    break;
            }

            return info;
        }

        /// <summary>
        /// Reads back exactly the three properties setNumberFormat writes, so the operation is not
        /// write-only and a round-trip test asserts the same properties it set.
        ///
        /// Measured on SampleReport.rpt: ISCRFieldFormat.NumericFormat is present and readable on
        /// every Field object regardless of its value type -- a String field (CardName1,
        /// crFieldValueTypeStringField) still reports NDecimalPlaces=2 -- so this is not restricted
        /// to numeric fields. A Text object has no ISCRFieldFormat at all, which is why this is
        /// only called from the ISCRFieldObject arm.
        ///
        /// Wrapped defensively, exactly as ReadSubreportLinks is: an unusual field kind that throws
        /// or returns nothing here must leave the property null, not fail the whole report read.
        /// </summary>
        private static Contracts.NumberFormatInfo ReadNumberFormat(ISCRFieldObject field)
        {
            try
            {
                var numeric = field.FieldFormat?.NumericFormat;
                if (numeric == null) return null;

                return new Contracts.NumberFormatInfo
                {
                    DecimalPlaces = numeric.NDecimalPlaces,
                    ThousandsSeparator = numeric.ThousandsSeparator,
                    SuppressIfZero = numeric.EnableSuppressIfZero
                };
            }
            catch
            {
                // Deliberately bare, matching ReadSubreportLinks and the AvailableFields walk: a
                // format that cannot be read is a null property, never a failed read.
                return null;
            }
        }

        /// <summary>
        /// The same lesson this project has learned twice over: a write-only API leaves the agent
        /// blind, and a round-trip test that reads a different property than it writes can never
        /// fail. This reads back exactly what SetSubreportLinks was given, via GetSubreportLinks.
        /// Wrapped defensively -- a sub-report with no links yet may throw here rather than return
        /// an empty collection -- so a sub-report before its first setSubreportLink call still
        /// reads back cleanly with an empty list instead of failing the whole report read.
        /// </summary>
        private static List<SubreportLinkInfo> ReadSubreportLinks(
            ISCDReportClientDocument doc, string subreportName)
        {
            var result = new List<SubreportLinkInfo>();
            try
            {
                // GetSubreportLinks is COM-typed to the marker interface SubreportLinks, but
                // Count/Item live on the "ISCR" dual interface the same concrete
                // SubreportLinksClass also implements -- verified by reflecting the installed
                // 11.5 ReportDefModel assembly.
                var links = (ISCRSubreportLinks)doc.SubreportController.GetSubreportLinks(subreportName);
                for (var i = 0; i < links.Count; i++)
                {
                    var link = (ISCRSubreportLink)links[i];
                    result.Add(new SubreportLinkInfo
                    {
                        MainReportFieldName = link.MainReportFieldName ?? "",
                        SubreportFieldName = link.SubreportFieldName ?? "",
                        LinkedParameterName = link.LinkedParameterName ?? ""
                    });
                }
            }
            catch
            {
                // Deliberately bare, matching AvailableFields' read-time contract above: a
                // sub-report with no links yet must not fail the whole report read.
            }

            return result;
        }

        // VERIFIED: FontColor.Font is CrystalDecisions.ReportAppServer.ReportDefModel.Font
        // (an ISCRFont), NOT System.Drawing.Font as the brief assumed. Same property names
        // (Name/Size/Bold/Italic/Underline) though, so the mapping logic is unchanged; only
        // Size is a decimal here, not a float.
        private static void ApplyFont(ObjectInfo info, Font font)
        {
            if (font == null) return;
            info.FontName = font.Name;
            info.FontSizePt = (float)font.Size;
            info.Bold = font.Bold;
            info.Italic = font.Italic;
            info.Underline = font.Underline;
        }

        private static string ClassifyKind(CrReportObjectKindEnum kind)
        {
            switch (kind)
            {
                case CrReportObjectKindEnum.crReportObjectKindField: return "Field";
                case CrReportObjectKindEnum.crReportObjectKindText: return "Text";
                case CrReportObjectKindEnum.crReportObjectKindLine: return "Line";
                case CrReportObjectKindEnum.crReportObjectKindBox: return "Box";
                case CrReportObjectKindEnum.crReportObjectKindSubreport: return "Subreport";
                case CrReportObjectKindEnum.crReportObjectKindPicture: return "Picture";
                case CrReportObjectKindEnum.crReportObjectKindChart: return "Chart";
                case CrReportObjectKindEnum.crReportObjectKindCrosstab: return "Crosstab";
                case CrReportObjectKindEnum.crReportObjectKindFieldHeading: return "FieldHeading";
                case CrReportObjectKindEnum.crReportObjectKindBlobField: return "BlobField";
                case CrReportObjectKindEnum.crReportObjectKindMap: return "Map";
                case CrReportObjectKindEnum.crReportObjectKindOlapGrid: return "OlapGrid";
                default: return "Other";
            }
        }

        private static string ClassifyAlignment(CrAlignmentEnum alignment)
        {
            switch (alignment)
            {
                case CrAlignmentEnum.crAlignmentLeft: return "Left";
                case CrAlignmentEnum.crAlignmentRight: return "Right";
                case CrAlignmentEnum.crAlignmentHorizontalCenter: return "Centre";
                case CrAlignmentEnum.crAlignmentJustified: return "Justified";
                default: return "Default";
            }
        }

        /// <summary>
        /// The band comes from the parent Area's Kind, NOT from the section name.
        /// RAS names sections "Section1", "Section2"... with no band encoded, so any
        /// name-based heuristic returns "Other" for everything.
        /// </summary>
        private static string ClassifyArea(CrAreaSectionKindEnum kind)
        {
            switch (kind)
            {
                case CrAreaSectionKindEnum.crAreaSectionKindReportHeader: return "ReportHeader";
                case CrAreaSectionKindEnum.crAreaSectionKindPageHeader: return "PageHeader";
                case CrAreaSectionKindEnum.crAreaSectionKindGroupHeader: return "GroupHeader";
                case CrAreaSectionKindEnum.crAreaSectionKindDetail: return "Details";
                case CrAreaSectionKindEnum.crAreaSectionKindGroupFooter: return "GroupFooter";
                case CrAreaSectionKindEnum.crAreaSectionKindReportFooter: return "ReportFooter";
                case CrAreaSectionKindEnum.crAreaSectionKindPageFooter: return "PageFooter";
                default: return "Other";
            }
        }

        // VERIFIED against the installed CrystalDecisions.ReportAppServer.DataDefModel.dll:
        // every member the supplement named exists (crFieldValueTypeStringField, the six
        // integer variants, NumberField, CurrencyField, DateField, TimeField, DateTimeField,
        // BooleanField, BlobField). No renames were required. The enum also has several members
        // not covered here (Decimal, Int64s/u, Memo, Bitmap, Icon, Picture, Ole, Chart,
        // SameAsInput, Pointer, InterfacePointer, Unknown) which fall through to "Other".
        private static string ClassifyValueType(CrFieldValueTypeEnum type)
        {
            switch (type)
            {
                case CrFieldValueTypeEnum.crFieldValueTypeStringField: return "String";
                case CrFieldValueTypeEnum.crFieldValueTypeInt8sField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt8uField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt16sField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt16uField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt32sField:
                case CrFieldValueTypeEnum.crFieldValueTypeInt32uField:
                case CrFieldValueTypeEnum.crFieldValueTypeNumberField: return "Number";
                case CrFieldValueTypeEnum.crFieldValueTypeCurrencyField: return "Currency";
                case CrFieldValueTypeEnum.crFieldValueTypeDateField: return "Date";
                case CrFieldValueTypeEnum.crFieldValueTypeTimeField: return "Time";
                case CrFieldValueTypeEnum.crFieldValueTypeDateTimeField: return "DateTime";
                case CrFieldValueTypeEnum.crFieldValueTypeBooleanField: return "Boolean";
                case CrFieldValueTypeEnum.crFieldValueTypeBlobField: return "Blob";
                default: return "Other";
            }
        }
    }
}
