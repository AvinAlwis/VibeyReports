using System;
using System.Text;
using EngineDoc = CrystalDecisions.CrystalReports.Engine.ReportDocument;
using CrystalDecisions.ReportAppServer.ClientDoc;
using CrystalDecisions.ReportAppServer.Controllers;
using CrystalDecisions.ReportAppServer.ReportDefModel;

namespace SubEdit
{
    internal static class Program
    {
        static string Col(uint c) => c == 0xFFFFFFFF ? "none"
            : "#" + (c & 0xFF).ToString("X2") + ((c >> 8) & 0xFF).ToString("X2") + ((c >> 16) & 0xFF).ToString("X2");

        [STAThread]
        static int Main(string[] args)
        {
            var rd = new EngineDoc();
            rd.Load(args[1]);
            try
            {
                var doc = rd.ReportClientDocument;
                if (args[0] == "dump") Dump(doc, args.Length > 2 ? args[2] : null);
                if (args[0] == "fixtables" || args[0] == "gridtables" || args[0] == "extend" || args[0] == "subgeom"
                    || args[0] == "italic" || args[0] == "time24" || args[0] == "addcolumns" || args[0] == "rebind"
                    || args[0] == "subrefresh" || args[0] == "subswap" || args[0] == "keeptogether")
                {
                    if (args[0] == "fixtables") FixTables(doc, args[3].Split(','));
                    else if (args[0] == "extend") Extend(doc, args[3].Split(','));
                    else if (args[0] == "subgeom") SubGeom(doc, args[3]);
                    else if (args[0] == "italic") Italic(doc, args[3].Split(','));
                    else if (args[0] == "time24") Time24(doc, args[3].Split(','));
                    else if (args[0] == "addcolumns") AddColumns(doc, args[3], args[4].Split(','));
                    else if (args[0] == "rebind") Rebind(doc, args[3], args[4]);
                    else if (args[0] == "subrefresh") SubRefresh(doc, args[3]);
                    else if (args[0] == "subswap") SubSwap(doc, args[3], args[4], args[5]);
                    else if (args[0] == "keeptogether") KeepTogether(doc, args[3].Split(','));
                    else GridTables(doc, args[3].Split(','));
                    var outPath = System.IO.Path.GetFullPath(args[2]);
                    var dir = System.IO.Path.GetDirectoryName(outPath);
                    var tmp = "_subedit_tmp.rpt";
                    var tmpFull = System.IO.Path.Combine(dir, tmp);
                    if (System.IO.File.Exists(tmpFull)) System.IO.File.Delete(tmpFull);
                    doc.SaveAs(tmp, dir, 0);
                    rd.Close();
                    if (System.IO.File.Exists(outPath)) System.IO.File.Delete(outPath);
                    System.IO.File.Move(tmpFull, outPath);
                    Console.WriteLine("saved " + outPath);
                }
                return 0;
            }
            finally { rd.Close(); }
        }

        // Crystal draws a field's border 1.5pt (30 twips) OUTSIDE the field, while a drawn Box or
        // Line sits 0.35pt inside its own coordinate - measured on Report 2 from the PDF drawing
        // operators. Shifting a bordered field right by 37 twips lands its left border exactly on a
        // drawn line at the field's original x.
        const int Shift = 37;
        const int Indent = 90;
        const int CellInset = 70;

