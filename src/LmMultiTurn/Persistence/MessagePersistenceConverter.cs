using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Utils;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Converts between IMessage and PersistedMessage for storage and retrieval.
/// </summary>
public static class MessagePersistenceConverter
{
    private static readonly JsonSerializerOptions DefaultOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false,
        Converters = { new IMessageJsonConverter() },
    };

    /// <summary>
    /// Converts an IMessage to a PersistedMessage for storage.
    /// </summary>
    /// <param name="message">The message to convert.</param>
    /// <param name="threadId">The thread ID (uses message.ThreadId if available, otherwise this value).</param>
    /// <param name="runId">The run ID (uses message.RunId if available, otherwise this value).</param>
    /// <param name="jsonOptions">Optional JSON serializer options. Defaults to snake_case with IMessageJsonConverter.</param>
    /// <returns>A PersistedMessage ready for storage.</returns>
    public static PersistedMessage ToPersistedMessage(
        IMessage message,
        string threadId,
        string runId,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(message);

        var options = jsonOptions ?? DefaultOptions;
        var messageJson = JsonSerializer.Serialize(message, message.GetType(), options);
        var effectiveThreadId = message.ThreadId ?? threadId;

        return new PersistedMessage
        {
            // Deterministic Id for ToolCallResultMessage with a non-empty ToolCallId so that
            // ReplaceMessageAsync can address the row without an in-memory index. Other message
            // types keep a random Id since they're append-only.
            Id = BuildPersistedId(message, effectiveThreadId),
            ThreadId = effectiveThreadId,
            RunId = message.RunId ?? runId,
            ParentRunId = message.ParentRunId,
            GenerationId = message.GenerationId,
            MessageOrderIdx = message.MessageOrderIdx,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            MessageType = message.GetType().Name,
            Role = message.Role.ToString(),
            FromAgent = message.FromAgent,
            MessageJson = messageJson,
        };
    }

    /// <summary>
    /// Constructs the deterministic persisted-Id for a <see cref="ToolCallResultMessage"/> with
    /// a non-empty ToolCallId. Used by <c>MultiTurnAgentBase</c> when calling
    /// <see cref="IConversationStore.ReplaceMessageAsync"/> on deferred-tool resolution.
    /// </summary>
    public static string BuildToolResultPersistedId(string threadId, string toolCallId)
    {
        ArgumentException.ThrowIfNullOrEmpty(threadId);
        ArgumentException.ThrowIfNullOrEmpty(toolCallId);
        return $"tcr:{threadId}:{toolCallId}";
    }

    private static string BuildPersistedId(IMessage message, string threadId)
    {
        if (message is ToolCallResultMessage tcr && !string.IsNullOrEmpty(tcr.ToolCallId))
        {
            return BuildToolResultPersistedId(threadId, tcr.ToolCallId);
        }

        return Guid.NewGuid().ToString("N");
    }

    /// <summary>
    /// Converts a PersistedMessage back to an IMessage.
    /// </summary>
    /// <param name="persisted">The persisted message to convert.</param>
    /// <param name="jsonOptions">Optional JSON serializer options. Defaults to snake_case with IMessageJsonConverter.</param>
    /// <returns>The deserialized IMessage.</returns>
    /// <exception cref="JsonException">Thrown if deserialization fails.</exception>
    public static IMessage FromPersistedMessage(
        PersistedMessage persisted,
        JsonSerializerOptions? jsonOptions = null)
    {
        ArgumentNullException.ThrowIfNull(persisted);

        var options = jsonOptions ?? DefaultOptions;

        var message = JsonSerializer.Deserialize<IMessage>(persisted.MessageJson, options);

        return message ?? throw new JsonException($"Failed to deserialize message {persisted.Id}");
    }

    /// <summary>
    /// Converts multiple IMessages to PersistedMessages.
    /// </summary>
    public static IReadOnlyList<PersistedMessage> ToPersistedMessages(
        IEnumerable<IMessage> messages,
        string threadId,
        string runId,
        JsonSerializerOptions? jsonOptions = null)
    {
        return
        [
            .. messages.Select(m => ToPersistedMessage(m, threadId, runId, jsonOptions))
        ];
    }

    /// <summary>
    /// Converts multiple PersistedMessages back to IMessages.
    /// </summary>
    public static IReadOnlyList<IMessage> FromPersistedMessages(
        IEnumerable<PersistedMessage> persistedMessages,
        JsonSerializerOptions? jsonOptions = null)
    {
        return
        [
            .. persistedMessages.Select(p => FromPersistedMessage(p, jsonOptions))
        ];
    }
}
