using System;
using System.IO;
using System.Text;
using System.Text.Json;
using VibeyReports.Contracts;

namespace VibeyReports.CrystalWorker
{
    /// <summary>
    /// The worker's real entry point (Task 8). Reads exactly one JSON <see cref="WorkerRequest"/>
    /// from stdin and writes exactly one JSON <see cref="WorkerResponse"/> to stdout - nothing else
    /// ever touches stdout. Every diagnostic (exception detail, trace) goes to stderr instead, so a
    /// parent process (Task 9's CrystalWorkerClient) can always deserialize stdout without having to
    /// separate signal from noise.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main()
        {
            // task-8-supplement.md C1 (Critical): Encoding.UTF8 is constructed with
            // encoderShouldEmitUTF8Identifier: true, so assigning it to Console.OutputEncoding can
            // emit a UTF-8 byte-order mark (EF BB BF) ahead of the JSON. A leading BOM makes
            // JsonSerializer.Deserialize throw on the client side ('0xEF' is an invalid start of a
            // value) - a failure that looks exactly like a worker crash across a process boundary.
            // A BOM-less UTF8Encoding avoids it.
            var bomless = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = bomless;

            WorkerResponse response;
            try
            {
                // round-1 fix F1: Console.In.ReadToEnd() decodes using Console.InputEncoding, which
                // defaults to the OEM/ANSI code page on Windows - a different encoding from the
                // BOM-less UTF-8 the client writes and this process writes back out on stdout. Rather
                // than assigning Console.InputEncoding (which can throw when stdin is redirected, e.g.
                // "The handle is invalid"), read the raw stdin stream directly and decode it with the
                // same BOM-less UTF-8 used everywhere else in this protocol.
                string raw;
                using (var stdin = Console.OpenStandardInput())
                using (var reader = new StreamReader(stdin, bomless))
                {
                    raw = reader.ReadToEnd();
                }
                var request = JsonSerializer.Deserialize<WorkerRequest>(raw, VibeyJson.Options);
                if (request == null) throw new InvalidOperationException("Empty request.");

                response = Handle(request);
            }
            catch (Exception ex)
            {
                // Diagnostics go to stderr only - stdout is reserved for the single JSON response.
                Console.Error.WriteLine(ex.ToString());
                response = WorkerResponse.Failure(ex.Message);
            }

            Console.Out.Write(JsonSerializer.Serialize(response, VibeyJson.Options));
            Console.Out.Flush();

            // Exit code 0 whenever a JSON response was produced, including ok:false. Non-zero would
            // signal the worker crashed before it could answer at all, which is not what happened
            // here even on a failure path.
            return 0;
        }

        private static WorkerResponse Handle(WorkerRequest request)
        {
            switch (request.Command)
            {
                case WorkerCommands.Read:
                    using (var session = CrystalSession.Open(request.ReportPath))
                    {
                        var response = WorkerResponse.Success();
                        response.Schema = ReportReader.Read(session);
                        return response;
                    }

                case WorkerCommands.Apply:
                {
                    if (request.Plan == null) return WorkerResponse.Failure("\"apply\" requires a \"plan\".");
                    if (string.IsNullOrWhiteSpace(request.OutputPath)) return WorkerResponse.Failure("\"apply\" requires an \"outputPath\".");

                    using (var session = CrystalSession.Open(request.ReportPath))
                    {
                        LayoutApplier.ApplyResult applyResult;
                        try
                        {
                            // The ONLY entry point into LayoutApplier. Apply validates the plan
                            // against the report's current schema before it mutates anything; the
                            // internal ApplyOperationsWithoutValidation seam that skips validation
                            // is never called here (task-8-supplement.md C3).
                            applyResult = LayoutApplier.Apply(session, request.Plan);
                        }
                        catch (LayoutApplier.InvalidPlanException ex)
                        {
                            // Validation failed before any mutation touched the document: the
                            // session is not faulted and nothing has been written.
                            return WorkerResponse.Failure("Layout plan failed validation.", ex.Result);
                        }

                        // Read the resulting schema BEFORE saving (round-1 fix F1). ReportReader.Read
                        // can throw on a freshly-added object read back without a reopen - this is
                        // the same RAS quirk LayoutApplierTests' ApplyAndReread helper works around by
                        // closing and reopening. Reading after SaveAs would risk "ok:false" while a
                        // real .rpt already sits on disk, a state the protocol has no way to express
                        // and Task 9's client cannot distinguish from a clean failure. Reading first
                        // makes "ok:false implies nothing was written" structurally true.
                        var schema = ReportReader.Read(session);

                        // If a mid-plan failure had occurred, LayoutApplier would have marked the
                        // session faulted and thrown before reaching this line; that exception is not
                        // caught here, so it propagates to Main's outer catch, and SaveAs is never
                        // reached - no output file is written (task-8-supplement.md C4).
                        session.SaveAs(request.OutputPath, request.Overwrite);

                        var response = WorkerResponse.Success();
                        response.OperationsApplied = applyResult.OperationsApplied;
                        response.RemovedObjects = applyResult.RemovedObjects;
                        response.RemovedTables = applyResult.RemovedTables;
                        response.OutputPath = Path.GetFullPath(request.OutputPath);
                        // Final review F4: schema.ReportPath was ReportReader.Read's session.SourcePath
                        // (the SOURCE .rpt), even though apply_layout's own description warns at length
                        // against previewing/continuing from the source path instead of outputPath. Point
                        // the returned schema at the file that was actually written.
                        schema.ReportPath = response.OutputPath;
                        response.Schema = schema;
                        return response;
                    }
                }

                case WorkerCommands.Render:
                    using (var session = CrystalSession.Open(request.ReportPath))
                    {
                        var response = WorkerResponse.Success();
                        // ReportRenderer.ExportPdf's InvalidOperationException carries an actionable,
                        // report-specific message (task-8-supplement.md C6); it is intentionally not
                        // caught here so Main's outer catch surfaces ex.Message verbatim in
                        // WorkerResponse.Error instead of a generic string.
                        response.PdfBase64 = Convert.ToBase64String(ReportRenderer.ExportPdf(session));
                        return response;
                    }

                default:
                    return WorkerResponse.Failure(
                        $"\"{request.Command}\" is not a supported command. Supported: {string.Join(", ", WorkerCommands.All)}.");
            }
        }
    }
}
