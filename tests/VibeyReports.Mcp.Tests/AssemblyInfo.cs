using Xunit;

// round-1 fix T1/T2: several tests in this assembly mutate the process-wide VIBEY_WORKER_PATH
// environment variable (WorkerLocatorTests) or start slow/hanging child processes with short
// timeouts (InvokeAsyncErrorPathTests). xunit parallelizes across test classes by default, and a
// stray environment-variable write from one class racing a read in another would be a source of
// flaky, hard-to-reproduce failures. Disabling parallelization keeps every test in this assembly
// deterministic at the cost of some wall-clock time, which is an acceptable trade for a suite this
// small.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

// round-1 fix F4 (follow-through): marking VibeyReports.Mcp.PdfRasterizer's assembly
// [SupportedOSPlatform("windows")] correctly terminates the CA1416 chain there, but by design that
// annotation then propagates to every *caller* of that assembly's APIs that isn't itself marked as
// Windows-only - which is every test in this project, since they all call into ReportTools,
// CrystalWorkerClient or WorkerLocator. This project is Windows-only for the same reason
// (VibeyReports.Mcp launches an x86 .NET Framework COM worker), so the chain terminates here too.
[assembly: System.Runtime.Versioning.SupportedOSPlatform("windows")]
