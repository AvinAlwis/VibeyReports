using System;
using System.IO;
using CrystalDecisions.CrystalReports.Engine;
using CrystalDecisions.ReportAppServer.ClientDoc;

namespace VibeyReports.CrystalWorker
{
    /// <summary>
    /// Owns one open report. All Crystal XI R2 interaction goes through here.
    /// </summary>
    /// <remarks>
    /// We open through the classic engine (<see cref="ReportDocument"/>) and take the in-process RAS
    /// bridge off it (<see cref="ReportDocument.ReportClientDocument"/>), rather than constructing
    /// <c>ReportClientDocument</c> directly. Constructing it directly activates the standalone RAS COM
    /// server, which attempts a same-machine TCP self-connect on port 1566 that hangs indefinitely in
    /// this environment (COMException: "Failed to connect to server &lt;hostname&gt;") - see
    /// task-4-report.md for the full diagnosis. The engine-owned bridge costs ~2ms and makes no network
    /// connection; the full RAS object model (ReportDefController, SaveAs, etc.) is reachable through it.
    /// This is also how D:\RptToXml opens reports.
    /// </remarks>
    public sealed class CrystalSession : IDisposable
    {
        private ReportDocument _engineDoc;
        private bool _disposed;
        private bool _faulted;

        private CrystalSession(ReportDocument engineDoc, string sourcePath)
        {
            _engineDoc = engineDoc;
            SourcePath = sourcePath;
        }

        public string SourcePath { get; }

        public ISCDReportClientDocument Document
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(CrystalSession));
                return _engineDoc.ReportClientDocument;
            }
        }

        /// <summary>
        /// Marks this session as holding a partially-applied plan. A faulted session refuses to save,
        /// because its document no longer corresponds to any plan the caller asked for.
        /// </summary>
        public void MarkFaulted() => _faulted = true;

        public bool IsFaulted => _faulted;

        public static CrystalSession Open(string rptPath)
        {
            if (string.IsNullOrWhiteSpace(rptPath)) throw new ArgumentException("Report path is required.", nameof(rptPath));

            var full = Path.GetFullPath(rptPath);
            if (!File.Exists(full)) throw new FileNotFoundException($"Report not found: {full}", full);

            var engineDoc = new ReportDocument();
            // NOT new ReportClientDocument() - see the class remarks above.
            engineDoc.Load(full);

            return new CrystalSession(engineDoc, full);
        }

        public void SaveAs(string destinationPath, bool overwrite)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CrystalSession));
            if (_faulted)
                throw new InvalidOperationException(
                    "This session holds a partially-applied layout plan and cannot be saved. " +
                    "Re-open the report and re-apply a corrected plan.");
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", nameof(destinationPath));

            var full = Path.GetFullPath(destinationPath);

            if (string.Equals(full, SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Refusing to overwrite the source report: {full}");

            if (File.Exists(full) && !overwrite)
                throw new IOException($"Destination already exists: {full}. Pass overwrite=true to replace it.");

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            // F5 (final review): save to a temporary name in the same directory first, and only
            // move it over the real destination once the COM save has actually succeeded. The
            // previous implementation deleted an existing destination BEFORE calling
            // Document.SaveAs, so a save that failed partway through (a locked file, a disk error,
            // Crystal itself throwing) left the caller with ok:false while the previous good output
            // had already been destroyed -- the one hole in "ok:false implies nothing was written",
            // and the documented iterate-in-place workflow (write each round back to the same
            // outputPath with overwrite:true) makes hitting it a normal occurrence, not an edge
            // case. Both guards above (never the source; refuse an existing destination unless
            // overwrite:true) still run first and are unchanged.
            var tempName = $".vibey-tmp-{Guid.NewGuid():N}{Path.GetExtension(full)}";
            var tempFull = Path.Combine(dir, tempName);

            try
            {
                // SaveAs(name, directory, options). 0 = crReportOptionDefault.
                Document.SaveAs(tempName, dir, 0);

                if (File.Exists(full)) File.Delete(full);
                File.Move(tempFull, full);
            }
            catch
            {
                if (File.Exists(tempFull)) File.Delete(tempFull);
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_engineDoc != null && _engineDoc.IsLoaded) _engineDoc.Close();
            }
            catch
            {
                // A failed Close must not mask the real error from the caller.
            }

            _engineDoc = null;
        }
    }
}
