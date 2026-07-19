using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn;

/// <summary>
/// Distinguishes WHY a message reached <see cref="MultiTurnAgentBase.PublishToAllAsync(IMessage, CancellationToken)"/>, so a
/// future durable observer (e.g. an append-only replay journal) can apply the right persistence
/// semantics without re-deriving intent from the <see cref="IMessage"/> payload alone.
/// </summary>
public enum AgentPublicationKind
{
    /// <summary>
    /// An ordinary published message — a streamed provider frame (text/reasoning update, tool
    /// call, etc.), a non-deferred tool result, an out-of-band <see cref="NotifyMessage"/>, or
    /// any other message that is not one of the other kinds below.
    /// </summary>
    Message,

    /// <summary>
    /// A previously-deferred tool call placeholder being replaced with its resolved content
    /// (see <c>MultiTurnAgentLoop.ResolveToolCallAsync</c>). The placeholder was already
    /// published once as <see cref="Message"/>; this publication carries the SAME
    /// <c>ToolCallId</c> with the final result, so a durable observer must treat it as a
    /// replacement of the earlier entry rather than a new append.
    /// </summary>
    Replacement,

    /// <summary>
    /// A <see cref="RunAssignmentMessage"/> marking the start (or injection) of a run.
    /// </summary>
    RunAssignment,

    /// <summary>
    /// A <see cref="RunCompletedMessage"/> marking a run's terminal outcome (completed, errored,
    /// or cancelled).
    /// </summary>
    RunTerminal,
}

/// <summary>
/// An agent publication observed at the <see cref="MultiTurnAgentBase.PublishToAllAsync(IMessage, CancellationToken)"/>
/// boundary — the same original <see cref="IMessage"/> instance every v1 subscriber receives,
/// tagged with enough intent (<see cref="Kind"/>) to support future durable replay without
/// re-inspecting the message payload, plus the publishing agent's <see cref="ThreadId"/> so a
/// single observer shared across a parent and its sub-agents can attribute each publication to
/// the right conversation. <paramref name="Sequence"/> is a monotonically increasing, per-agent
/// counter assigned under the SAME lock <c>MultiTurnAgentBase</c> uses for its v1 replay-buffer
/// bookkeeping, so it reflects the exact order publications reached that lock — a durable observer
/// (or a test) can rely on <see cref="Sequence"/> — not delivery/completion timing — as the
/// authoritative per-agent order, even when <see cref="IAgentPublicationObserver.OnPublishedAsync"/>
/// calls for concurrent publications complete in a different order.
/// </summary>
public sealed record AgentPublication(string ThreadId, IMessage Message, AgentPublicationKind Kind, long Sequence);

/// <summary>
/// Optional agent-wide hook observing every message <see cref="MultiTurnAgentBase.PublishToAllAsync(IMessage, CancellationToken)"/>
/// hands to v1 subscribers, WITHOUT changing v1's payload, instance identity, or ordering
/// contract. Intended as the single future authority for durable child replay (e.g. an
/// append-only journal) — see WI #194 tasks 5-6. Deliberately carries no persistence/journal
/// semantics itself; it only defines the observation boundary.
/// </summary>
/// <remarks>
/// The publishing caller AWAITS <see cref="OnPublishedAsync"/> after the existing non-blocking
/// v1 fan-out for THAT publication, so v1 subscriber behavior (identity, order, non-blocking
/// delivery) is completely unaffected by an observer's presence. An exception thrown here
/// propagates to that caller (the run) rather than being swallowed — callers that need
/// best-effort semantics must catch inside their own implementation; a failure for one
/// publication never permanently blocks delivery of later publications to this observer.
/// <para>
/// <b>Ordering:</b> <c>MultiTurnAgentBase</c> serializes calls to this method — for a single agent
/// instance, no two <see cref="IAgentPublicationObserver.OnPublishedAsync"/> invocations ever run
/// concurrently, and they start in <see cref="AgentPublication.Sequence"/> order (the order
/// publications acquired the agent's internal replay lock), regardless of how the underlying
/// <c>PublishToAllAsync</c> calls overlap or which one becomes ready first.
/// </para>
/// <para>
/// <b>Cancellation scope:</b> the <c>ct</c> parameter is NOT the cancellation token the publishing
/// run/request passed to <c>PublishToAllAsync</c> — a run/request being cancelled (e.g. a client
/// disconnect or <c>StopAsync</c>) must not interrupt observer durability. Instead <c>ct</c> is
/// scoped to the agent's own lifetime and is only signalled when the agent itself is disposed
/// (<c>DisposeAsync</c>), so implementations that need to distinguish "agent is being torn down"
/// from "keep going" can rely on it.
/// </para>
/// </remarks>
public interface IAgentPublicationObserver
{
    /// <param name="publication">The observed publication, including its per-agent <see cref="AgentPublication.Sequence"/>.</param>
    /// <param name="ct">
    /// Cancelled only when the publishing <see cref="MultiTurnAgentBase"/> is disposed — never
    /// merely because the publishing run/request's own token was cancelled. See remarks.
    /// </param>
    ValueTask OnPublishedAsync(AgentPublication publication, CancellationToken ct);
}
