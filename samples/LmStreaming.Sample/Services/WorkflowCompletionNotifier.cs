using AchieveAi.LmDotnetTools.LmAgentInfra.Agents;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Delivers an async workflow's completion back into the conversation that started it, and records
///     it in the pool's accepted-input ledger (#418).
/// </summary>
/// <remarks>
///     <para>
///         This exists as a named seam rather than as a lambda inside <c>Program.cs</c>'s agent factory
///         for one reason: it is the accept path least likely to be looked for, and a lambda buried in
///         the composition root is a path no test can reach. Everything the delivery has to get right -
///         record before the send, withdraw on failure, name the agent that took the input - is here,
///         where <c>WorkflowCompletionNotifierTests</c> can drive it.
///     </para>
///     <para>
///         A workflow finishes long after the turn that started it, so the conversation is typically
///         idle when this runs. Without the ledger write, the entry has no run id and is not running
///         between the send and a run picking the notice up - exactly what a grantee handoff or a
///         sandbox session refresh reads as "nothing in hand" - and the agent is disposed with the
///         workflow's result still queued on it. The result is then silently never delivered, which is
///         the worst shape this failure can take: the workflow did the work and nobody is told.
///     </para>
/// </remarks>
internal static class WorkflowCompletionNotifier
{
    /// <summary>
    ///     Prefix on the minted input ids, so one in the ledger or in a log line is attributable to a
    ///     workflow completion rather than to a caller's send.
    /// </summary>
    internal const string InputIdPrefix = "workflow-notify-";

    /// <summary>
    ///     Queues <paramref name="notify"/> onto <paramref name="conversation"/> and records it as
    ///     outstanding work against <paramref name="threadId"/> until a run takes it.
    /// </summary>
    /// <remarks>
    ///     The id is minted here because the ledger retires on the agent echoing that same id back on
    ///     the run assignment that consumes the input. A null id would leave the record retiring only
    ///     on the grace backstop.
    /// </remarks>
    internal static async Task DeliverAsync(
        MultiTurnAgentPool pool,
        string threadId,
        IMultiTurnAgent conversation,
        IMessage notify,
        CancellationToken ct
    )
    {
        var inputId = InputIdPrefix + Guid.NewGuid().ToString("N");

        // Recorded BEFORE the send: afterwards would leave the notice sitting in the agent's channel
        // and absent from the ledger, which is the same hole this closes, only narrower.
        pool.AddOutstandingInput(threadId, inputId, conversation);
        try
        {
            _ = await conversation.SendAsync([notify], inputId, ct: ct);
        }
        catch
        {
            // Nothing was queued, so the id must not outlive the attempt - no run will ever name an
            // input the agent did not receive, and the thread would read busy until the grace expired.
            pool.RemoveOutstandingInput(threadId, inputId, conversation);
            throw;
        }
    }
}
