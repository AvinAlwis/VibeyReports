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

        return Find(AppContext.BaseDirectory);
    }

    /// <summary>
    /// The base-directory-relative probes only (side-by-side, then worker/ subdirectory, then the
    /// dev-mode walk-up). Split out from <see cref="Find()"/> — which additionally honours
    /// VIBEY_WORKER_PATH — round-1 fix T1: so tests can drive the worker/ subdirectory probe with a
    /// synthetic base directory, isolating it from both the environment-variable override and
    /// whatever the walk-up would otherwise find from the real AppContext.BaseDirectory.
    /// Internal, exposed to VibeyReports.Mcp.Tests via InternalsVisibleTo.
    /// </summary>
    internal static string Find(string baseDirectory)
    {
        var sideBySide = Path.Combine(baseDirectory, ExeName);
        if (File.Exists(sideBySide)) return sideBySide;

        var published = Path.Combine(baseDirectory, "worker", ExeName);
        if (File.Exists(published)) return published;

        var dir = baseDirectory;
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
