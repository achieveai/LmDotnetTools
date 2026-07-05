using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
///     Configuration for Claude's extended thinking capability.
/// </summary>
public record AnthropicThinking
{
    /// <summary>
    ///     Creates a new instance of the AnthropicThinking record with default values.
    /// </summary>
    public AnthropicThinking() { }

    /// <summary>
    ///     Creates a new instance of the AnthropicThinking record with the specified budget tokens.
    /// </summary>
    /// <param name="budgetTokens">The budget for thinking tokens.</param>
    public AnthropicThinking(int budgetTokens)
    {
        BudgetTokens = budgetTokens;
    }

    /// <summary>
    ///     Creates a new instance of the AnthropicThinking record with the specified type and budget tokens.
    /// </summary>
    /// <param name="type">The type of thinking.</param>
    /// <param name="budgetTokens">The budget for thinking tokens.</param>
    public AnthropicThinking(string type, int budgetTokens)
    {
        Type = type;
        BudgetTokens = budgetTokens;
    }

    /// <summary>
    ///     The type of thinking. Currently only "enabled" is supported.
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "enabled";

    /// <summary>
    ///     The budget for thinking tokens. Should be at least 1024 for models that support thinking.
    /// </summary>
    [JsonPropertyName("budget_tokens")]
    public int BudgetTokens { get; init; } = 1024;
}

/// <summary>
///     Output configuration for a request. Currently carries <c>effort</c>, the control the GitHub
///     Copilot backend's <em>adaptive-thinking</em> Claude models expose (via <c>output_config.effort</c>)
///     to bound how much the model reasons before answering. Those models reject the classic
///     <c>thinking.type=enabled</c>/<c>budget_tokens</c> knobs; a low effort keeps reasoning short so the
///     answer is not starved of the token budget. Omitted from the request when not set, so it is inert
///     for api.anthropic.com and any caller that does not supply it.
/// </summary>
public record AnthropicOutputConfig
{
    /// <summary>Reasoning effort — e.g. <c>"low"</c>, <c>"medium"</c>, <c>"high"</c>.</summary>
    [JsonPropertyName("effort")]
    public string? Effort { get; init; }
}
