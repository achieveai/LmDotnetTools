using System.Text.Json.Serialization;

namespace AchieveAi.LmDotnetTools.LmLifecycle.Payloads;

/// <summary>
/// Token usage measured for a turn or a run.
/// </summary>
/// <remarks>
/// <para>
/// Counts are reported exactly as the provider gave them and are never reconstructed by summing or
/// subtracting other fields. Providers disagree about what "prompt tokens" includes — some report
/// total input, others report only the uncached part — so a consumer that derives one field from
/// the others will be wrong for at least one provider. Read the fields the provider populated and
/// treat the rest as unknown.
/// </para>
/// <para>
/// <see cref="Completeness"/> says how much to trust the numbers. Anything other than
/// <see cref="LifecycleUsageCompleteness.Complete"/> means at least one contributing response
/// reported no usage, so the totals understate reality.
/// </para>
/// </remarks>
public sealed record LifecycleUsage
{
    /// <summary>Input tokens, as reported by the provider.</summary>
    [JsonPropertyName("prompt_tokens")]
    public long PromptTokens { get; set; }

    /// <summary>Output tokens, as reported by the provider.</summary>
    [JsonPropertyName("completion_tokens")]
    public long CompletionTokens { get; set; }

    /// <summary>Total tokens, as reported by the provider.</summary>
    [JsonPropertyName("total_tokens")]
    public long TotalTokens { get; set; }

    /// <summary>
    /// Input tokens served from a provider-side cache, when the provider reports them. Absent means
    /// the provider did not report the figure, not that it was zero.
    /// </summary>
    [JsonPropertyName("cached_prompt_tokens")]
    public long? CachedPromptTokens { get; set; }

    /// <summary>
    /// Tokens spent on reasoning, when the provider reports them separately. Absent means the
    /// provider did not report the figure, not that it was zero.
    /// </summary>
    [JsonPropertyName("reasoning_tokens")]
    public long? ReasoningTokens { get; set; }

    /// <summary>
    /// How complete the measurement is. See <see cref="LifecycleUsageCompleteness"/>. Open
    /// vocabulary.
    /// </summary>
    [JsonPropertyName("completeness")]
    public string Completeness { get; set; } = LifecycleUsageCompleteness.InProgress;
}