        static void FixTables(ISCDReportClientDocument doc, string[] subreports)
        {
            foreach (var name in subreports)
            {
                var ctl = ((ISCRSubreportClientDocument)doc.SubreportController.GetSubreport(name)).ReportDefController;
                foreach (Area a in ctl.ReportDefinition.Areas)
                foreach (Section s in a.Sections)
                {
                    if (s.Name != "DetailSection1") continue;
                    var fields = new System.Collections.Generic.List<FieldObject>();
                    foreach (ReportObject o in s.ReportObjects)
                        if (o is FieldObject f) fields.Add(f);
                    fields.Sort((x, y) => x.Left.CompareTo(y.Left));
                    for (int i = 0; i < fields.Count; i++)
                    {
                        var f = fields[i];
                        bool last = i == fields.Count - 1;
                        var c = (FieldObject)f.Clone(true);
                        c.Left = f.Left + Shift;
                        if (last) c.Width = f.Width - 2 * Shift;
                        else c.Border.RightLineStyle = CrLineStyleEnum.crLineStyleNoLine;

                        bool numeric = c.FieldFormat.NumericFormat != null &&
                                       (c.FieldValueType == CrystalDecisions.ReportAppServer.DataDefModel.CrFieldValueTypeEnum.crFieldValueTypeNumberField ||
                                        c.FieldValueType == CrystalDecisions.ReportAppServer.DataDefModel.CrFieldValueTypeEnum.crFieldValueTypeCurrencyField ||
                                        c.FieldValueType == CrystalDecisions.ReportAppServer.DataDefModel.CrFieldValueTypeEnum.crFieldValueTypeInt32sField ||
                                        c.FieldValueType == CrystalDecisions.ReportAppServer.DataDefModel.CrFieldValueTypeEnum.crFieldValueTypeInt64sField);
                        string how;
                        if (numeric)
                        {
                            // A numeric format has no indentation; centring is the only padding it can have.
                            c.Format.HorizontalAlignment = CrAlignmentEnum.crAlignmentHorizontalCenter;
                            c.FieldFormat.NumericFormat.EnableSuppressIfZero = true;
                            how = "centred";
                        }
                        else if (c.FieldFormat.StringFormat?.IndentAndSpacingFormat != null)
                        {
                            c.FieldFormat.StringFormat.IndentAndSpacingFormat.LeftIndent = Indent;
                            c.FieldFormat.StringFormat.IndentAndSpacingFormat.RightIndent = Indent;
                            how = "indented";
                        }
                        else how = "no padding available";
                        ctl.ReportObjectController.Modify(f, c);
                        Console.WriteLine($"  {name}.{f.Name} {f.Left}->{c.Left} w={c.Width} right={(last ? "kept" : "none")} {how} ({c.FieldValueType})");
                    }
                }
            }
        }

