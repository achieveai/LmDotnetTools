using AchieveAi.LmDotnetTools.LmWorkflow.Persistence;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Runtime;

/// <summary>
///     Sequences best-effort snapshot saves for a single workflow instance. Saves are SERIALIZED on a
///     per-instance chain so they apply in capture order — save N completes before save N+1 starts — which
///     prevents a slow, stale save from overwriting a newer snapshot (Fix M2). A persistence fault never
///     faults the chain or the live run; it is surfaced at Warning (Fix M3) and the next save re-attempts.
/// </summary>
/// <remarks>
///     This collaborator owns its OWN <c>_saveLock</c>, independent of the runtime's state lock: the runtime
///     captures a snapshot under the state lock, releases it, then enqueues the save here. The save chain is
///     advanced under <c>_saveLock</c> so it never blocks (or is blocked by) the runtime's state lock.
/// </remarks>
internal sealed class SnapshotPersister
{
    // Persistence is serialized so saves apply in capture order. The chain is advanced under a dedicated
    // lock so it never blocks the main runtime lock.
    private readonly object _saveLock = new();
    private Task _saveChain = Task.CompletedTask;

    /// <summary>
    ///     Enqueues a best-effort save of <paramref name="snapshot"/> behind any in-flight save for the
    ///     instance. <see cref="TaskContinuationOptions.ExecuteSynchronously"/> keeps a save inline when its
    ///     antecedent is ALREADY complete (the common case for a synchronous store), so persistence stays as
    ///     prompt as a fire-and-forget for such stores; an async store's save simply chains behind its
    ///     still-running predecessor.
    /// </summary>
    public void Enqueue(
        IWorkflowStore store,
        string instanceId,
        WorkflowInstanceSnapshot snapshot,
        ILogger? logger
    )
    {
        lock (_saveLock)
        {
            _saveChain = _saveChain
                .ContinueWith(
                    _ => SaveBestEffortAsync(store, instanceId, snapshot, logger),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default
                )
                .Unwrap();
        }
    }

    /// <summary>
    ///     Returns the current per-instance save chain so a host can flush every pending best-effort save
    ///     before disposing the run. Best-effort saves never fault the chain, so awaiting this never throws;
    ///     it is a no-op (already-completed task) when nothing has been enqueued.
    /// </summary>
    public Task DrainAsync()
    {
        lock (_saveLock)
        {
            return _saveChain;
        }
    }

    private static async Task SaveBestEffortAsync(
        IWorkflowStore store,
        string instanceId,
        WorkflowInstanceSnapshot snapshot,
        ILogger? logger
    )
    {
        try
        {
            await store.SaveAsync(instanceId, snapshot).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Best-effort: a persistence fault must never corrupt or fail the live run. Surface it at Warning
            // (Fix M3) so a store outage is visible; the next mutation re-attempts with a fresh snapshot.
            logger?.LogWarning(ex, "Workflow {InstanceId} snapshot persistence failed", instanceId);
        }
    }
}
