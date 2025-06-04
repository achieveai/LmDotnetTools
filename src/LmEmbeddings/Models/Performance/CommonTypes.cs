using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmEmbeddings.Models;

/// <summary>
/// Represents a time period for performance metrics
/// </summary>
public record TimePeriod
{
    /// <summary>
    /// Start of the time period
    /// </summary>
    [JsonPropertyName("start")]
    public DateTime Start { get; init; }

    /// <summary>
    /// End of the time period
    /// </summary>
    [JsonPropertyName("end")]
    public DateTime End { get; init; }

    /// <summary>
    /// Duration in seconds
    /// </summary>
    [JsonPropertyName("duration_seconds")]
    public double DurationSeconds => (End - Start).TotalSeconds;

    /// <summary>
    /// Human-readable description of the period
    /// </summary>
    [JsonPropertyName("description")]
    public string? Description { get; init; }
}

/// <summary>
/// Direction of a performance trend
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrendDirection
{
    /// <summary>
    /// Metric is improving
    /// </summary>
    Improving,

    /// <summary>
    /// Metric is stable
    /// </summary>
    Stable,

    /// <summary>
    /// Metric is degrading
    /// </summary>
    Degrading,

    /// <summary>
    /// Trend is unknown or inconsistent
    /// </summary>
    Unknown
} 