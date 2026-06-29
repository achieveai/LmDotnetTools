namespace AchieveAi.LmDotnetTools.LmWorkflow.Ingest;

/// <summary>
///     Thrown by <see cref="WorkflowValidator.ValidateAndThrow"/> when a workflow definition is invalid.
///     Carries the full list of validation errors; the message is the errors joined for readability.
/// </summary>
public sealed class WorkflowValidationException : Exception
{
    /// <summary>Creates the exception from the collected validation errors.</summary>
    public WorkflowValidationException(IReadOnlyList<string> errors)
        : base(FormatMessage(errors))
    {
        Errors = errors ?? [];
    }

    /// <summary>Every validation error that caused the failure.</summary>
    public IReadOnlyList<string> Errors { get; }

    private static string FormatMessage(IReadOnlyList<string>? errors)
    {
        return errors is { Count: > 0 }
            ? "Workflow validation failed: " + string.Join("; ", errors)
            : "Workflow validation failed.";
    }
}
