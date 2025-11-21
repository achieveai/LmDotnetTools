using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects;

/// <summary>
/// Base class for all AG-UI protocol events.
/// All events inherit from this class and must specify their event type.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(SessionStartedEvent), AgUiEventTypes.SESSION_STARTED)]
[JsonDerivedType(typeof(RunStartedEvent), AgUiEventTypes.RUN_STARTED)]
[JsonDerivedType(typeof(RunFinishedEvent), AgUiEventTypes.RUN_FINISHED)]
[JsonDerivedType(typeof(ErrorEvent), AgUiEventTypes.RUN_ERROR)]
[JsonDerivedType(typeof(TextMessageStartEvent), AgUiEventTypes.TEXT_MESSAGE_START)]
[JsonDerivedType(typeof(TextMessageContentEvent), AgUiEventTypes.TEXT_MESSAGE_CONTENT)]
[JsonDerivedType(typeof(TextMessageEndEvent), AgUiEventTypes.TEXT_MESSAGE_END)]
[JsonDerivedType(typeof(ToolCallStartEvent), AgUiEventTypes.TOOL_CALL_START)]
[JsonDerivedType(typeof(ToolCallArgumentsEvent), AgUiEventTypes.TOOL_CALL_ARGS)]
[JsonDerivedType(typeof(ToolCallEndEvent), AgUiEventTypes.TOOL_CALL_END)]
[JsonDerivedType(typeof(ToolCallResultEvent), AgUiEventTypes.TOOL_CALL_RESULT)]
[JsonDerivedType(typeof(StepStartedEvent), AgUiEventTypes.STEP_STARTED)]
[JsonDerivedType(typeof(StepFinishedEvent), AgUiEventTypes.STEP_FINISHED)]
[JsonDerivedType(typeof(StateSnapshotEvent), AgUiEventTypes.STATE_SNAPSHOT)]
[JsonDerivedType(typeof(StateDeltaEvent), AgUiEventTypes.STATE_DELTA)]
public abstract record AgUiEventBase
{
    /// <summary>
    /// The type of the event (e.g., "RUN_STARTED", "TEXT_MESSAGE_CONTENT", etc.)
    /// Uses SCREAMING_SNAKE_CASE per AG-UI protocol specification.
    /// This property is ignored during serialization - the JsonPolymorphic attribute handles the "type" discriminator.
    /// </summary>
    [JsonIgnore]
    public abstract string Type { get; }

    /// <summary>
    /// Unix timestamp in milliseconds when this event occurred (official protocol field)
    /// </summary>
    [JsonPropertyName("timestamp")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? Timestamp { get; init; }

    /// <summary>
    /// Raw event data for extensibility (official protocol field)
    /// </summary>
    [JsonPropertyName("rawEvent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? RawEvent { get; init; }

    /// <summary>
    /// Unique identifier for this event instance (extension field for internal tracking)
    /// </summary>
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString();

    /// <summary>
    /// Session identifier linking this event to a specific conversation session (extension field)
    /// </summary>
    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    /// <summary>
    /// Optional correlation ID for tracking related events across services (extension field)
    /// </summary>
    [JsonPropertyName("correlationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Thread identifier for conversation continuity (CopilotKit protocol field)
    /// Maps to a persistent conversation thread across multiple runs
    /// </summary>
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    /// <summary>
    /// Run identifier for this specific agent execution (CopilotKit protocol field)
    /// Tracks individual runs within a conversation thread
    /// </summary>
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }
}
