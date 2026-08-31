using System.Diagnostics;
using FluentAssertions;
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
}
