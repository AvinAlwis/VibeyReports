using FluentAssertions;
using VibeyReports.Mcp;
using Xunit;

namespace VibeyReports.Mcp.Tests;

/// <summary>
/// round-1 fix T1: WorkerLocator_FindsTheCrystalWorkerExecutable (in CrystalWorkerClientTests) only
/// proves Find() returns *some* valid path, and per the round-1 findings it was actually satisfied by
/// the dev-mode walk-up fallback in every observed run - never by the worker/ subdirectory probe added
/// for the Task 11 published layout. These tests isolate each probe so a broken or mis-ordered one
/// would fail here even though the happy-path test above stays green.
/// </summary>
public class WorkerLocatorTests
{
    [Fact]
    public void Find_WithBaseDirectory_UsesTheWorkerSubdirectoryProbeWhenItIsTheOnlyCandidate()
    {
        // A directory with nothing side-by-side and nothing discoverable by walking up (it lives
        // under the OS temp folder, far from any "VibeyReports.CrystalWorker" or "src" directory),
        // so only the worker/ subdirectory probe added in C4 can possibly satisfy this call.
        var baseDir = Path.Combine(Path.GetTempPath(), $"vibey_locator_{Guid.NewGuid():N}");
        var workerDir = Path.Combine(baseDir, "worker");
        Directory.CreateDirectory(workerDir);
        var stub = Path.Combine(workerDir, "VibeyReports.CrystalWorker.exe");
        File.WriteAllText(stub, "stub");

        try
        {
            var found = WorkerLocator.Find(baseDir);

            found.Should().Be(stub);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Find_WithBaseDirectory_PrefersSideBySideOverTheWorkerSubdirectory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"vibey_locator_{Guid.NewGuid():N}");
        var workerDir = Path.Combine(baseDir, "worker");
        Directory.CreateDirectory(workerDir);
        var sideBySide = Path.Combine(baseDir, "VibeyReports.CrystalWorker.exe");
        var published = Path.Combine(workerDir, "VibeyReports.CrystalWorker.exe");
        File.WriteAllText(sideBySide, "stub");
        File.WriteAllText(published, "stub");

        try
        {
            var found = WorkerLocator.Find(baseDir);

            found.Should().Be(sideBySide);
        }
        finally { Directory.Delete(baseDir, recursive: true); }
    }

    [Fact]
    public void Find_EnvironmentVariable_WinsOverEveryOtherProbe()
    {
        // A base directory that would satisfy the side-by-side probe on its own, to prove the
        // environment variable is checked first and short-circuits everything else.
        var baseDir = Path.Combine(Path.GetTempPath(), $"vibey_locator_{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        var sideBySide = Path.Combine(baseDir, "VibeyReports.CrystalWorker.exe");
        File.WriteAllText(sideBySide, "stub");

        var overrideDir = Path.Combine(Path.GetTempPath(), $"vibey_locator_override_{Guid.NewGuid():N}");
        Directory.CreateDirectory(overrideDir);
        var overridden = Path.Combine(overrideDir, "some-other-name.exe");
        File.WriteAllText(overridden, "stub");

        var original = Environment.GetEnvironmentVariable(WorkerLocator.OverrideVariable);
        Environment.SetEnvironmentVariable(WorkerLocator.OverrideVariable, overridden);
        try
        {
            // Find() (no argument) is the public entry point; it checks VIBEY_WORKER_PATH before
            // ever consulting AppContext.BaseDirectory, so baseDir's side-by-side stub must lose.
            var found = WorkerLocator.Find();

            found.Should().Be(Path.GetFullPath(overridden));
        }
        finally
        {
            Environment.SetEnvironmentVariable(WorkerLocator.OverrideVariable, original);
            Directory.Delete(baseDir, recursive: true);
            Directory.Delete(overrideDir, recursive: true);
        }
    }

    [Fact]
    public void Find_EnvironmentVariable_PointingAtAMissingFile_FailsLoudlyRatherThanFallingThrough()
    {
        var original = Environment.GetEnvironmentVariable(WorkerLocator.OverrideVariable);
        Environment.SetEnvironmentVariable(WorkerLocator.OverrideVariable, @"C:\definitely\not\a-worker.exe");
        try
        {
            Action act = () => WorkerLocator.Find();

            act.Should().Throw<FileNotFoundException>()
                .WithMessage($"*{WorkerLocator.OverrideVariable}*");
        }
        finally { Environment.SetEnvironmentVariable(WorkerLocator.OverrideVariable, original); }
    }
}
