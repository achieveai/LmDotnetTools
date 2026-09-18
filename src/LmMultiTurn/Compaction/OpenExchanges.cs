using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

/// <summary>
///     RC3 (eval spec §4): the exchanges still open at a cut. <b>Inbound</b> — a Question or DelegateTask row at
///     or before the cut with no Response citing it anywhere in the thread, so an answer already in the tail
///     closes it. <b>Outbound</b> — a <c>SendMessage</c> receipt for a question or delegate_task at or before the
///     cut with no Response citing its message id. A descendant-question notification stays open until the same
///     agent's completion notification. Never blocks a cut: it only says what the manifest has to pin.
/// </summary>
internal static class OpenExchanges
{
    /// <summary>Longest body excerpt one entry carries.</summary>
    public const int SummaryChars = 200;

    private const string SendMessageTool = "SendMessage";

    /// <summary>The exchanges open at <paramref name="cutSeq" />, in the order their rows opened them.</summary>
    public static IReadOnlyList<OpenExchangeRef> Find(IReadOnlyList<SequencedMessage> rows, long cutSeq)
    {
        ArgumentNullException.ThrowIfNull(rows);

        var answered = rows.Select(r => r.Message)
            .OfType<AgentMessage>()
            .Where(m => m.AgentMessageType == AgentMessageType.Response)
            .Select(m => m.InResponseTo)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
        var completedAgents = rows.Select(r => r.Message)
            .OfType<NotifyMessage>()
            .Where(n => n.NotifyKind == NotifyKinds.SubAgentCompletion)
            .Select(n => n.SourceToolCallId)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        // What each outbound SendMessage call said, by tool call id: its receipt carries ids, not the content.
        var contentByCall = new Dictionary<string, string>(StringComparer.Ordinal);

        var open = new List<OpenExchangeRef>();
        foreach (var row in rows)
        {
            if (row.Seq > cutSeq)
            {
                break;
            }

            switch (row.Message)
            {
                case AgentMessage { ExpectsReply: true } ask when !answered.Contains(ask.MessageId):
                    open.Add(
                        new OpenExchangeRef
                        {
                            MessageId = ask.MessageId,
                            Direction = "inbound",
                            From = ask.FromAgentId,
                            Seq = row.Seq,
                            AskedAtRun = row.EffectiveRunId,
                            Summary = Cap(ask.Body),
                        }
                    );
                    break;
                case NotifyMessage { NotifyKind: NotifyKinds.DescendantQuestion } question
                    when question.SourceToolCallId is { Length: > 0 } agent && !completedAgents.Contains(agent):
                    // The notification carries no message id of its own, so the row that raised it names the entry.
                    open.Add(
                        new OpenExchangeRef
                        {
                            MessageId = $"descendant:{agent}:{row.Seq}",
                            Direction = "inbound",
                            From = agent,
                            Seq = row.Seq,
                            AskedAtRun = row.EffectiveRunId,
                            Summary = Cap(question.Detail ?? question.Label),
                        }
                    );
                    break;
                default:
                    break; // Every other row kind opens no exchange.
            }

            foreach (var call in ToolRows.CallsOf(row.Message))
            {
                if (
                    call.ToolCallId is { Length: > 0 } id
                    && string.Equals(call.FunctionName, SendMessageTool, StringComparison.OrdinalIgnoreCase)
                    && StringArg(call.FunctionArgs, "content") is { } content
                )
                {
                    contentByCall[id] = content;
                }
            }

            foreach (var result in ToolRows.ResultsOf(row.Message))
            {
                if (
                    result.ToolCallId is not { Length: > 0 } callId
                    || !contentByCall.TryGetValue(callId, out var content)
                    || Receipt(result.Result) is not { } receipt
                )
                {
                    continue;
                }

                if (receipt.MsgType is "question" or "delegate_task" && !answered.Contains(receipt.MessageId))
                {
                    open.Add(
                        new OpenExchangeRef
                        {
                            MessageId = receipt.MessageId,
                            Direction = "outbound",
                            From = "self",
                            To = receipt.ToAgentId,
                            Seq = row.Seq,
                            AskedAtRun = row.EffectiveRunId,
                            Summary = Cap(content),
                        }
                    );
                }
            }
        }

        return open;
    }

    private static string Cap(string? text)
    {
        text = (text ?? string.Empty).Trim();
        return text.Length <= SummaryChars ? text : text[..SummaryChars];
    }

    private sealed record ReceiptDto(string MessageId, string? ToAgentId, string? MsgType);

    /// <summary>The accepted receipt's ids, or null when the result is not one.</summary>
    private static ReceiptDto? Receipt(string? result)
    {
        if (string.IsNullOrWhiteSpace(result))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result);
            var root = document.RootElement;
            if (
                root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("status", out var status)
                || status.GetString() != "accepted"
                || !root.TryGetProperty("message_id", out var id)
                || id.GetString() is not { Length: > 0 } messageId
            )
            {
                return null;
            }

            return new ReceiptDto(
                messageId,
                root.TryGetProperty("to_agent_id", out var to) ? to.GetString() : null,
                root.TryGetProperty("msg_type", out var type) ? type.GetString() : null
            );
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? StringArg(string? args, string name)
    {
        if (string.IsNullOrWhiteSpace(args))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(args);
            return
                document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty(name, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
