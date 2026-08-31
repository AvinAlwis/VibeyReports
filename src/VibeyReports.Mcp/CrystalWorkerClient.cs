using System.Diagnostics;
using System.Text;
using System.Text.Json;
using VibeyReports.Contracts;

namespace VibeyReports.Mcp;

/// <summary>
/// Runs the x86 .NET Framework Crystal worker as a child process, one request per run.
/// One process per call keeps the legacy COM state from leaking between operations.
/// </summary>
public sealed class CrystalWorkerClient
{
    private readonly string _workerPath;
    private readonly TimeSpan _timeout;

    public CrystalWorkerClient(string workerPath, TimeSpan? timeout = null)
    {
        _workerPath = workerPath ?? throw new ArgumentNullException(nameof(workerPath));
        _timeout = timeout ?? TimeSpan.FromMinutes(3);
    }

    public Task<WorkerResponse> ReadAsync(string reportPath, CancellationToken ct) =>
        InvokeAsync(new WorkerRequest { Command = WorkerCommands.Read, ReportPath = reportPath }, ct);

    public Task<WorkerResponse> ApplyAsync(string reportPath, string outputPath, LayoutPlan plan, bool overwrite, CancellationToken ct) =>
        InvokeAsync(new WorkerRequest
        {
            Command = WorkerCommands.Apply,
            ReportPath = reportPath,
            OutputPath = outputPath,
            Overwrite = overwrite,
            Plan = plan
        }, ct);

    public Task<WorkerResponse> RenderAsync(string reportPath, CancellationToken ct) =>
        InvokeAsync(new WorkerRequest { Command = WorkerCommands.Render, ReportPath = reportPath }, ct);

    private async Task<WorkerResponse> InvokeAsync(WorkerRequest request, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(_workerPath)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(_workerPath)!
        };

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"Could not start the Crystal worker: {_workerPath}");

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(_timeout);

        await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request, VibeyJson.Options));
        process.StandardInput.Close();

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { /* already gone */ }
            return WorkerResponse.Failure($"The Crystal worker did not finish within {_timeout.TotalSeconds:F0}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(stdout))
            return WorkerResponse.Failure($"The Crystal worker exited without a response. Exit code {process.ExitCode}. stderr: {Truncate(stderr)}");

        try
        {
            return JsonSerializer.Deserialize<WorkerResponse>(stdout, VibeyJson.Options)
                   ?? WorkerResponse.Failure("The Crystal worker returned an empty JSON document.");
        }
        catch (JsonException ex)
        {
            return WorkerResponse.Failure($"Could not parse the worker's response: {ex.Message}. Output began: {Truncate(stdout)}");
        }
    }

    private static string Truncate(string s) =>
        string.IsNullOrEmpty(s) ? "(empty)" : s.Length <= 800 ? s : s[..800] + "…";
}
