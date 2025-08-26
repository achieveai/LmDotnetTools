using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmConfig.Capabilities;

/// <summary>
/// Represents the thinking/reasoning capabilities of a model.
/// </summary>
public record ThinkingCapability
{
    /// <summary>
    /// The type of thinking capability supported by the model.
    /// </summary>
    [JsonPropertyName("type")]
    public ThinkingType Type { get; init; } = ThinkingType.None;

    /// <summary>
    /// Whether the model supports configurable budget tokens for thinking.
    /// </summary>
    [JsonPropertyName("supports_budget_tokens")]
    public bool SupportsBudgetTokens { get; init; } = false;

    /// <summary>
    /// Whether the model supports different thinking types/modes.
    /// </summary>
    [JsonPropertyName("supports_thinking_type")]
    public bool SupportsThinkingType { get; init; } = false;

    /// <summary>
    /// Maximum number of thinking tokens the model can use.
    /// </summary>
    [JsonPropertyName("max_thinking_tokens")]
    public int? MaxThinkingTokens { get; init; }

    /// <summary>
    /// Whether thinking is built into the model (cannot be disabled).
    /// </summary>
    [JsonPropertyName("is_built_in")]
    public bool IsBuiltIn { get; init; } = false;

    /// <summary>
    /// Whether thinking content is exposed in the response.
    /// </summary>
    [JsonPropertyName("is_exposed")]
    public bool IsExposed { get; init; } = false;

    /// <summary>
    /// The parameter name used to control thinking in API requests.
    /// </summary>
    [JsonPropertyName("parameter_name")]
    public string? ParameterName { get; init; }

    /// <summary>
    /// Additional thinking-specific parameters or configuration options.
    /// </summary>
    [JsonPropertyName("custom_parameters")]
    public IDictionary<string, object>? CustomParameters { get; init; }
}

/// <summary>
/// Defines the types of thinking capabilities available.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ThinkingType
{
    /// <summary>
    /// No thinking capability.
    /// </summary>
    None,

    /// <summary>
    /// Anthropic-style thinking with configurable parameters.
    /// </summary>
    Anthropic,

    /// <summary>
    /// DeepSeek-style built-in reasoning.
    /// </summary>
    DeepSeek,

    /// <summary>
    /// OpenAI O1-style built-in reasoning.
    /// </summary>
    OpenAI,

    /// <summary>
    /// Custom or provider-specific thinking implementation.
    /// </summary>
    Custom,
}
