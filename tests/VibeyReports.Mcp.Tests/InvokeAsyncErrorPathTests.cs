using System.Diagnostics;
using FluentAssertions;
using VibeyReports.Contracts;
using VibeyReports.Mcp;
using Xunit;

namespace VibeyReports.Mcp.Tests;

/// <summary>
/// round-1 fix T2: the timeout/kill, malformed-JSON, and ExitCode != 0 branches of
/// CrystalWorkerClient.InvokeAsync had zero coverage. These decide whether Claude gets a useful
/// message or a mystery, so they are exercised here against VibeyReports.FakeWorker - a tiny
/// deterministic stand-in keyed off a keyword in reportPath - rather than against the real,
/// slow, COM-backed worker.
/// </summary>
public class InvokeAsyncErrorPathTests
{
    private static string FakeWorkerPath
    {
        get
        {
            const string exeName = "VibeyReports.FakeWorker.exe";
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8 && dir is not null; i++)
            {
                var candidateDir = Path.Combine(dir, "tests", "VibeyReports.FakeWorker");
                if (Directory.Exists(candidateDir))
                {
                    var candidate = Directory.GetFiles(candidateDir, exeName, SearchOption.AllDirectories)
                        .OrderByDescending(File.GetLastWriteTimeUtc)
                        .FirstOrDefault();
                    if (candidate is not null) return candidate;
                }
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
            }

            throw new FileNotFoundException($"Could not find {exeName}. Build tests/VibeyReports.FakeWorker first.");
        }
    }

    [Fact]
    public async Task InvokeAsync_KillsAndReportsATimeoutForAHungWorker()
    {
        var client = new CrystalWorkerClient(FakeWorkerPath, timeout: TimeSpan.FromSeconds(2));

        var response = await client.ReadAsync(@"C:\reports\HANG.rpt", CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error.Should().ContainEquivalentOf("did not finish");

        // The client's Kill(entireProcessTree: true) is fire-and-forget from InvokeAsync's point of
        // view (stdoutTask/stderrTask are deliberately left unawaited afterwards - a known, deferred
        // gap), so allow the OS a brief moment to actually tear the process down before asserting it
        // is gone rather than orphaned.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (Process.GetProcessesByName("VibeyReports.FakeWorker").Length > 0 && DateTime.UtcNow < deadline)
            await Task.Delay(200);

        Process.GetProcessesByName("VibeyReports.FakeWorker").Should().BeEmpty(
            "the hung worker process should be killed, not left orphaned");
    }

    [Fact]
    public async Task InvokeAsync_ReturnsAUsefulFailureWhenTheWorkerWritesUnparsableOutput()
    {
        var client = new CrystalWorkerClient(FakeWorkerPath);

        var response = await client.ReadAsync(@"C:\reports\GARBAGE.rpt", CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error.Should().Contain("Could not parse");
        response.Error.Should().Contain("not json at all");
    }

    [Fact]
    public async Task InvokeAsync_ReturnsAUsefulFailureWhenTheWorkerExitsNonZero()
    {
        var client = new CrystalWorkerClient(FakeWorkerPath);

        var response = await client.ReadAsync(@"C:\reports\CRASH.rpt", CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error.Should().Contain("Exit code 3");
    }

    /// <summary>
    /// Final review F6: previously, await process.StandardInput.WriteAsync(...) was not passed the
    /// cancellation token, so a worker that hangs BEFORE draining stdin could block that write
    /// forever on a payload larger than the pipe's buffer -- the timeout only ever applied to
    /// WaitForExitAsync afterwards, which the write never reached. VibeyReports.FakeWorker's
    /// VIBEY_FAKEWORKER_HANG_BEFORE_READ mode never reads stdin at all, and reportPath here is
    /// padded well past any realistic OS pipe buffer size to force the write to actually block
    /// absent the fix.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_TimesOutRatherThanHangingWhenTheWorkerNeverDrainsStdinOnALargeRequest()
    {
        var client = new CrystalWorkerClient(FakeWorkerPath, timeout: TimeSpan.FromSeconds(2));
        var hugeReportPath = @"C:\reports\" + new string('x', 8 * 1024 * 1024) + ".rpt";

        Environment.SetEnvironmentVariable("VIBEY_FAKEWORKER_HANG_BEFORE_READ", "1");
        Task<WorkerResponse> responseTask;
        try
        {
            responseTask = client.ReadAsync(hugeReportPath, CancellationToken.None);
        }
        finally
        {
            Environment.SetEnvironmentVariable("VIBEY_FAKEWORKER_HANG_BEFORE_READ", null);
        }

        var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(15));

        response.Ok.Should().BeFalse();
        response.Error.Should().ContainEquivalentOf("did not finish");
    }

    /// <summary>
    /// Final review F6: Process.Start throws (rather than returning null) when the target
    /// executable does not exist - the same failure mode as the worker being deleted or blocked
    /// after WorkerLocator's own startup probe succeeded. Before the fix, this propagated as a raw
    /// Win32Exception with no catch-all in ReportTools to turn it into the {"ok":false,...} shape
    /// every tool description promises.
    /// </summary>
    [Fact]
    public async Task InvokeAsync_ReturnsOkFalseRatherThanThrowingWhenTheWorkerExecutableDoesNotExist()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"vibey_missing_{Guid.NewGuid():N}.exe");
        var client = new CrystalWorkerClient(missingPath);

        var response = await client.ReadAsync(@"C:\reports\whatever.rpt", CancellationToken.None);

        response.Ok.Should().BeFalse();
        response.Error.Should().NotBeNullOrWhiteSpace();
    }
}
