using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Collaboration;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace AchieveAi.LmDotnetTools.LmWorkflow.Collaboration;

/// <summary>
///     The collaboration capability surface of one workflow controller: peers WRITE to it (an
///     <see cref="AgentMessage"/> is delivered into the controller loop's own non-blocking input path) and
///     permitted readers READ from it (the run's live status and the controller's persisted orchestration
///     transcript).
/// </summary>
/// <remarks>
///     <para>
///         <b>Why LmWorkflow owns this.</b> <c>LmMultiTurn</c>'s own loop endpoint is internal to that
///         assembly, and a workflow controller has to be registered BEFORE its loop exists (capacity is taken
///         first, so a refused run never builds a loop at all). The endpoint is therefore late-bound: until
///         <see cref="Attach"/> runs, a delivery is an explicit, recoverable refusal rather than a crash, and
///         <see cref="Detach"/> restores that state once the run is torn down.
///     </para>
///     <para>
///         <b>Authority.</b> Delivery only enqueues ordinary input on the controller loop. It cannot drive a
///         workflow transition: every transition still goes through the runtime's validated workflow tools,
///         which reject anything the current graph state does not already permit. Agent-authored content
///         therefore never grants approval nor expands the controller's authority.
///     </para>
/// </remarks>
internal sealed class WorkflowControllerEndpoint : IAgentWriteEndpoint, IAgentReadEndpoint
{
    /// <summary>Reason recorded when the controller's run is not (or no longer) live.</summary>
    internal const string NotRunningReasonCode = "not_running";

    /// <summary>Reason recorded when the controller loop's input channel is full.</summary>
    internal const string InputQueueFullReasonCode = "input_queue_full";

    private readonly Func<string> _status;
    private readonly string _threadId;
    private readonly IConversationStore? _conversationStore;
    private IMultiTurnAgent? _loop;

    /// <summary>Creates the endpoint for one controller run.</summary>
    /// <param name="status">Reads the run's live collaboration status. Never blocks.</param>
    /// <param name="threadId">The controller loop's persistence thread id, read for the transcript.</param>
    /// <param name="conversationStore">
    ///     The store the controller's own turns are persisted to. When absent the transcript facet reports
    ///     empty rather than failing — the run is simply not persisted, so there is nothing to read.
    /// </param>
    internal WorkflowControllerEndpoint(
        Func<string> status,
        string threadId,
        IConversationStore? conversationStore
    )
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentException.ThrowIfNullOrEmpty(threadId);

        _status = status;
        _threadId = threadId;
        _conversationStore = conversationStore;
    }

    /// <summary>Binds the controller loop once it exists, making the write facet live.</summary>
    internal void Attach(IMultiTurnAgent loop)
    {
        ArgumentNullException.ThrowIfNull(loop);
        Volatile.Write(ref _loop, loop);
    }

    /// <summary>Unbinds the loop at teardown so a late delivery is refused instead of hitting a disposed loop.</summary>
    internal void Detach() => Volatile.Write(ref _loop, null);

    /// <inheritdoc />
    public async ValueTask<AgentDeliveryOutcome> DeliverAsync(
        AgentMessage message,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        if (Volatile.Read(ref _loop) is not { } loop)
        {
            return new AgentDeliveryOutcome(AgentDeliveryDisposition.Refused, NotRunningReasonCode);
        }

        // TrySendAsync (not the blocking send) so a full input channel is a visible, recoverable refusal
        // rather than a delivery task that stalls behind the controller's lifecycle.
        var receipt = await loop.TrySendAsync([message], ct: cancellationToken).ConfigureAwait(false);

        return receipt is null
            ? new AgentDeliveryOutcome(AgentDeliveryDisposition.Refused, InputQueueFullReasonCode)
            : new AgentDeliveryOutcome(AgentDeliveryDisposition.Delivered);
    }

    /// <inheritdoc />
    public ValueTask<string> GetStatusAsync(CancellationToken cancellationToken = default) =>
        new(_status());

    /// <inheritdoc />
    public async ValueTask<IReadOnlyList<IMessage>> GetTranscriptAsync(
        CancellationToken cancellationToken = default
    )
    {
        if (_conversationStore is null)
        {
            return [];
        }

        var persisted = await _conversationStore
            .LoadMessagesAsync(_threadId, cancellationToken)
            .ConfigureAwait(false);

        return MessagePersistenceConverter.FromPersistedMessages(persisted);
    }
}
