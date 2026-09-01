using System.Collections.Generic;

namespace VibeyReports.Contracts;

public sealed class ValidationError
{
    public int OperationIndex { get; set; }
    public string Message { get; set; } = "";
}

public sealed class ValidationResult
{
    public bool IsValid { get; set; }
    public List<ValidationError> Errors { get; set; } = new List<ValidationError>();

    public static ValidationResult Ok() => new ValidationResult { IsValid = true };
}
