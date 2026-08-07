using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Delivery;

/// <summary>
///     Classifies published messages into the two delivery classes resilient stream delivery depends on:
///     <b>canonical/control</b> messages, which a consumer that reconnects or resynchronizes mid-run must
///     receive, and <b>streaming fragments</b>, which it must not. A fragment carries only a delta of a
///     value the canonical complete message repeats in full (text, reasoning, tool-call arguments), so
///     replaying fragments to a resynchronizing consumer either duplicates already-rendered content or
///     hands it a partial value it cannot reconcile. Replaying the canonical message alone reconstructs
///     the same end state exactly once.
/// </summary>
/// <remarks>
///     The classification is an explicit type-pattern switch, never a name-suffix heuristic: a new message
///     type must be considered deliberately. The default is <see langword="true" /> so an unrecognized
///     type is treated as canonical — the safe direction, since over-delivering a complete message is
///     recoverable by the consumer while silently dropping one is not.
/// </remarks>
internal static class ReplayMessagePolicy
{
    /// <summary>
    ///     Returns <see langword="true" /> when <paramref name="message" /> is a canonical content, control
    ///     or accounting message that a resynchronizing consumer must receive, and <see langword="false" />
    ///     for a streaming update/fragment.
    /// </summary>
    /// <param name="message">The published message to classify.</param>
    internal static bool IsCanonicalOrControl(IMessage message) =>
        message switch
        {
            // Streaming fragments. JSON argument fragments have no message type of their own — they ride
            // on the tool-call update types below via ToolCallUpdate.JsonFragmentUpdates.
            TextUpdateMessage => false,
            ReasoningUpdateMessage => false,
            ToolCallUpdateMessage => false,
            ToolsCallUpdateMessage => false,
            _ => true,
        };
}