        // The ruling is DRAWN, not bordered. Crystal draws no border at all for a field whose value
        // is blank - NULL, '', a non-breaking space, or a zero hidden by suppress-if-zero, all
        // measured - so field borders can never give a closed grid once a cell is empty. A Box and
        // Lines set to extend to the bottom of the section grow with the tallest cell instead, and
        // sit on exactly the same coordinates as the header's own Box and Lines.
        static void GridTables(ISCDReportClientDocument doc, string[] subreports)
        {
            foreach (var name in subreports)
            {
                var ctl = ((ISCRSubreportClientDocument)doc.SubreportController.GetSubreport(name)).ReportDefController;
                BoxObject headerBox = null;
                var headerLines = new System.Collections.Generic.List<LineObject>();
                Section detail = null;
                foreach (Area a in ctl.ReportDefinition.Areas)
                foreach (Section s in a.Sections)
                {
                    if (s.Name == "DetailSection1") detail = s;
                    if (s.Name == "ReportHeaderSection2")
                        foreach (ReportObject o in s.ReportObjects)
                        {
                            if (o is BoxObject b) headerBox = b;
                            if (o is LineObject l) headerLines.Add(l);
                        }
                }
                if (detail == null || headerBox == null) throw new InvalidOperationException(name + ": table layout not recognised");

                var fields = new System.Collections.Generic.List<FieldObject>();
                foreach (ReportObject o in detail.ReportObjects)
                {
                    if (o is FieldObject f) fields.Add(f);
                    else if (o is BoxObject || o is LineObject) throw new InvalidOperationException(name + ": detail already has drawn rules");
                }

                // 1. Fields: no borders, inset from the rules by the same 70 twips the header text
                //    uses, numbers centred. Inset by GEOMETRY, not paragraph indent: a memo field
                //    (the comment columns) ignores IndentAndSpacingFormat - measured.
                foreach (var f in fields)
                {
                    var c = (FieldObject)f.Clone(true);
                    c.Border.LeftLineStyle = CrLineStyleEnum.crLineStyleNoLine;
                    c.Border.RightLineStyle = CrLineStyleEnum.crLineStyleNoLine;
                    c.Border.TopLineStyle = CrLineStyleEnum.crLineStyleNoLine;
                    c.Border.BottomLineStyle = CrLineStyleEnum.crLineStyleNoLine;
                    c.Left = f.Left + CellInset;
                    c.Width = f.Width - 2 * CellInset;
                    var t = c.FieldValueType;
                    bool numeric = t == CrystalDecisions.ReportAppServer.DataDefModel.CrFieldValueTypeEnum.crFieldValueTypeNumberField
                                || t == CrystalDecisions.ReportAppServer.DataDefModel.CrFieldValueTypeEnum.crFieldValueTypeCurrencyField
                                || t == CrystalDecisions.ReportAppServer.DataDefModel.CrFieldValueTypeEnum.crFieldValueTypeInt32sField
                                || t == CrystalDecisions.ReportAppServer.DataDefModel.CrFieldValueTypeEnum.crFieldValueTypeInt64sField;
                    if (numeric) c.Format.HorizontalAlignment = CrAlignmentEnum.crAlignmentHorizontalCenter;
                    ctl.ReportObjectController.Modify(f, c);
                    Console.WriteLine($"  {name}.{f.Name} borders off, inset {c.Left}+{c.Width}{(numeric ? ", centred" : "")} ({t})");
                }

                int bottom = detail.Height;

                // 2. One box per row: its top meets the previous row's bottom, so they read as one rule.
                var box = (BoxObject)headerBox.Clone(true);
                box.Name = "RowBox";
                box.SectionName = detail.Name;
                box.EndSectionName = detail.Name;
                box.Top = 0; box.Bottom = bottom;
                box.FillColor = 0xFFFFFFFF;          // no fill
                box.EnableExtendToBottomOfSection = true;
                ctl.ReportObjectController.Add(box, detail, -1);

                // 3. One divider per header divider, at the same x.
                foreach (var hl in headerLines)
                {
                    var l = (LineObject)hl.Clone(true);
                    l.Name = "Row" + hl.Name;
                    l.SectionName = detail.Name;
                    l.EndSectionName = detail.Name;
                    l.Top = 0; l.Bottom = bottom;
                    l.EnableExtendToBottomOfSection = true;
                    ctl.ReportObjectController.Add(l, detail, -1);
                }
                Console.WriteLine($"  {name}: row box + {headerLines.Count} dividers, extend to bottom");
            }
        }

        // Main report: sets "extend to bottom of section" on the named Boxes and Lines, which the
        // layout worker has no operation for. Every name must exist and be a Box or a Line.
        static void Extend(ISCDReportClientDocument doc, string[] names)
        {
            var wanted = new System.Collections.Generic.HashSet<string>(names);
            foreach (Area a in doc.ReportDefinition.Areas)
            foreach (Section s in a.Sections)
            foreach (ReportObject o in s.ReportObjects)
            {
                if (!wanted.Contains(o.Name)) continue;
                if (o is BoxObject b)
                {
                    var c = (BoxObject)b.Clone(true);
                    c.EnableExtendToBottomOfSection = true;
                    doc.ReportDefController.ReportObjectController.Modify(b, c);
                }
                else if (o is LineObject l)
                {
                    var c = (LineObject)l.Clone(true);
                    c.EnableExtendToBottomOfSection = true;
                    doc.ReportDefController.ReportObjectController.Modify(l, c);
                }
                else throw new InvalidOperationException(o.Name + " is not a Box or Line");
                wanted.Remove(o.Name);
                Console.WriteLine($"  {s.Name}.{o.Name} extends to bottom of section");
            }
            if (wanted.Count > 0) throw new InvalidOperationException("Not found: " + string.Join(", ", wanted));
        }

