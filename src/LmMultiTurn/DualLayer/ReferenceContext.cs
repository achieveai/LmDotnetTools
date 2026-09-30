using System.Text;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;

/// <summary>
/// Renders the messages the planner received since the executor's last call as one block the executor
/// reads for reference. Each message is one line-group with its origin in brackets, so the executor can
/// tell the user's words from a sub-agent's notice or a peer's message without parsing anything.
/// </summary>
internal static class ReferenceContext
{
    public static string Render(IReadOnlyList<IMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var block = new StringBuilder();
        _ = block
            .Append('<')
            .Append(DualLayerPrompts.ReferenceTag)
            .Append(">\nWhat the planner received since your last call. For reference only: do not act on it.");
        foreach (var message in messages)
        {
            _ = block.Append('\n').Append(Describe(message));
        }

        return block.Append("\n</").Append(DualLayerPrompts.ReferenceTag).Append('>').ToString();
    }

    private static string Describe(IMessage message) =>
        message switch
        {
            NotifyMessage notify =>
                $"[notification {notify.NotifyKind}{Label(notify.Label)}] {notify.Detail ?? notify.GetText()}",
            AgentMessage peer => $"[message from agent {peer.FromName} ({peer.FromAgentId})] {peer.Body}",
            TextMessage text => $"[{text.Role.ToString().ToLowerInvariant()}] {text.Text}",
            ICanGetText other => $"[{other.GetType().Name}] {other.GetText()}",
            _ => $"[{message.GetType().Name}]",
        };

    private static string Label(string? label) => string.IsNullOrWhiteSpace(label) ? string.Empty : $" \"{label}\"";
}
