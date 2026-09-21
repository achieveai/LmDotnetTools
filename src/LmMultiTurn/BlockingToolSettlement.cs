using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.ClientTools;
using AchieveAi.LmDotnetTools.LmMultiTurn.Triggers;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// How a blocking tool call — one that parked the run waiting for a human or an external event —
/// finally ended.
/// </summary>
internal enum BlockingToolEnding
{
    /// <summary>Nobody has ended it yet.</summary>
    Pending = 0,

    /// <summary>
    /// The loop settled it with its tool's placeholder so an interrupting input could run. The real
    /// answer, if one ever arrives, is redirected into the conversation instead of resolving the call.
    /// </summary>
    SettledEarly = 1,

    /// <summary>The real result arrived first and resolved the call in the ordinary way.</summary>
    Answered = 2,
}

/// <summary>
/// The one-shot ending of a single blocking tool call, and the one-shot delivery of the answer that
/// lost to an early settle.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists on top of the coordinator's claim.</b>
/// <see cref="DelayedResultCoordinator.TryBeginResolve"/> already serialises two resolutions of the
/// same call under its own lock — but the loser is told only "somebody else holds this with a
/// different fingerprint", and <c>MultiTurnAgentLoop.ResolveToolCallInternalAsync</c> turns that into
/// <see cref="ResolveToolCallOutcome.Conflict"/> <em>without touching history</em>. When the loser is
/// the human's answer and the winner is an early settle, refusing it is exactly how the answer gets
/// lost. This latch is what lets the loser find out <em>which</em> ending won, so an answer that lost
/// can be redirected into the conversation rather than refused.
/// </para>
/// <para>
/// Claimed before the coordinator is asked, by both paths, so the two orders are symmetric: whichever
/// caller reaches the CAS first decides the ending, and the other one reads that decision instead of
/// racing it.
/// </para>
/// <para>
/// In-memory only, and deliberately so — it covers the live race. Survival across a loop rebuild (a
/// mode or provider switch) and a process restart comes from the placeholder that the early settle
/// writes into the resolved <c>ToolCallResultMessage</c> in history, which
/// <see cref="EarlySettlePlaceholders.IsEarlySettleResult"/> recognises.
/// </para>
/// </remarks>
internal sealed class BlockingToolOutcome
{
    /// <summary>Nobody is delivering the redirected answer, and nobody has.</summary>
    private const int DeliveryFree = 0;

    /// <summary>A delivery holds the right to inject and has not finished trying.</summary>
    private const int DeliveryInFlight = 1;

    /// <summary>The answer is in the conversation. Permanent.</summary>
    private const int DeliveryDone = 2;

    private int _ending;
    private int _answerDelivery;

    /// <summary>How the call ended, or <see cref="BlockingToolEnding.Pending"/> while it has not.</summary>
    public BlockingToolEnding Ending => (BlockingToolEnding)Volatile.Read(ref _ending);

    /// <summary>Claims the ending for an early settle. Exactly one claim ever wins, forever.</summary>
    public bool TryClaimEarlySettle() => TryClaim(BlockingToolEnding.SettledEarly);

    /// <summary>Claims the ending for the real result. Exactly one claim ever wins, forever.</summary>
    public bool TryClaimRealResult() => TryClaim(BlockingToolEnding.Answered);

    /// <summary>
    /// Claims the right to inject the redirected answer, as an IN-FLIGHT claim the caller must then
    /// settle with <see cref="CommitAnswerDelivery"/> or <see cref="ReleaseAnswerDelivery"/>. False
    /// for every delivery that arrives while one is in flight or after one has landed, so a
    /// redelivered webhook or a double-submitted answer form cannot inject the same answer twice.
    /// </summary>
    /// <remarks>
    /// Three states rather than a one-shot flag, because the injection can FAIL. The caller reports a
    /// failed injection as <c>StoreFailed</c> — "nothing happened, send it again" — and a claim that
    /// went straight to delivered would make every one of those retries a <c>Duplicate</c>: the
    /// answer lost, behind an outcome that promised it was safe to retry. In-flight keeps the
    /// exactly-once guarantee against a concurrent redelivery while leaving the claim recoverable.
    /// </remarks>
    public bool TryClaimAnswerDelivery() =>
        Interlocked.CompareExchange(ref _answerDelivery, DeliveryInFlight, DeliveryFree) == DeliveryFree;

    /// <summary>
    /// Makes an in-flight claim permanent, once the answer is actually in the conversation. Every
    /// later delivery of the same answer is refused from here on, forever.
    /// </summary>
    public void CommitAnswerDelivery() => Volatile.Write(ref _answerDelivery, DeliveryDone);