        // "sub:obj:left:top:width:height[:sectionHeight]" - moves/resizes one object inside an embedded
        // sub-report (the worker only reaches the main report), optionally setting its section's height.
        static void SubGeom(ISCDReportClientDocument doc, string spec)
        {
            var p = spec.Split(':');
            var ctl = ((ISCRSubreportClientDocument)doc.SubreportController.GetSubreport(p[0])).ReportDefController;
            foreach (Area a in ctl.ReportDefinition.Areas)
            foreach (Section s in a.Sections)
            foreach (ReportObject o in s.ReportObjects)
            {
                if (o.Name != p[1]) continue;
                if (p.Length > 6)
                {
                    int h = int.Parse(p[6]);
                    if (h > s.Height) ctl.ReportSectionController.SetProperty(s, CrReportSectionPropertyEnum.crReportSectionPropertyHeight, h);
                }
                var c = o.Clone(true) as ReportObject;
                c.Left = int.Parse(p[2]); c.Top = int.Parse(p[3]); c.Width = int.Parse(p[4]); c.Height = int.Parse(p[5]);
                ctl.ReportObjectController.Modify(o, c);
                if (p.Length > 6)
                {
                    int h = int.Parse(p[6]);
                    if (h < s.Height) ctl.ReportSectionController.SetProperty(s, CrReportSectionPropertyEnum.crReportSectionPropertyHeight, h);
                }
                Console.WriteLine($"  {p[0]}.{o.Name} -> {c.Left},{c.Top} {c.Width}x{c.Height}" + (p.Length > 6 ? $" section {s.Name}={p[6]}" : ""));
                return;
            }
            throw new InvalidOperationException("Not found: " + spec);
        }

        static System.Collections.Generic.IEnumerable<(Section s, ReportObject o)> MainObjects(ISCDReportClientDocument doc, string[] names)
        {
            var wanted = new System.Collections.Generic.HashSet<string>(names);
            var found = new System.Collections.Generic.List<(Section, ReportObject)>();
            foreach (Area a in doc.ReportDefinition.Areas)
            foreach (Section s in a.Sections)
            foreach (ReportObject o in s.ReportObjects)
                if (wanted.Remove(o.Name)) found.Add((s, o));
            if (wanted.Count > 0) throw new InvalidOperationException("Not found: " + string.Join(", ", wanted));
            return found;
        }

        // Main report: italicises text objects. The font lives on each text run as well as on the
        // object, so both are set - the run's font is what renders.
        static void Italic(ISCDReportClientDocument doc, string[] names)
        {
            foreach (var (s, o) in MainObjects(doc, names))
            {
                if (!(o is TextObject t)) throw new InvalidOperationException(o.Name + " is not a Text object");
                var c = (TextObject)t.Clone(true);
                c.FontColor.Font.Italic = true;
                foreach (Paragraph p in c.Paragraphs)
                foreach (ParagraphElement e in p.ParagraphElements)
                    if (e is ParagraphTextElement pt && pt.FontColor?.Font != null) pt.FontColor.Font.Italic = true;
                doc.ReportDefController.ReportObjectController.Modify(t, c);
                Console.WriteLine($"  {s.Name}.{o.Name} italic");
            }
        }

