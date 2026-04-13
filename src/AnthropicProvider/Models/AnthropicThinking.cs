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
