using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Serialization;

/// <summary>
/// Custom JSON converter for polymorphic AG-UI event serialization/deserialization
/// </summary>
public class AgUiEventConverter : JsonConverter<AgUiEventBase>
{
    /// <summary>
    /// Reads and converts the JSON to an AG-UI event
    /// </summary>
    public override AgUiEventBase? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        if (!root.TryGetProperty("type", out var typeElement))
        {
            throw new JsonException("Missing 'type' property in AG-UI event");
        }

        var type = typeElement.GetString();

        return type switch
        {
            // Lifecycle events (AG-UI protocol SCREAMING_SNAKE_CASE)
            "RUN_STARTED" => JsonSerializer.Deserialize<RunStartedEvent>(root.GetRawText(), options),
            "RUN_FINISHED" => JsonSerializer.Deserialize<RunFinishedEvent>(root.GetRawText(), options),
            "RUN_ERROR" => JsonSerializer.Deserialize<ErrorEvent>(root.GetRawText(), options),

            // Text message events
            "TEXT_MESSAGE_START" => JsonSerializer.Deserialize<TextMessageStartEvent>(root.GetRawText(), options),
            "TEXT_MESSAGE_CONTENT" => JsonSerializer.Deserialize<TextMessageContentEvent>(root.GetRawText(), options),
            "TEXT_MESSAGE_END" => JsonSerializer.Deserialize<TextMessageEndEvent>(root.GetRawText(), options),

            // Tool call events
            "TOOL_CALL_START" => JsonSerializer.Deserialize<ToolCallStartEvent>(root.GetRawText(), options),
            "TOOL_CALL_ARGS" => JsonSerializer.Deserialize<ToolCallArgumentsEvent>(root.GetRawText(), options),
            "TOOL_CALL_END" => JsonSerializer.Deserialize<ToolCallEndEvent>(root.GetRawText(), options),

            // State events
            "STATE_SNAPSHOT" => JsonSerializer.Deserialize<StateSnapshotEvent>(root.GetRawText(), options),
            "STATE_DELTA" => JsonSerializer.Deserialize<StateDeltaEvent>(root.GetRawText(), options),

            // Legacy kebab-case support (for backward compatibility during transition)
            "run-started" => JsonSerializer.Deserialize<RunStartedEvent>(root.GetRawText(), options),
            "run-finished" => JsonSerializer.Deserialize<RunFinishedEvent>(root.GetRawText(), options),
            "text-message-start" => JsonSerializer.Deserialize<TextMessageStartEvent>(root.GetRawText(), options),
            "text-message-content" => JsonSerializer.Deserialize<TextMessageContentEvent>(root.GetRawText(), options),
            "text-message-end" => JsonSerializer.Deserialize<TextMessageEndEvent>(root.GetRawText(), options),
            "tool-call-start" => JsonSerializer.Deserialize<ToolCallStartEvent>(root.GetRawText(), options),
            "tool-call-arguments" => JsonSerializer.Deserialize<ToolCallArgumentsEvent>(root.GetRawText(), options),
            "tool-call-end" => JsonSerializer.Deserialize<ToolCallEndEvent>(root.GetRawText(), options),
            "state-snapshot" => JsonSerializer.Deserialize<StateSnapshotEvent>(root.GetRawText(), options),
            "state-delta" => JsonSerializer.Deserialize<StateDeltaEvent>(root.GetRawText(), options),
            "error" => JsonSerializer.Deserialize<ErrorEvent>(root.GetRawText(), options),

            _ => throw new JsonException($"Unknown AG-UI event type: {type}")
        };
    }

    /// <summary>
    /// Writes the AG-UI event to JSON
    /// </summary>
    public override void Write(Utf8JsonWriter writer, AgUiEventBase value, JsonSerializerOptions options)
    {
        // Serialize the concrete type to preserve all properties
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
    }
}
