using Xunit;

// Every test class in this assembly opens one of the shared .rpt files under tests/fixtures
// through CrystalSession.Open, and xunit parallelizes across test classes by default. Crystal does
// not tolerate that on a shared file: ReportRenderer's PrintOutputController.Export needs access
// the other classes' open sessions deny it, and the result is
//
//     COMException: The process cannot access the file because it is being used by another process.
//
// surfacing as ExportPdf_ProducesAFileEndingWithEof failing while passing in isolation. The race
// has always been latent -- the renderer tests and LayoutApplierTests have shared SampleReport.rpt
// since both were written -- but it only became likely once the formatting operations added a
// dozen more LayoutApplierTests holding that same fixture open.
//
// Disabling parallelization is the same remedy VibeyReports.Mcp.Tests already applies for its own
// shared-process-state races, and it is the honest one here: the contended resource is a file on
// disk plus a single-threaded COM stack, neither of which the tests can make concurrent-safe. The
// cost is wall-clock time on a suite that is dominated by Crystal's own open/save latency anyway.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
