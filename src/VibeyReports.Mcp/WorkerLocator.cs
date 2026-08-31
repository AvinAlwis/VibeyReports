namespace VibeyReports.Mcp;

public static class WorkerLocator
{
    public const string OverrideVariable = "VIBEY_WORKER_PATH";
    private const string ExeName = "VibeyReports.CrystalWorker.exe";

    /// <summary>
    /// Finds the x86 Crystal worker. Honours VIBEY_WORKER_PATH, then looks next to this
    /// assembly, then in a "worker" subdirectory next to this assembly (the published layout —
    /// see Task 11: the net10 host and the net48 x86 worker publish to separate directories
    /// because they both carry VibeyReports.Contracts.dll and would otherwise clobber each
    /// other), then walks up looking for the worker's build output (for dev runs).
    /// </summary>
    public static string Find()
    {
        var overridden = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            if (!File.Exists(overridden))
                throw new FileNotFoundException($"{OverrideVariable} points at a file that does not exist: {overridden}", overridden);
            return Path.GetFullPath(overridden);
        }

        var sideBySide = Path.Combine(AppContext.BaseDirectory, ExeName);
        if (File.Exists(sideBySide)) return sideBySide;

        var published = Path.Combine(AppContext.BaseDirectory, "worker", ExeName);
        if (File.Exists(published)) return published;

        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var candidate = Directory.GetDirectories(dir, "VibeyReports.CrystalWorker", SearchOption.TopDirectoryOnly)
                .Concat(Directory.GetDirectories(dir, "src", SearchOption.TopDirectoryOnly))
                .SelectMany(d => Directory.Exists(d)
                    ? Directory.GetFiles(d, ExeName, SearchOption.AllDirectories)
                    : Array.Empty<string>())
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();

            if (candidate is not null) return candidate;
            dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar));
        }

        throw new FileNotFoundException(
            $"Could not find {ExeName}. Build src/VibeyReports.CrystalWorker, or set {OverrideVariable}.");
    }
}
