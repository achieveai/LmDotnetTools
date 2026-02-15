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

        return new PersistedMessage
        {
            Id = Guid.NewGuid().ToString("N"),
            ThreadId = message.ThreadId ?? threadId,
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
