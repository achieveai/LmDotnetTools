namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

/// <summary>The lifecycle status the runtime tracks for each authored task it surfaces and observes.</summary>
public enum WorkflowTaskStatus
{
    /// <summary>The task has been surfaced as an expected action but no spawn has been observed yet.</summary>
    Pending,

    /// <summary>A spawn for the task has been observed and correlated; its result is awaited.</summary>
    InFlight,

    /// <summary>The task result was observed and passed schema validation (and any writes applied).</summary>
    Validated,

    /// <summary>The task errored, returned non-JSON, or failed schema validation.</summary>
    Failed,
}
