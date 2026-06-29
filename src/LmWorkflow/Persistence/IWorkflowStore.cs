namespace AchieveAi.LmDotnetTools.LmWorkflow.Persistence;

/// <summary>
///     Durable storage for <see cref="WorkflowInstanceSnapshot"/>s, keyed by a stable instance id. The
///     runtime persists a fresh snapshot after every state-mutating operation (best-effort), and
///     <c>WorkflowSession.ResumeAsync</c> reloads the latest snapshot to rebuild a runtime and continue the
///     run. Implementations must isolate stored snapshots from later mutation of the live runtime (store a
///     copy, not the caller's instance).
/// </summary>
/// <remarks>
///     The <c>instanceId</c> is used as the store correlation key AND is written to logs on a persistence
///     failure, so callers MUST supply an OPAQUE, non-user-identifying value (not an email / tenant /
///     customer id).
/// </remarks>
public interface IWorkflowStore
{
    /// <summary>Upserts the latest snapshot for <paramref name="instanceId"/>.</summary>
    Task SaveAsync(string instanceId, WorkflowInstanceSnapshot snapshot, CancellationToken ct = default);

    /// <summary>Loads the latest snapshot for <paramref name="instanceId"/>, or <c>null</c> if absent.</summary>
    Task<WorkflowInstanceSnapshot?> LoadAsync(string instanceId, CancellationToken ct = default);

    /// <summary>Removes any snapshot for <paramref name="instanceId"/>; a no-op when absent.</summary>
    Task DeleteAsync(string instanceId, CancellationToken ct = default);

    /// <summary>Lists every instance id with a stored snapshot.</summary>
    Task<IReadOnlyList<string>> ListAsync(CancellationToken ct = default);
}
