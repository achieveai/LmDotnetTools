using System;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Emitted when a new WebSocket session is established
/// </summary>
public sealed record SessionStartedEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => "SESSION_STARTED";

    /// <summary>
    /// Timestamp when the session was started (UTC)
    /// </summary>
    [JsonPropertyName("startedAt")]
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
}
