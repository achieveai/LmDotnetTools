using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AgUi.DataObjects.Events;

/// <summary>
/// Reports errors during processing
/// </summary>
public sealed record ErrorEvent : AgUiEventBase
{
    /// <inheritdoc/>
    [JsonIgnore]
    public override string Type => "RUN_ERROR";

    /// <summary>
    /// Error code for categorization
    /// </summary>
    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>
    /// Human-readable error message
    /// </summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    /// <summary>
    /// Additional error details
    /// </summary>
    [JsonPropertyName("details")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ImmutableDictionary<string, object>? Details { get; init; }

    /// <summary>
    /// Whether the error is recoverable and processing can continue
    /// </summary>
    [JsonPropertyName("recoverable")]
    public bool Recoverable { get; init; }
}
