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
            // Group header/footer SECTION names in document order, collected here so GroupInfo can
            // report the names Crystal chose for each group's two sections. Crystal names them
            // itself and there is no property on ISCRGroup pointing at them, so the mapping is by
            // position: headers appear in group order, footers in REVERSE group order (measured --
            // group 0 is the outermost, so its footer prints last).
            var groupHeaderSections = new List<string>();
            var groupFooterSections = new List<string>();

            var areas = doc.ReportDefController.ReportDefinition.Areas;
            for (var a = 0; a < areas.Count; a++)
            {
                var area = areas[a];
                var band = ClassifyArea(area.Kind);
                var sections = area.Sections;

                if (sections.Count > 0)
                {
                    // A group area can hold several sections (Group Header a, b, c); the first is
                    // the one a caller means by "the group header".
                    if (band == "GroupHeader") groupHeaderSections.Add(sections[0].Name);
                    else if (band == "GroupFooter") groupFooterSections.Add(sections[0].Name);
                }

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

            ReadGroupsAndSorts(doc, schema, groupHeaderSections, groupFooterSections);

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

        /// <summary>
        /// Reads back exactly what addGroup and addSort write, so neither is write-only: the field
        /// and the direction for both, plus -- for a group -- the two section names Crystal chose,
        /// which the caller has no other way to learn and needs in order to place anything into the
        /// new band.
        ///
        /// A group's direction is NOT a property of the group: measured, ISCRGroupOptions carries
        /// no direction at all, and GroupController.Add creates an entry in DataDefinition.Sorts
        /// for the grouped field instead. So Sorts is read first and the group's direction is taken
        /// from the entry on its own field. That also means Sorts legitimately reports one entry
        /// per group before any addSort has been issued, which is the truth about the report rather
        /// than an artefact of this reader.
        ///
        /// Wrapped defensively, like the AvailableFields walk and ReadSubreportLinks: a report with
        /// no groups must read back as empty lists, never as a failed read.
        /// </summary>
        private static void ReadGroupsAndSorts(
            ISCDReportClientDocument doc,
            ReportSchema schema,
            List<string> groupHeaderSections,
            List<string> groupFooterSections)
        {
            try
            {
                var dataDefinition = doc.DataDefController.DataDefinition;

                var sorts = dataDefinition.Sorts;
                for (var i = 0; i < sorts.Count; i++)
                {
                    var sort = (ISCRSort)sorts[i];
                    schema.Sorts.Add(new SortInfo
                    {
                        FieldRef = sort.SortField == null ? "" : (sort.SortField.FormulaForm ?? ""),
                        Direction = ClassifySortDirection(sort.Direction)
                    });
                }

                var groups = dataDefinition.Groups;
                for (var g = 0; g < groups.Count; g++)
                {
                    var group = (ISCRGroup)groups[g];
                    var fieldRef = group.ConditionField == null ? "" : (group.ConditionField.FormulaForm ?? "");

                    schema.Groups.Add(new GroupInfo
                    {
                        FieldRef = fieldRef,
                        Direction = DirectionOfSortOn(schema.Sorts, fieldRef),
                        // Only claim a section name when the counts line up exactly. A report whose
                        // group areas and groups disagree is not one this positional mapping can
                        // describe, and a wrong name here is worse than none: it is the name the
                        // caller would then place objects into.
                        HeaderSection = groupHeaderSections.Count == groups.Count ? groupHeaderSections[g] : null,
                        FooterSection = groupFooterSections.Count == groups.Count
                            ? groupFooterSections[groups.Count - 1 - g]
                            : null
                    });
                }
            }
            catch
            {
                // Deliberately bare, matching the AvailableFields walk: a report whose group or
                // sort definitions cannot be read must still read back as a report.
                schema.Groups.Clear();
                schema.Sorts.Clear();
            }
        }

        private static string DirectionOfSortOn(List<SortInfo> sorts, string fieldRef)
        {
            if (string.IsNullOrEmpty(fieldRef)) return "";
            foreach (var sort in sorts)
                if (string.Equals(sort.FieldRef, fieldRef, StringComparison.OrdinalIgnoreCase))
                    return sort.Direction;
            return "";
        }

        /// <summary>
        /// Maps CrSortDirectionEnum onto the two-value vocabulary addGroup/addSort accept. The four
        /// TopN variants cannot be SET by this tool (they need an N no operation can express), but a
        /// report edited elsewhere may well carry one, so those are reported verbatim under their
        /// Crystal enum name rather than being flattened into a wrong "ascending".
        /// </summary>
        private static string ClassifySortDirection(CrSortDirectionEnum direction)
        {
            switch (direction)
            {
                case CrSortDirectionEnum.crSortDirectionAscendingOrder: return SortDirections.Ascending;
                case CrSortDirectionEnum.crSortDirectionDescendingOrder: return SortDirections.Descending;
                default: return direction.ToString();
            }
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
                Suppressed = ro.Format != null && ro.Format.EnableSuppress,
                // What setBorder writes. ISCRBorder hangs off ISCRReportObject itself, so this is
                // read for every kind -- there is no kind gate on the write side either.
                Border = ReadBorder(ro)
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
                    // Without this a rebuild flattens every rule to Crystal's default weight of
                    // 15 twips. These reports use 10 for a hairline panel divider and 20 for a
                    // heavier tile outline, and the difference is plainly visible.
                    info.LineThicknessTwips = box.LineThickness;
                    break;

                case ISCRLineObject line:
                    info.LineColorHex = ColorRef.ToHex(line.LineColor);
                    info.LineThicknessTwips = line.LineThickness;
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
        /// Reads back exactly the five properties setBorder writes -- the four side styles and
        /// the colour -- so the operation is not write-only and a round-trip test asserts the same
        /// properties it set. That matters here specifically: this project has shipped a
        /// write-only property twice, and setNumberFormat's silent no-op survived its first test
        /// because the assertion looked at something the applier never touched.
        ///
        /// Styles come back as the friendly BorderStyles names the operation accepts, not as
        /// CrLineStyleEnum names, so what is read is directly comparable with what was written.
        ///
        /// Wrapped defensively, exactly as ReadNumberFormat and ReadSubreportLinks are: a border
        /// that cannot be read leaves the property null rather than failing the whole report read.
        /// </summary>
        private static BorderInfo ReadBorder(ISCRReportObject ro)
        {
            try
            {
                var border = ro.Border;
                if (border == null) return null;

                return new BorderInfo
                {
                    Left = ClassifyLineStyle(border.LeftLineStyle),
                    Right = ClassifyLineStyle(border.RightLineStyle),
                    Top = ClassifyLineStyle(border.TopLineStyle),
                    Bottom = ClassifyLineStyle(border.BottomLineStyle),
                    ColorHex = ColorRef.ToHex(border.BorderColor)
                };
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// CrLineStyleEnum -&gt; the BorderStyles vocabulary. A member Crystal reports that this
        /// tool has no name for is returned verbatim as its enum name rather than being flattened
        /// into "none", which would report a border that is there as one that is not.
        /// </summary>
        private static string ClassifyLineStyle(CrLineStyleEnum style)
        {
            switch (style)
            {
                case CrLineStyleEnum.crLineStyleNoLine: return BorderStyles.None;
                case CrLineStyleEnum.crLineStyleSingle: return BorderStyles.Single;
                case CrLineStyleEnum.crLineStyleDouble: return BorderStyles.Double;
                case CrLineStyleEnum.crLineStyleDashed: return BorderStyles.Dashed;
                case CrLineStyleEnum.crLineStyleDotted: return BorderStyles.Dotted;
                default: return style.ToString();
            }
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
                var fieldFormat = field.FieldFormat;
                var numeric = fieldFormat?.NumericFormat;
                if (numeric == null) return null;

                return new Contracts.NumberFormatInfo
                {
                    DecimalPlaces = numeric.NDecimalPlaces,
                    ThousandsSeparator = numeric.ThousandsSeparator,
                    SuppressIfZero = numeric.EnableSuppressIfZero,
                    // Measured: while this is true Crystal formats from the locale defaults and
                    // discards writes to the two properties above, so reporting them without it
                    // would be reporting values that are not what renders.
                    SystemDefault = fieldFormat.CommonFormat != null && fieldFormat.CommonFormat.EnableSystemDefault
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
