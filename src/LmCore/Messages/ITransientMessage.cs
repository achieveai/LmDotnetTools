namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     Marker for live-only messages that are broadcast to the current subscribers of a run but must NEVER
///     be added to the in-flight replay buffer, the conversation history, or durable persistence. A
///     reconnecting or rehydrating consumer reconstructs the state such a message conveys from an
///     authoritative source (for usage, the persisted conversation aggregate served from
///     <c>GET /conversations/{id}/usage</c>) rather than by replaying the transient frames. Used by
///     <see cref="ConversationUsageMessage" /> so the live usage banner can update mid-run without polluting
///     history, replay, or storage (#196).
/// </summary>
public interface ITransientMessage
{
}