    /// <summary>
    /// Hands an in-flight claim back after an injection that did not happen, so the retry the caller
    /// was told to make can take it. A no-op once the delivery has been committed.
    /// </summary>
    public void ReleaseAnswerDelivery() =>
        _ = Interlocked.CompareExchange(ref _answerDelivery, DeliveryFree, DeliveryInFlight);

    private bool TryClaim(BlockingToolEnding ending) =>
        Interlocked.CompareExchange(ref _ending, (int)ending, (int)BlockingToolEnding.Pending)
        == (int)BlockingToolEnding.Pending;
}

/// <summary>
/// What a blocking tool returns when the loop settles it early, and how to restate its request to the
/// model when the real answer arrives later.
/// </summary>
/// <param name="ResultJson">
/// The tool result written in place of the real one. It is the tool's own envelope shape with a named,
/// non-error <c>status</c>, so a model branching on <c>status</c> sees a legitimate outcome rather than
/// a string to parse.
/// </param>
/// <param name="RenderRequest">
/// Renders the original call's arguments back into readable form, for the injected message that carries
/// the request alongside the late answer.
/// </param>
/// <param name="ShouldWake">
/// Whether this batch of interrupting input is worth ending the park for. Evaluated at the only seam
/// where the batch's message kinds are still visible - the loop's parked branch - because the tool
/// itself never sees them. A tool that says no is left parked and the batch is folded into history
/// instead, which is the cheaper ending and the one that keeps a background trickle from turning
/// every park into a turn.
/// </param>
/// <param name="InjectionTag">
/// The element the late real result is injected under. Named per tool: a timer firing is not an
/// answer from the human, and an envelope that says it is would be read as one.
/// </param>
internal sealed record EarlySettleSpec(
    string ResultJson,
    Func<string?, string> RenderRequest,
    Func<ParkedInterruptionBatch, bool> ShouldWake,
    string InjectionTag = "user-answer"
);

/// <summary>
/// One message that arrived while the conversation was parked, reduced to what a wake policy may look
/// at.
/// </summary>
/// <param name="Message">The message itself.</param>
/// <param name="IsTriggerFire">
/// Whether it is a trigger envelope a <c>Wait</c> in notify mode produced rather than something a
/// person or a peer sent. It is a <see cref="TextMessage"/> like a human turn is, and only the queue
/// entry it arrived on can tell the two apart - so the distinction is captured here, at the drain,
/// while it still exists.
/// </param>
internal readonly record struct ParkedInterruption(IMessage Message, bool IsTriggerFire);

/// <summary>
/// The interrupting batch as a wake policy sees it.
/// </summary>
/// <param name="Items">Every message in the batch, in arrival order.</param>
/// <param name="AllNotifications">
/// The loop's own <c>AllMessagesAreNotifications</c> verdict, passed in rather than recomputed so a
/// policy that wants "anything a notification-only fold would not already have handled" is spelled
/// with the same predicate the fold branch is gated on.
/// </param>
internal sealed record ParkedInterruptionBatch(IReadOnlyList<ParkedInterruption> Items, bool AllNotifications);

/// <summary>
/// The tools the loop is allowed to settle early, and what it settles them with.
/// </summary>
/// <remarks>
/// <para>
/// A tool that is not in this table keeps the pre-existing fail-fast: a third-party tool with no async
/// fallback must never be silently settled with a lie about what happened.
/// </para>
/// <para>
/// <b>Adoption point.</b> The <c>WaitAgent</c>/<c>WaitForAgents</c> work adds one entry here, with the
/// placeholder text living as a <c>const</c> on the tool's own provider so the tool description and the
/// placeholder cannot drift apart.
/// </para>
/// </remarks>
internal static class EarlySettlePlaceholders
{
    private static readonly Dictionary<string, EarlySettleSpec> Specs = new(StringComparer.Ordinal)
    {
        [AskUserQuestionToolProvider.ToolName] = new EarlySettleSpec(
            AskUserQuestionToolProvider.EarlySettleResultJson,
            AskUserQuestionToolProvider.RenderRequestForInjection,
            // A question wakes for everything a notification-only fold does not already handle. This
            // is exactly the condition the parked branch used before wake policies existed, written
            // out so that adding a curated policy for another tool cannot change this one.
            static batch =>
                !batch.AllNotifications
        ),
        [WaitToolProvider.WaitToolName] = new EarlySettleSpec(
            WaitToolProvider.EarlySettleResultJson,
            WaitToolProvider.RenderRequestForInjection,
            WakesAParkedWait,
            InjectionTag: WaitToolProvider.InjectionTag
        ),
    };

