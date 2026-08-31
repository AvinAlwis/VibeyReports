using System;
using System.IO;
using CrystalDecisions.ReportAppServer.ClientDoc;

namespace VibeyReports.CrystalWorker
{
    /// <summary>
    /// Owns one open ReportClientDocument. All Crystal XI R2 interaction goes through here.
    /// </summary>
    public sealed class CrystalSession : IDisposable
    {
        private ReportClientDocument _doc;
        private bool _disposed;

        private CrystalSession(ReportClientDocument doc, string sourcePath)
        {
            _doc = doc;
            SourcePath = sourcePath;
        }

        public string SourcePath { get; }

        public ISCDReportClientDocument Document
        {
            get
            {
                if (_disposed) throw new ObjectDisposedException(nameof(CrystalSession));
                return _doc;
            }
        }

        public static CrystalSession Open(string rptPath)
        {
            if (string.IsNullOrWhiteSpace(rptPath)) throw new ArgumentException("Report path is required.", nameof(rptPath));

            var full = Path.GetFullPath(rptPath);
            if (!File.Exists(full)) throw new FileNotFoundException($"Report not found: {full}", full);

            var doc = new ReportClientDocument();
            // Keep the document open until we dispose it explicitly.
            // NOTE: the brief's ground truth says AutoClose is exposed via get_AutoClose/set_AutoClose
            // accessor methods, and to call doc.set_AutoClose(false) if the property assignment does not
            // compile. In practice it is the other way around here: the interop layer synthesizes a real
            // C# property named AutoClose from those accessor methods, so calling set_AutoClose(...)
            // directly is rejected by the compiler (CS0571 - cannot explicitly call operator or accessor).
            // The property assignment is what actually compiles.
            doc.AutoClose = false;
            // 0 = default open options.
            doc.Open(full, 0);

            return new CrystalSession(doc, full);
        }

        public void SaveAs(string destinationPath, bool overwrite)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CrystalSession));
            if (string.IsNullOrWhiteSpace(destinationPath)) throw new ArgumentException("Destination path is required.", nameof(destinationPath));

            var full = Path.GetFullPath(destinationPath);

            if (string.Equals(full, SourcePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException($"Refusing to overwrite the source report: {full}");

            if (File.Exists(full) && !overwrite)
                throw new IOException($"Destination already exists: {full}. Pass overwrite=true to replace it.");

            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(full)) File.Delete(full);

            // SaveAs(name, directory, options). 0 = crReportOptionDefault.
            _doc.SaveAs(Path.GetFileName(full), dir, 0);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try
            {
                if (_doc != null && _doc.IsOpen) _doc.Close();
            }
            catch
            {
                // A failed Close must not mask the real error from the caller.
            }

            _doc = null;
        }
    }
}
