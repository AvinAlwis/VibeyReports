using System;
using System.IO;
using System.Runtime.InteropServices;
using CrystalDecisions.ReportAppServer.ReportDefModel;

namespace VibeyReports.CrystalWorker
{
    public static class ReportRenderer
    {
        /// <summary>
        /// Exports the report to PDF. The report is rendered with whatever saved data or
        /// database connection it already carries; Vibey Reports never changes either.
        /// </summary>
        /// <remarks>
        /// VERIFIED (task-7-supplement.md, C1): <c>ISCRByteArray</c> has no <c>get_Stream()</c> —
        /// the brief's original stream plumbing does not compile. The RAS-returned
        /// <c>ByteArray</c> exposes the export bytes directly via its <c>ByteArray</c> property
        /// (<c>Byte[]</c>); there is no stream anywhere on the type. Reflection against the
        /// installed CrystalDecisions.ReportAppServer.CommonObjectModel.dll confirms
        /// <c>PrintOutputController.Export(...)</c> returns
        /// <c>CrystalDecisions.ReportAppServer.CommonObjectModel.ByteArray</c>, whose members are
        /// Count, ByteArray, getref_ByteArray(), Save(path, overwrite), AttachArray, DetachArray —
        /// no Stream-returning member exists.
        /// </remarks>
        public static byte[] ExportPdf(CrystalSession session)
        {
            if (session == null) throw new ArgumentNullException(nameof(session));

            try
            {
                var byteArray = session.Document.PrintOutputController.Export(
                    CrReportExportFormatEnum.crReportExportFormatPDF, 0);

                if (byteArray == null) throw new InvalidOperationException("Crystal returned no export data.");

                var bytes = byteArray.ByteArray;
                if (bytes == null || bytes.Length == 0)
                    throw new InvalidOperationException("Crystal returned an empty PDF export.");

                return bytes;
            }
            catch (COMException ex)
            {
                // VERIFIED (task-7-supplement.md, C2): a report that declares parameters with no
                // saved values (e.g. Documents.rpt) throws COMException: "Missing parameter
                // values." on Export. Vibey Reports never supplies parameter values (Global
                // Constraint 5), so this is an expected, non-fatal outcome: the report's layout
                // can still be read and edited, it just cannot be previewed. Wrap with context so
                // the failure is actionable when it surfaces through the MCP preview tool.
                throw new InvalidOperationException(
                    $"Could not render \"{Path.GetFileName(session.SourcePath)}\" to PDF: {ex.Message.Trim()} " +
                    "Reports that declare parameters with no saved values cannot be previewed, because Vibey Reports " +
                    "does not supply parameter values. The layout can still be read and edited.",
                    ex);
            }
        }
    }
}
