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
/// the right conversation.
/// </summary>
public sealed record AgentPublication(string ThreadId, IMessage Message, AgentPublicationKind Kind);

/// <summary>
/// Optional agent-wide hook observing every message <see cref="MultiTurnAgentBase.PublishToAllAsync(IMessage, CancellationToken)"/>
/// hands to v1 subscribers, WITHOUT changing v1's payload, instance identity, or ordering
/// contract. Intended as the single future authority for durable child replay (e.g. an
/// append-only journal) — see WI #194 tasks 5-6. Deliberately carries no persistence/journal
/// semantics itself; it only defines the observation boundary.
/// </summary>
/// <remarks>
/// The publishing caller AWAITS <see cref="OnPublishedAsync"/> after the existing non-blocking
/// v1 fan-out, so v1 subscriber behavior (identity, order, non-blocking delivery) is completely
/// unaffected by an observer's presence. An exception thrown here propagates to that caller (the
/// run) rather than being swallowed — callers that need best-effort semantics must catch inside
/// their own implementation.
/// </remarks>
public interface IAgentPublicationObserver
{
    ValueTask OnPublishedAsync(AgentPublication publication, CancellationToken ct);
}
