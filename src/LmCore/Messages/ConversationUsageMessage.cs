using System.Collections.Immutable;
using System.Text.Json.Serialization;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmCore.Messages;

/// <summary>
///     A live-only usage frame carrying the conversation-wide token totals (and cost, when known) folded
///     across the WHOLE conversation tree — the primary loop's own turns plus every sub-agent / workflow
///     descendant (#196). Broadcast to the parent run's subscribers whenever the folded aggregate changes,
///     so the client usage banner reflects descendant spend live rather than only after a reload of the
///     persisted aggregate. Implements <see cref="ITransientMessage" />: it is never buffered, added to
///     history, or persisted — the authoritative figure survives reload via the persisted aggregate.
/// </summary>
/// <remarks>
///     The token fields are the exact banner tuple, pre-computed server-side (including the per-model
///     <c>uncachedInput</c> normalization) so the client SETs the banner from them directly and the live
///     view matches the reload view by construction. Field names are fixed camelCase via
///     <see cref="JsonPropertyNameAttribute" /> so the wire shape is stable regardless of the serializer's
///     naming policy.
/// </remarks>
public sealed record ConversationUsageMessage : IMessage, ITransientMessage
{
    /// <summary>Grand total tokens across every model in the conversation tree.</summary>
    [JsonPropertyName("totalTokens")]
    public long TotalTokens { get; init; }

    /// <summary>Summed billed input (prompt) tokens, including the cached-read subset.</summary>
    [JsonPropertyName("promptTokens")]
    public long PromptTokens { get; init; }

    /// <summary>
    ///     Fresh (non-cached) input tokens: summed per model row as <c>input - cacheRead</c> when the cache
    ///     read is a subset of input, else the full input (the additive-cache-read case). Matches the
    ///     client's <c>uncachedInput</c> rule so live and reload agree.
    /// </summary>
    [JsonPropertyName("uncachedInputTokens")]
    public long UncachedInputTokens { get; init; }

    /// <summary>Summed billed output (completion) tokens.</summary>
    [JsonPropertyName("completionTokens")]
    public long CompletionTokens { get; init; }

    /// <summary>Summed cached-read tokens (a subset of <see cref="PromptTokens" />).</summary>
    [JsonPropertyName("cachedTokens")]
    public long CachedTokens { get; init; }

    /// <summary>Summed cache-creation tokens (billed separately, additive to the total).</summary>
    [JsonPropertyName("cacheCreationTokens")]
    public long CacheCreationTokens { get; init; }

    /// <summary>Completeness of the folded aggregate ("InProgress" / "Partial" / "Complete").</summary>
    [JsonPropertyName("completeness")]
    public string Completeness { get; init; } = nameof(UsageCompleteness.InProgress);

    /// <summary>Public-pricing cost estimate in micro-units, or null when unavailable.</summary>
    [JsonPropertyName("estimatedCostMicros")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? EstimatedCostMicros { get; init; }

    /// <summary>Provider-reported cost in micro-units, or null when the provider reports none.</summary>
    [JsonPropertyName("providerReportedCostMicros")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? ProviderReportedCostMicros { get; init; }

    /// <summary>ISO currency code for the cost figures.</summary>
    [JsonPropertyName("currency")]
    public string Currency { get; init; } = "USD";

    /// <summary>The role associated with this frame (assistant, matching other loop-emitted messages).</summary>
    [JsonPropertyName("role")]
    public Role Role { get; init; } = Role.Assistant;

    /// <summary>The name or identifier of the agent that produced this usage frame.</summary>
    [JsonPropertyName("fromAgent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? FromAgent { get; init; }

    /// <summary>Not carried on transient usage frames.</summary>
    [JsonIgnore]
    public ImmutableDictionary<string, object>? Metadata { get; init; }

    /// <summary>Not carried on transient usage frames.</summary>
    [JsonPropertyName("generationId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? GenerationId { get; init; }

    /// <summary>The conversation thread this aggregate belongs to.</summary>
    [JsonPropertyName("threadId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ThreadId { get; init; }

    /// <summary>The run this frame was emitted during, when known.</summary>
    [JsonPropertyName("runId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RunId { get; init; }

    /// <summary>
    ///     Projects a folded <see cref="ConversationUsageAggregate" /> into the banner tuple, applying the
    ///     client's per-model <c>uncachedInput</c> rule so the live frame and the reload transform produce
    ///     identical banner figures.
    /// </summary>
    /// <param name="aggregate">The folded conversation-wide aggregate.</param>
    /// <param name="threadId">The root conversation id this aggregate belongs to.</param>
    public static ConversationUsageMessage FromAggregate(ConversationUsageAggregate aggregate, string threadId)
    {
        ArgumentNullException.ThrowIfNull(aggregate);

        long prompt = 0, uncached = 0, completion = 0, cached = 0, cacheCreation = 0;
        foreach (var row in aggregate.PerModel)
        {
            prompt += row.InputTokens;
            completion += row.OutputTokens;
            cached += row.CacheReadTokens;
            cacheCreation += row.CacheWriteTokens;

            // Per-row uncached-input rule (mirrors the client): the cache read is normally a subset of
            // input (OpenAI family), so uncached = input - cacheRead; when a provider reports cache reads
            // additively (cacheRead > input, Anthropic family) fall back to the full input and never go
            // negative. Done per row before summing so a mixed set is handled correctly.
            uncached += row.CacheReadTokens <= row.InputTokens
                ? row.InputTokens - row.CacheReadTokens
                : row.InputTokens;
        }

        return new ConversationUsageMessage
        {
            ThreadId = threadId,
            TotalTokens = aggregate.TotalTokens,
            PromptTokens = prompt,
            UncachedInputTokens = uncached,
            CompletionTokens = completion,
            CachedTokens = cached,
            CacheCreationTokens = cacheCreation,
            Completeness = aggregate.Completeness.ToString(),
            EstimatedCostMicros = aggregate.EstimatedPublicCostMicros,
            ProviderReportedCostMicros = aggregate.ProviderReportedCostMicros,
            Currency = aggregate.Currency,
        };
    }
}
