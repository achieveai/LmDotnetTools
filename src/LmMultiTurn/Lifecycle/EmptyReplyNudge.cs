using System.Collections.Immutable;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;

/// <summary>
/// The continuation prompt the run loop sends when a turn after tool results ends with no reply: no
/// text and no tool call, only reasoning or nothing at all.
/// </summary>
/// <remarks>
/// Anthropic documents this as an <c>end_turn</c> the model chose, most often after a user-role text
/// follows tool results (the elapsed-time notice is one), and says to recover with a continuation prompt
/// in a new user message rather than a plain retry. Without it the loop reads "no tool calls" as "done"
/// and the run ends mid-task. The loop sends at most two in a row, and a turn with tool calls resets
/// the count. Like the notice, the nudge is user-role, loop-authored and hidden from every client view.
/// </remarks>
public static class EmptyReplyNudge
{
    /// <summary>Metadata key that marks a <see cref="TextMessage"/> as this nudge.</summary>
    public const string MetadataKey = "empty_reply_nudge";

    /// <summary>The nudge's text.</summary>
    public const string Text =
        "Please continue with the task. Your last reply had no text and no tool call, so nothing was done.";

    /// <summary>True when <paramref name="message"/> is this nudge.</summary>
    public static bool IsNudge(IMessage message)
    {
        return message is TextMessage { Metadata: { } metadata } && metadata.ContainsKey(MetadataKey);
    }

    /// <summary>A new nudge.</summary>
    public static TextMessage Build()
    {
        return new TextMessage
        {
            Text = Text,
            Role = Role.User,
            Metadata = ImmutableDictionary<string, object>.Empty.Add(MetadataKey, true),
        };
    }

    /// <summary>
    /// True for a user-role text the loop wrote for the model, not the person: the elapsed-time notice
    /// or this nudge. Such a row stays in the model's context but is never shown or counted as input.
    /// </summary>
    public static bool IsLoopAuthored(IMessage message)
    {
        return ElapsedTimeNotice.IsNotice(message) || IsNudge(message);
    }
}
