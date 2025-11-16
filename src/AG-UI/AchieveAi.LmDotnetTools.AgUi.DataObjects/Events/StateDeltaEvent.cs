using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Provides incremental state updates (only changed values)
/// </summary>
public sealed record StateDeltaEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonPropertyName("type")]
    public override string Type => "STATE_DELTA";

    /// <summary>
    /// Dictionary of state changes (null values indicate deletion)
    /// </summary>
    [JsonPropertyName("changes")]
    public ImmutableDictionary<string, object?> Changes { get; init; } = ImmutableDictionary<string, object?>.Empty;

    /// <summary>
    /// Version number before applying this delta
    /// </summary>
    [JsonPropertyName("fromVersion")]
    public int FromVersion { get; init; }

    /// <summary>
    /// Version number after applying this delta
    /// </summary>
    [JsonPropertyName("toVersion")]
    public int ToVersion { get; init; }
}
