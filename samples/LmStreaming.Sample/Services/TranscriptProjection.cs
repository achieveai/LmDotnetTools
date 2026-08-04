using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>
///     Shapes persisted transcripts for every reader that is handed one.
/// </summary>
/// <remarks>
///     There is exactly one of these because a transcript is a disclosure boundary: the conversation's
///     own <c>/messages</c> route, the cross-agent transcript endpoint, and the
///     <c>GetAgentTranscript</c> tool must apply the SAME normalization and the SAME exclusions. Split
///     across three call sites, one of them eventually forgets — and the thing it forgets is reasoning.
///     <para>
///     A fourth caller, the workspace transcript mirror (#251), passes <c>excludeReasoning: false</c>. It
///     is NOT exempted from normalization — it is normalized like every other reader, and deliberately
///     includes reasoning: the mirror writes the conversation's own full-fidelity record into its own
///     workspace, which is the one read that is not cross-agent. See ADR
///     <c>0011-workspace-transcript-files</c>.
///     </para>
/// </remarks>
public static class TranscriptProjection
{
    private static readonly JsonSerializerOptions MessageJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new IMessageJsonConverter() },
    };

    /// <summary>
    ///     Normalizes persisted messages so discriminators are consistent (e.g. legacy
    ///     <c>server_tool_use</c> → <c>tool_call</c> with an execution target), optionally dropping the
    ///     agent's own reasoning.
    /// </summary>
    /// <remarks>
    ///     Reasoning is excluded from every cross-agent read (#244). An agent's private deliberation is
    ///     the one part of a transcript that was never addressed to anybody, so it is filtered here, at
    ///     the single place transcripts are shaped, rather than at each caller that might forget.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="messages"/> is null.</exception>
    public static List<PersistedMessage> Normalize(
        IReadOnlyList<PersistedMessage> messages,
        bool excludeReasoning)
    {
        ArgumentNullException.ThrowIfNull(messages);

        var normalized = new List<PersistedMessage>(messages.Count);
        foreach (var m in messages)
        {
            var msg = TryDeserialize(m);
            if (msg == null)
            {
                // An unparseable row is passed through exactly as stored: the reader that has always
                // tolerated it keeps working, and nothing is silently dropped.
                normalized.Add(m);
                continue;
            }

            if (excludeReasoning && IsReasoning(msg))
            {
                continue;
            }

            try
            {
                // Fix legacy "{}{"query":"..."}" args from the content_block_start bug.
                msg = FixLegacyDoubledArgs(msg);
                normalized.Add(m with { MessageJson = JsonSerializer.Serialize(msg, msg.GetType(), MessageJson) });
            }
            catch
            {
                normalized.Add(m);
            }
        }

        return normalized;
    }

    /// <summary>
    ///     Deserializes one persisted row, or null when it cannot be read as a message. Exposed so a
    ///     reader that renders a transcript (rather than re-serializing it) uses the same converters.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="message"/> is null.</exception>
    public static IMessage? TryDeserialize(PersistedMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        try
        {
            return JsonSerializer.Deserialize<IMessage>(message.MessageJson, MessageJson);
        }
        catch
        {
            // Any failure to read a stored row is treated as "unreadable", never as a request failure:
            // one bad row must not take down a whole transcript that has always rendered.
            return null;
        }
    }

    /// <summary>Whether a message is an agent's own deliberation, in either its delta or final form.</summary>
    public static bool IsReasoning(IMessage message) => message is ReasoningMessage or ReasoningUpdateMessage;

    /// <summary>
    ///     Fixes legacy persisted messages where content_block_start leaked "{}" into FunctionArgs,
    ///     producing invalid JSON like {}{"query":"..."}.
    /// </summary>
    private static IMessage FixLegacyDoubledArgs(IMessage msg) =>
        msg switch
        {
            ToolCallMessage tc when NeedsArgsFix(tc.FunctionArgs) =>
                tc with { FunctionArgs = StripLeadingEmptyObject(tc.FunctionArgs!) },
            _ => msg,
        };

    private static bool NeedsArgsFix(string? args) =>
        args is not null && args.StartsWith("{}{", StringComparison.Ordinal);

    private static string StripLeadingEmptyObject(string args) => args[2..]; // Remove leading "{}"
}
