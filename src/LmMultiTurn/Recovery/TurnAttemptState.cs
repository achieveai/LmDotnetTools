using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Delivery;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Recovery;

/// <summary>
///     Records what a single provider attempt actually produced, so that an attempt cut short by a
///     transport failure can be recovered from without either repeating finished work or persisting
///     work that never finished.
/// </summary>
/// <remarks>
///     <para>
///     The distinction that drives every recovery decision is <b>canonical versus fragment</b>. A
///     fragment (<c>*UpdateMessage</c>) is a delta of a value the canonical complete message repeats in
///     full, so an attempt that produced only fragments produced nothing worth keeping and can be
///     retried outright; an attempt that completed even one canonical message produced work that must
///     survive and be continued from. Classification is delegated to
///     <see cref="ReplayMessagePolicy.IsCanonicalOrControl" /> so the loop and the delivery bridge can
///     never disagree about what "completed" means.
///     </para>
///     <para>
///     The state also owns the attempt's dispatched tool tasks. Tool execution starts eagerly while the
///     provider is still streaming, so an interrupted attempt leaves tasks in flight; recovery may not
///     begin until every one of them has reached a terminal state, or a background execution would race
///     the replacement attempt.
///     </para>
///     <para>Not thread-safe: a single turn is observed by exactly one loop thread.</para>
/// </remarks>
internal sealed class TurnAttemptState(string generationId)
{
    private readonly List<IMessage> _completedMessages = [];
    private readonly Dictionary<string, Task<ToolCallResultMessage>> _pendingToolTasks = [];

    /// <summary>
    ///     Memoized settle operation. Recovery settles the attempt and the ordinary end-of-turn await
    ///     settles it too; both must observe the same single wait rather than each starting their own.
    /// </summary>
    private Task? _settle;

    /// <summary>The generation id this attempt streamed under.</summary>
    public string GenerationId { get; } = generationId;

    /// <summary>
    ///     <see langword="true" /> once the attempt delivered at least one canonical message that carried
    ///     content or ran an effect. This is the retry-versus-continue discriminator:
    ///     <see langword="false" /> means the attempt is safe to abandon wholesale.
    /// </summary>
    /// <remarks>
    ///     Deliberately narrower than "completed at least one canonical message". Accounting and control
    ///     messages — usage above all — are canonical because they must reach history, yet they show the
    ///     user nothing and run nothing. Counting them would instruct the model to continue from a reply
    ///     of which not one word was ever delivered, which strands the turn.
    /// </remarks>
    public bool HasCanonicalMessages => _completedMessages.Any(IsDeliveredContentOrEffect);

    /// <summary>The canonical messages this attempt completed, in arrival order.</summary>
    public IReadOnlyList<IMessage> CompletedMessages => _completedMessages;

    /// <summary>The tool executions this attempt dispatched, keyed by tool call id.</summary>
    public IReadOnlyDictionary<string, Task<ToolCallResultMessage>> PendingToolTasks => _pendingToolTasks;

    /// <summary>
    ///     <see langword="true" /> when the attempt emitted at least one locally executed tool call, which
    ///     is what tells the run loop another turn is owed.
    /// </summary>
    public bool HasToolCalls { get; private set; }

    /// <summary>
    ///     Folds one streamed message into the attempt and reports whether it belongs in conversation
    ///     history.
    /// </summary>
    /// <param name="message">The message the provider just emitted.</param>
    /// <returns>
    ///     <see langword="true" /> for a canonical message the caller must add to history,
    ///     <see langword="false" /> for a streaming fragment it must not. Returning the classification
    ///     rather than exposing a second predicate keeps a single classification site per message.
    /// </returns>
    public bool Observe(IMessage message)
    {
        if (!ReplayMessagePolicy.IsCanonicalOrControl(message))
        {
            return false;
        }

        _completedMessages.Add(message);
        return true;
    }

    /// <summary>
    ///     Whether <paramref name="message" /> put content in front of the user or ran an effect, as
    ///     opposed to merely accounting for the attempt.
    /// </summary>
    /// <param name="message">A canonical message the attempt completed.</param>
    /// <remarks>
    ///     Unrecognized types count as delivered. Under-counting is the dangerous direction: it takes a
    ///     turn that already showed the user something and replays it from the top, duplicating the very
    ///     visible effect recovery exists to protect. Over-counting merely asks the model to continue.
    /// </remarks>
    private static bool IsDeliveredContentOrEffect(IMessage message) => message is not UsageMessage;

    /// <summary>
    ///     Records a dispatched tool execution so recovery can wait for it.
    /// </summary>
    /// <param name="toolCallId">The tool call id the execution answers.</param>
    /// <param name="execution">The in-flight execution.</param>
    public void TrackToolTask(string toolCallId, Task<ToolCallResultMessage> execution)
    {
        HasToolCalls = true;
        _pendingToolTasks[toolCallId] = execution;
    }

    /// <summary>
    ///     Waits until every tool execution this attempt dispatched has reached a terminal state,
    ///     exactly once however many times it is called.
    /// </summary>
    /// <returns>
    ///     A task that faults with the first tool failure, matching what an ordinary end-of-turn await
    ///     has always done. A caller recovering from a transport failure is expected to observe and log
    ///     that fault rather than let it mask the interruption being recovered from.
    /// </returns>
    /// <remarks>
    ///     Deliberately takes no <see cref="CancellationToken" />: abandoning the wait is precisely the
    ///     orphaned-execution race recovery exists to prevent, so there is no token this method could
    ///     honour without breaking its own contract.
    /// </remarks>
    public Task SettleToolTasksAsync() => _settle ??= Task.WhenAll(_pendingToolTasks.Values);
}
