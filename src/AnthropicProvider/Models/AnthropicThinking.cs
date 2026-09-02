using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.AnthropicProvider.Models;

/// <summary>
///     Configuration for Claude's extended thinking capability.
/// </summary>
/// <remarks>
///     Two request shapes exist. The classic shape (<see cref="Enabled" />) is
///     <c>{"type":"enabled","budget_tokens":N}</c>, used by api.anthropic.com and Copilot's classic
///     Claude models. Claude adaptive-thinking models (Opus 5, Sonnet 5, Opus 4.7+) reject that shape
///     with HTTP 400 and instead take <see cref="Adaptive" />'s
///     <c>{"type":"adaptive","display":"summarized"}</c> — <c>budget_tokens</c> must be omitted, not
///     zero. On those models Anthropic's <c>display</c> defaults to <c>"omitted"</c>, which returns a
///     thinking block with an empty <c>thinking</c> field and only a signature; asking for
///     <c>"summarized"</c> is what makes the thinking text come back (#709). <c>"summarized"</c> is the
///     only display this SDK ever requests — never <c>"updates"</c> or raw chain-of-thought.
/// </remarks>
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
    ///     The classic <c>type: "enabled"</c> shape with an explicit token budget.
    /// </summary>
    /// <param name="budgetTokens">The budget for thinking tokens. Defaults to 1024.</param>
    public static AnthropicThinking Enabled(int budgetTokens = 1024) =>
        new() { Type = "enabled", BudgetTokens = budgetTokens };

    /// <summary>
    ///     The <c>type: "adaptive"</c> shape required by Claude adaptive-thinking models. Omits
    ///     <c>budget_tokens</c> (those models reject it) and opts into a displayable summary rather
    ///     than Anthropic's default <c>"omitted"</c> display, which returns empty thinking text (#709).
    /// </summary>
    /// <param name="display">Always <c>"summarized"</c> in practice — never raw chain-of-thought.</param>
    public static AnthropicThinking Adaptive(string display = "summarized") =>
        new() { Type = "adaptive", Display = display };

    /// <summary>
    ///     The type of thinking: <c>"enabled"</c> (classic, budget-based) or <c>"adaptive"</c>
    ///     (Claude adaptive-thinking models; paired with <see cref="Display" />).
    /// </summary>
    [JsonPropertyName("type")]
    public string Type { get; init; } = "enabled";

    /// <summary>
    ///     The budget for thinking tokens. Should be at least 1024 for models that support thinking.
    ///     Omitted from the request when null — adaptive-thinking models reject this field entirely.
    /// </summary>
    /// <remarks>
    ///     Deliberately has NO property initializer: a record built through an object initializer skips
    ///     the constructors and factories below, and a defaulted 1024 would then ride along on an
    ///     adaptive request that the model rejects with HTTP 400. The budget-taking constructors and
    ///     <see cref="Enabled" /> set it explicitly instead.
    /// </remarks>
    [JsonPropertyName("budget_tokens")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? BudgetTokens { get; init; }

    /// <summary>
    ///     How much of the model's thinking an adaptive-thinking model returns: <c>"summarized"</c>
    ///     (readable summary text) or <c>"omitted"</c> (Anthropic's default — an empty thinking field
    ///     and only a signature). Omitted from the request when null, so it stays inert for the classic
    ///     <see cref="Enabled" /> shape and for any caller that does not set it.
    /// </summary>
    [JsonPropertyName("display")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Display { get; init; }
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
    /// <summary>
    ///     Reasoning effort — e.g. <c>"low"</c>, <c>"medium"</c>, <c>"high"</c>, <c>"max"</c>.
    ///     Defaults to <c>"high"</c>: that is the depth DeepSeek's Anthropic-compatible endpoint
    ///     assumes for a regular request, so an <see cref="AnthropicOutputConfig" /> constructed
    ///     without an explicit effort asks for the same thing rather than sending <c>effort: null</c>.
    /// </summary>
    [JsonPropertyName("effort")]
    public string? Effort { get; init; } = "high";
}