        // Main report: a time field shown as 24-hour hh:mm:ss, independent of the server's locale.
        static void Time24(ISCDReportClientDocument doc, string[] names)
        {
            foreach (var (s, o) in MainObjects(doc, names))
            {
                if (!(o is FieldObject f)) throw new InvalidOperationException(o.Name + " is not a Field");
                var c = (FieldObject)f.Clone(true);
                c.FieldFormat.CommonFormat.EnableSystemDefault = false;
                var tf = c.FieldFormat.TimeFormat;
                tf.TimeBase = CrTimeBaseEnum.crTimeBase24Hour;
                tf.HourFormat = CrHourFormatEnum.crHourFormatNumericHour;
                tf.MinuteFormat = CrMinuteFormatEnum.crMinuteFormatNumericMinute;
                tf.SecondFormat = CrSecondFormatEnum.crSecondFormatNumericSecond;
                tf.HourMinuteSeparator = ":";
                tf.MinuteSecondSeparator = ":";
                tf.AMString = ""; tf.PMString = "";
                doc.ReportDefController.ReportObjectController.Modify(f, c);
                Console.WriteLine($"  {s.Name}.{o.Name} 24-hour");
            }
        }

        // Main report: appends string columns ("name:length") to the cached field list of the table
        // with the given alias, so objects can be bound to columns a procedure has since gained.
        // Under Report Navigator's push model the data arrives by column NAME, so the cached list
        // only has to know the name and type.
        static void AddColumns(ISCDReportClientDocument doc, string alias, string[] cols)
        {
            CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable table = null;
            foreach (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable t in doc.Database.Tables)
                if (t.Alias == alias) table = t;
            if (table == null) throw new InvalidOperationException("No table " + alias);
            var clone = (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable)((CrystalDecisions.ReportAppServer.DataDefModel.Table)table).Clone(true);
            foreach (var spec in cols)
            {
                var p = spec.Split(':');
                var f = new CrystalDecisions.ReportAppServer.DataDefModel.DBFieldClass();
                f.Name = p[0]; f.Type = CrystalDecisions.ReportAppServer.DataDefModel.CrFieldValueTypeEnum.crFieldValueTypeStringField;
                f.Length = int.Parse(p[1]);
                f.TableAlias = alias;
                clone.DataFields.Add(f);
            }
            // A report keeps its user name but never its password; add it to a COPY of the
            // connection, as the layout worker does. Never printed - any error text is scrubbed.
            var password = Environment.GetEnvironmentVariable("VIBEY_DB_PASSWORD");
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Set VIBEY_DB_PASSWORD first.");
            var conn = (CrystalDecisions.ReportAppServer.DataDefModel.ConnectionInfo)((CrystalDecisions.ReportAppServer.DataDefModel.ConnectionInfo)table.ConnectionInfo).Clone(true);
            conn.Password = password;
            clone.ConnectionInfo = conn;
            try { doc.DatabaseController.SetTableLocation(table, clone); }
            catch (Exception ex) { throw new InvalidOperationException(ex.Message.Replace(password, "********")); }
            int n = 0;
            foreach (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable t in doc.Database.Tables)
                if (t.Alias == alias) n = t.DataFields.Count;
            Console.WriteLine($"  {alias}: {n} fields after adding {string.Join(", ", cols)}");
        }

