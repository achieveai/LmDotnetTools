namespace AchieveAi.LmDotnetTools.LmWorkflow.Ingest;

/// <summary>
///     The outcome of validating a workflow definition. <see cref="Errors"/> contains every error found
///     (validation collects all failures rather than stopping at the first).
/// </summary>
public sealed record ValidationResult(bool IsValid, IReadOnlyList<string> Errors);
