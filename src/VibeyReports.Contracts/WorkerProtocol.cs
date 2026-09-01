using System.Collections.Generic;

namespace VibeyReports.Contracts;

public static class WorkerCommands
{
    public const string Read = "read";
    public const string Apply = "apply";
    public const string Render = "render";

    public static readonly string[] All = { Read, Apply, Render };
}

public sealed class WorkerRequest
{
    public string Command { get; set; } = "";
    public string ReportPath { get; set; } = "";
    /// <summary>Destination for "apply". Ignored by "read" and "render".</summary>
    public string? OutputPath { get; set; }
    public bool Overwrite { get; set; }
    /// <summary>Required for "apply".</summary>
    public LayoutPlan? Plan { get; set; }
}

public sealed class WorkerResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }

    public ReportSchema? Schema { get; set; }
    public int? OperationsApplied { get; set; }
    /// <summary>Names of objects deleted by a "removeObject" operation in the applied plan, set by "apply".</summary>
    public List<string>? RemovedObjects { get; set; }
    /// <summary>Aliases of data-source tables deleted by a "removeTable" operation, set by "apply".</summary>
    public List<string>? RemovedTables { get; set; }
    public string? OutputPath { get; set; }
    /// <summary>Base64-encoded PDF, set by "render".</summary>
    public string? PdfBase64 { get; set; }
    public List<ValidationError>? ValidationErrors { get; set; }

    public static WorkerResponse Success() => new WorkerResponse { Ok = true };

    public static WorkerResponse Failure(string error, ValidationResult? validation = null) =>
        new WorkerResponse
        {
            Ok = false,
            Error = error,
            ValidationErrors = validation?.Errors
        };
}