        // Main report: moves every field object and every group from table alias `from` to table
        // alias `to` (a second copy of the same procedure added by addTable, which is the only way
        // to make the report see columns the procedure has since gained), removes `from`, and
        // renames `to` back to `from`. Objects are MODIFIED, not rebuilt, so no formatting is lost.
        static void Rebind(ISCDReportClientDocument doc, string from, string to)
        {
            CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable target = null;
            foreach (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable t in doc.Database.Tables)
                if (t.Alias == to) target = t;
            if (target == null) throw new InvalidOperationException("No table " + to);
            CrystalDecisions.ReportAppServer.DataDefModel.Field FieldIn(string name)
            {
                foreach (CrystalDecisions.ReportAppServer.DataDefModel.Field f in target.DataFields)
                    if (f.Name == name) return f;
                throw new InvalidOperationException($"{to} has no column {name}");
            }
            string prefix = "{" + from + ".";
            // MEASURED: Modify refuses a new data source ("Cannot change the data source of field
            // objects"). So each field is replaced: an exact clone bound to the new table is ADDED
            // to the same section, then the original removed. Collected first - the section's
            // object collection must not change under the loop.
            var work = new System.Collections.Generic.List<(Section s, FieldObject fo)>();
            foreach (Area a in doc.ReportDefinition.Areas)
            foreach (Section s in a.Sections)
            foreach (ReportObject o in s.ReportObjects)
            {
                if (o is FieldObject fo && fo.DataSourceName != null && fo.DataSourceName.StartsWith(prefix))
                    work.Add((s, fo));
                else if (o is TextObject to2)
                {
                    foreach (Paragraph p in to2.Paragraphs)
                    foreach (ParagraphElement e in p.ParagraphElements)
                        if (e is ParagraphFieldElement pf && pf.DataSource != null && pf.DataSource.StartsWith(prefix))
                            throw new InvalidOperationException($"{o.Name} embeds {pf.DataSource} - not handled");
                }
            }
            int moved = 0;
            var roc = doc.ReportDefController.ReportObjectController;
            foreach (var (s, fo) in work)
            {
                var col = fo.DataSourceName.Substring(prefix.Length).TrimEnd('}');
                var c = (FieldObject)fo.Clone(true);
                c.DataSourceName = FieldIn(col).FormulaForm;
                string name = fo.Name;
                roc.Remove(fo);
                c.Name = name;
                roc.Add(c, s, -1);
                moved++;
            }
            Console.WriteLine($"  {moved} field object(s) moved from {from} to {to}");

            var gc = doc.DataDefController.GroupController;
            var groups = doc.DataDefinition.Groups;
            for (int i = 0; i < groups.Count; i++)
            {
                var g = groups[i];
                var cf = (CrystalDecisions.ReportAppServer.DataDefModel.Field)g.ConditionField;
                if (cf.FormulaForm == null || !cf.FormulaForm.StartsWith(prefix)) continue;
                var ng = (CrystalDecisions.ReportAppServer.DataDefModel.Group)g.Clone(true);
                ng.ConditionField = FieldIn(cf.Name);
                gc.Modify(g, ng);
                Console.WriteLine($"  group {i} on {cf.Name} moved to {to}");
            }

            doc.DatabaseController.RemoveTable(from);
            foreach (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable t in doc.Database.Tables)
                if (t.Alias == to) doc.DatabaseController.ModifyTableAlias(t, from);
            Console.WriteLine($"  removed {from}; {to} renamed to {from}");
        }

