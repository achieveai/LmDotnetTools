using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;

/// <summary>
/// Joins the three halves of restart survival at one call: read what the last process wrote, reconcile
/// it into this collaboration, and keep writing it down as the collaboration changes.
/// </summary>
/// <remarks>
/// One entry point rather than three, because the parts are only correct in this order and only
/// together. Reconciling without then re-capturing would leave the persisted document naming agents
/// this process has already declared gone, so every subsequent restart would inherit them; capturing
/// without first reconciling would overwrite the document with an empty roster before anyone had read
/// it, which is the one failure that destroys the evidence instead of just missing it.
/// </remarks>
public static class CollaborationIdentityWiring
{
    /// <summary>
    /// Reconciles the persisted binding for <paramref name="setup"/> and subscribes durable capture to
    /// the collaboration's own change notifications.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this AFTER the root's loop has been constructed, so the root is already registered as live.
    /// Reconciling first would still be corrected by the later registration — a live entry always beats
    /// a tombstone in <see cref="AgentCollaborationDirectory.Resolve"/> — but the operator trace would
    /// name the live root as a casualty, which is a lie in the one place an operator is reading.
    /// </para>
    /// <para>
    /// The returned handle unsubscribes and then flushes. Disposing it is what makes the last change
    /// durable; without it the final capture is left scheduled in memory, which is exactly the state
    /// this whole feature exists to stop being lost.
    /// </para>
    /// </remarks>
    /// <param name="setup">The root's collaboration, already registered in its own directory.</param>
    /// <param name="store">Where the binding is persisted, keyed by the collaboration id.</param>
    /// <param name="logger">Sink for the operator trace and for write failures.</param>
    /// <param name="cancellationToken">Cancels the initial load only.</param>
    /// <returns>A handle that stops capturing and flushes what is outstanding.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="setup"/> or <paramref name="store"/> is null.</exception>
    public static async Task<IAsyncDisposable> AttachAsync(
        AgentCollaborationSetup setup,
        IConversationStore store,
        ILogger? logger = null,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(setup);
        ArgumentNullException.ThrowIfNull(store);

        var bundle = setup.Bundle;
        var rootAgentId = setup.AgentId;

        var persisted = await ConversationAgentBindingProjection.LoadAsync(
            store,
            bundle.CollaborationId,
            cancellationToken
        );

        var report = AgentCollaborationRestartReconciler.Reconcile(bundle, persisted);
        if (!report.IsEmpty)
        {
            // Information-level, not debug: this is the ONLY place a restart's casualties are ever
            // reported. Nothing is pushed to the model and no turn is started, so a trace nobody sees
            // by default is a restart nobody can explain afterwards.
            logger?.LogInformation(
                "{CollaborationTrace} (collaboration {CollaborationId})",
                report.ToOperatorTrace(),
                bundle.CollaborationId
            );
        }

        var writer = new UsagePersistenceWriter(
            // The capture is taken HERE, on the writer's own drain task, rather than at schedule time:
            // the writer coalesces a burst into one write, and taking the snapshot at write time is what
            // makes the surviving write the latest state instead of the first one that asked.
            ct =>
                ConversationAgentBindingProjection.SaveAsync(store, bundle.CaptureIdentityBinding(rootAgentId), ct),
            ex =>
                logger?.LogError(
                    ex,
                    "Failed to persist the collaboration identity binding for {CollaborationId}; agents spawned in "
                        + "this session may resolve as unknown rather than as not live after a restart",
                    bundle.CollaborationId
                )
        );

        bundle.Directory.Logger ??= logger;

        Action capture = writer.Schedule;
        bundle.Directory.OnDirectoryChanged += capture;

        // One write now, so the reconciled roster replaces the one that has just been acted on. Without
        // it a process that reconciles and then does nothing else leaves the old document in place, and
        // the next restart reports the same agents dead a second time.
        writer.Schedule();

        return new IdentityCaptureSubscription(bundle.Directory, capture, writer);
    }

    /// <summary>Unsubscribes capture, then flushes whatever was still scheduled.</summary>
    private sealed class IdentityCaptureSubscription(
        AgentCollaborationDirectory directory,
        Action capture,
        UsagePersistenceWriter writer
    ) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            // Unsubscribe FIRST. Flushing while still subscribed would let a change arriving during the
            // flush schedule another write against a collaboration that is being torn down.
            directory.OnDirectoryChanged -= capture;
            _ = await writer.FlushAsync();
        }
    }
}
