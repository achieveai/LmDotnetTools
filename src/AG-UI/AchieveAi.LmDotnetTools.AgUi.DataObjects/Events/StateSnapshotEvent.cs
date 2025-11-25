using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
///     Provides complete state representation at a point in time
/// </summary>
public sealed record StateSnapshotEvent : AgUiEventBase
{
    /// <inheritdoc />
    [JsonIgnore]
    public override string Type => "STATE_SNAPSHOT";

    /// <summary>
    ///     Complete state dictionary
    /// </summary>
    [JsonPropertyName("state")]
    public ImmutableDictionary<string, object> State { get; init; } = ImmutableDictionary<string, object>.Empty;

    /// <summary>
    ///     Version number of this state snapshot
    /// </summary>
    [JsonPropertyName("version")]
    public int Version { get; init; }

    /// <summary>
    ///     Version timestamp of this state snapshot (UTC)
    /// </summary>
    [JsonPropertyName("versionTimestamp")]
    public DateTime VersionTimestamp { get; init; } = DateTime.UtcNow;
}