        // Sub-report: refreshes the cached column list of its (single) table after the procedure
        // behind it has gained columns - the addTable + rebind route, done inside the sub-report.
        // The same procedure is added under a temporary alias (Crystal reads its columns from the
        // server), every field is replaced by an identical clone bound to it, the old table is
        // removed and the temporary alias renamed back. Needs VIBEY_DB_PASSWORD.
        static void SubRefresh(ISCDReportClientDocument doc, string subName)
        {
            var sub = (ISCRSubreportClientDocument)doc.SubreportController.GetSubreport(subName);
            var tables = sub.DataDefController.Database.Tables;
            if (tables.Count != 1) throw new InvalidOperationException($"{subName}: expected one table, found {tables.Count}");
            var source = (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable)tables[0];
            string alias = source.Alias, tmp = alias + "_refresh";

            var password = Environment.GetEnvironmentVariable("VIBEY_DB_PASSWORD");
            if (string.IsNullOrEmpty(password)) throw new InvalidOperationException("Set VIBEY_DB_PASSWORD first.");
            CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable table =
                source is CrystalDecisions.ReportAppServer.DataDefModel.ISCRProcedure
                    ? (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable)new CrystalDecisions.ReportAppServer.DataDefModel.ProcedureClass()
                    : new CrystalDecisions.ReportAppServer.DataDefModel.TableClass();
            var conn = (CrystalDecisions.ReportAppServer.DataDefModel.ConnectionInfo)((CrystalDecisions.ReportAppServer.DataDefModel.ConnectionInfo)source.ConnectionInfo).Clone(true);
            conn.Password = password;
            table.ConnectionInfo = conn;
            table.Name = source.Name;
            table.Alias = tmp;
            table.QualifiedName = source.QualifiedName;
            try { sub.DatabaseController.AddTable(table, null); }
            catch (Exception ex) { throw new InvalidOperationException(ex.Message.Replace(password, "********")); }

            CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable added = null;
            foreach (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable t in sub.DataDefController.Database.Tables)
                if (t.Alias == tmp) added = t;
            CrystalDecisions.ReportAppServer.DataDefModel.Field FieldIn(string name)
            {
                foreach (CrystalDecisions.ReportAppServer.DataDefModel.Field f in added.DataFields)
                    if (f.Name == name) return f;
                throw new InvalidOperationException($"{tmp} has no column {name}");
            }

            string prefix = "{" + alias + ".";
            var ctl = sub.ReportDefController;
            var work = new System.Collections.Generic.List<(Section s, FieldObject fo)>();
            foreach (Area a in ctl.ReportDefinition.Areas)
            foreach (Section s in a.Sections)
            foreach (ReportObject o in s.ReportObjects)
                if (o is FieldObject fo && fo.DataSourceName != null && fo.DataSourceName.StartsWith(prefix))
                    work.Add((s, fo));
            foreach (var (s, fo) in work)
            {
                var col = fo.DataSourceName.Substring(prefix.Length).TrimEnd('}');
                var c = (FieldObject)fo.Clone(true);
                c.DataSourceName = FieldIn(col).FormulaForm;
                string name = fo.Name;
                ctl.ReportObjectController.Remove(fo);
                c.Name = name;
                ctl.ReportObjectController.Add(c, s, -1);
            }
            if (sub.DataDefController.Database.Tables.Count > 0 && sub.DataDefController.DataDefinition.Groups.Count > 0)
                throw new InvalidOperationException($"{subName}: has groups - not handled");
            sub.DatabaseController.RemoveTable(alias);
            foreach (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable t in sub.DataDefController.Database.Tables)
                if (t.Alias == tmp) sub.DatabaseController.ModifyTableAlias(t, alias);
            int n = 0;
            foreach (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable t in sub.DataDefController.Database.Tables) n = t.DataFields.Count;
            Console.WriteLine($"  {subName}: {work.Count} field(s) rebound; {alias} now has {n} columns");
        }

        // Sub-report: re-binds one field object to another column of the same table - an exact
        // clone (geometry, font, alignment, can-grow) bound to the new column replaces it.
        static void SubSwap(ISCDReportClientDocument doc, string subName, string objName, string column)
        {
            var sub = (ISCRSubreportClientDocument)doc.SubreportController.GetSubreport(subName);
            var table = (CrystalDecisions.ReportAppServer.DataDefModel.ISCRTable)sub.DataDefController.Database.Tables[0];
            CrystalDecisions.ReportAppServer.DataDefModel.Field col = null;
            foreach (CrystalDecisions.ReportAppServer.DataDefModel.Field f in table.DataFields) if (f.Name == column) col = f;
            if (col == null) throw new InvalidOperationException($"{table.Alias} has no column {column}");
            var ctl = sub.ReportDefController;
            foreach (Area a in ctl.ReportDefinition.Areas)
            foreach (Section s in a.Sections)
            foreach (ReportObject o in s.ReportObjects)
            {
                if (o.Name != objName) continue;
                var fo = (FieldObject)o;
                var c = (FieldObject)fo.Clone(true);
                c.DataSourceName = col.FormulaForm;
                c.FieldValueType = col.Type;
                ctl.ReportObjectController.Remove(fo);
                c.Name = objName;
                ctl.ReportObjectController.Add(c, s, -1);
                Console.WriteLine($"  {subName}.{objName}: {fo.DataSourceName} -> {col.FormulaForm} ({col.Type})");
                return;
            }
            throw new InvalidOperationException($"{subName} has no object {objName}");
        }