    /// <summary>
    /// The curated wake policy for a parked <c>Wait</c>: which interruptions are worth ending the park
    /// for, and which are folded into history under it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A <c>Wait</c> is not a question - nobody is standing by for it, and most of what arrives while
    /// it is parked is background chatter the run would rather absorb than be woken by. So unlike
    /// <c>AskUserQuestion</c>, which wakes for anything that is not purely a notification, this
    /// enumerates what wakes: a person, a peer addressing this agent, a descendant stuck on a
    /// question, a sub-agent or workflow reaching a terminal state. Everything else - todo nudges and
    /// digests, context-discovery injections, plain client notifications, another wait's notify-mode
    /// fire - is folded and the run stays parked and armed.
    /// </para>
    /// <para>
    /// <b>On "a sub-agent the wait is not waiting on".</b> Every kind a <c>Wait</c> can be armed on is
    /// registered by the host, and no in-tree kind names an agent, so a
    /// <see cref="NotifyKinds.SubAgentCompletion"/> cannot be the parked wait's own target today. If a
    /// host ever registers an agent-completion kind, the target's own fire resolves the call through
    /// the ordinary path and this policy is never consulted for it; the worst a race can do is wake a
    /// wait that was about to resolve by itself.
    /// </para>
    /// </remarks>
    internal static bool WakesAParkedWait(ParkedInterruptionBatch batch)
    {
        foreach (var item in batch.Items)
        {
            // Nothing a parked Wait can be armed on names an agent, so no completion reaching this
            // seam can be its own target - see the remark above. The handler-side waits, which CAN
            // name their targets, pass a real predicate.
            if (WakesABlockedWait(item, static _ => false))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether <paramref name="item"/> is worth ending a blocking wait for, given which agents that
    /// wait is already blocked on.
    /// </summary>
    /// <remarks>
    /// <para>
    /// One vocabulary for both places a wait can be interrupted: the run loop's parked branch (a
    /// deferred <c>Wait</c>) and the sub-agent wait handlers (<c>WaitAgent</c>, <c>WaitForAgents</c>),
    /// which block inside a turn and race this against their completion tasks. They are different
    /// mechanisms - one settles a deferral, the other returns a status - but "is this worth
    /// interrupting for" must not be two answers, or the same todo nudge would wake one and not the
    /// other.
    /// </para>
    /// <para>
    /// <paramref name="isAlreadyWaitedOn"/> is the single difference between the two callers. A
    /// completion the wait is ALREADY blocked on is not an interruption - it is the wait's own
    /// result, and it arrives through the completion task, which reports it as <c>completed</c>.
    /// Waking on it as well would end the wait a beat early with the wrong status and no result.
    /// </para>
    /// </remarks>
    internal static bool WakesABlockedWait(ParkedInterruption item, Func<string?, bool> isAlreadyWaitedOn) =>
        item.Message switch
        {
            // A peer addressing this agent directly - a question, a delegated task, or the answer
            // to one this agent asked for. Every one of them is somebody waiting on this run.
            AgentMessage => true,
            NotifyMessage notify => notify.NotifyKind switch
            {
                NotifyKinds.SubAgentCompletion => !isAlreadyWaitedOn(notify.SourceToolCallId),
                NotifyKinds.DescendantQuestion or NotifyKinds.WorkflowCompletion => true,
                _ => false,
            },
            // A notify-mode wait firing is machine output, not a person, whatever its shape.
            _ when item.IsTriggerFire => false,
            TextMessage text => text.Role == Role.User,
            _ => false,
        };

    /// <summary>The settlement for <paramref name="toolName"/>, or false when it has none.</summary>
    public static bool TryGet(string? toolName, out EarlySettleSpec spec)
    {
        if (string.IsNullOrEmpty(toolName))
        {
            spec = null!;
            return false;
        }

        return Specs.TryGetValue(toolName, out spec!);
    }

    /// <summary>
    /// Whether <paramref name="result"/> is the placeholder an early settle left behind.
    /// </summary>
    /// <remarks>
    /// This is the durable half of the latch. The placeholder is written into the resolved
    /// <c>ToolCallResultMessage</c> by the ordinary resolution path, so it is persisted with the
    /// conversation and read back on a loop rebuild or a process restart — at which point the
    /// in-memory <see cref="BlockingToolOutcome"/> no longer exists and this is the only way a late
    /// answer can tell "settled early" from "answered with something else".
    /// </remarks>
    public static bool IsEarlySettleResult(string? result)
    {
        if (string.IsNullOrEmpty(result) || result[0] != '{')
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(result);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && string.Equals(status.GetString(), EarlySettleStatus, StringComparison.Ordinal);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>
    /// The <c>status</c> every early-settle placeholder carries. Shared across tools on purpose: it is
    /// the one token a model — and <see cref="IsEarlySettleResult"/> — branches on.
    /// </summary>
    public const string EarlySettleStatus = "deferred_to_notification";
}
