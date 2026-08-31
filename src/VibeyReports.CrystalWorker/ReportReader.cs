using System;
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
                        Suppressed = section.Format != null && section.Format.EnableSuppress
                    };

                    var objects = section.ReportObjects;
                    for (var j = 0; j < objects.Count; j++)
                    {
                        info.Objects.Add(ReadObject((ISCRReportObject)objects[j]));
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

        private static ObjectInfo ReadObject(ISCRReportObject ro)
        {
            var info = new ObjectInfo
            {
                Name = ro.Name,
                Kind = ClassifyKind(ro.Kind),
                LeftTwips = ro.Left,
                TopTwips = ro.Top,
                WidthTwips = ro.Width,
                HeightTwips = ro.Height,
                Alignment = ro.Format == null ? null : ClassifyAlignment(ro.Format.HorizontalAlignment)
            };

            switch (ro)
            {
                case ISCRFieldObject field:
                    ApplyFont(info, field.FontColor?.Font);
                    info.DataSource = field.DataSource ?? "";
                    break;

                case ISCRTextObject text:
                    ApplyFont(info, text.FontColor?.Font);
                    info.Text = text.Text ?? "";
                    break;
            }

            return info;
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