        // Main report: "keep together" on the named sections - the section moves whole to the next
        // page rather than splitting a banner from its table. The worker has no operation for it.
        static void KeepTogether(ISCDReportClientDocument doc, string[] names)
        {
            var wanted = new System.Collections.Generic.HashSet<string>(names);
            foreach (Area a in doc.ReportDefinition.Areas)
            foreach (Section s in a.Sections)
            {
                if (!wanted.Remove(s.Name)) continue;
                var fmt = (SectionFormat)s.Format.Clone(true);
                fmt.EnableKeepTogether = true;
                doc.ReportDefController.ReportSectionController.SetProperty(s, CrReportSectionPropertyEnum.crReportSectionPropertyFormat, fmt);
                Console.WriteLine($"  {s.Name}: keep together (was {s.Format.EnableKeepTogether})");
            }
            if (wanted.Count > 0) throw new InvalidOperationException("Not found: " + string.Join(", ", wanted));
        }

        static void Dump(ISCDReportClientDocument doc, string only)
        {
            var names = doc.SubreportController.GetSubreportNames();
            for (int i = 0; i < names.Count; i++)
            {
                string name = names[i];
                if (only != null && name != only) continue;
                var ctl = ((ISCRSubreportClientDocument)doc.SubreportController.GetSubreport(name)).ReportDefController;
                Console.WriteLine("=== " + name);
                foreach (Area a in ctl.ReportDefinition.Areas)
                foreach (Section s in a.Sections)
                {
                    Console.WriteLine($"  [{s.Name}] h={s.Height} supp={s.Format.EnableSuppress} bg={Col(s.Format.BackgroundColor)} keep={s.Format.EnableKeepTogether}");
                    foreach (ReportObject o in s.ReportObjects)
                    {
                        string extra;
                        if (o is LineObject l)
                            extra = $" LINE {l.Left},{l.Top}->{l.Right},{l.Bottom} style={l.LineStyle} thick={l.LineThickness} col={Col(l.LineColor)} ext={l.EnableExtendToBottomOfSection} end={l.EndSectionName}";
                        else if (o is BoxObject x)
                            extra = $" BOX {x.Left},{x.Top}->{x.Right},{x.Bottom} style={x.LineStyle} thick={x.LineThickness} col={Col(x.LineColor)} fill={Col(x.FillColor)} ext={x.EnableExtendToBottomOfSection}";
                        else
                        {
                            var b = o.Border;
                            extra = $" {o.Left},{o.Top} {o.Width}x{o.Height} grow={o.Format.EnableCanGrow} border[L{(int)b.LeftLineStyle} R{(int)b.RightLineStyle} T{(int)b.TopLineStyle} B{(int)b.BottomLineStyle} {Col(b.BorderColor)} bg={Col(b.BackgroundColor)}]";
                        }
                        string txt = "";
                        if (o is FieldObject f) txt = " {" + f.DataSourceName + "}";
                        if (o is TextObject t)
                            foreach (Paragraph p in t.Paragraphs)
                            foreach (ParagraphElement e in p.ParagraphElements)
                                if (e is ParagraphTextElement pt) txt += " '" + pt.Text + "'";
                        string font = "";
                        try { var fc = (o as FieldObject)?.FontColor ?? (o as TextObject)?.FontColor; if (fc != null && fc.Font != null) font = $" font={fc.Font.Name} {fc.Font.Size}{(fc.Font.Bold ? "b" : "")} col={Col(fc.Color)}"; } catch { }
                        Console.WriteLine($"    {o.Name} {o.Kind}{extra}{txt}{font}");
                    }
                }
            }
        }
    }
}
