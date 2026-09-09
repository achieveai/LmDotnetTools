using System.Globalization;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.UsageAccounting;

/// <summary>
///     Maps a provider <see cref="UsageMessage" /> to a durable <see cref="UsageRecord" /> for the usage
///     ledger. Shared by the primary loop's own-usage capture and the sub-agent/workflow relay so both
///     produce identically-shaped records (#196). Token counts are captured verbatim (per-call field
///     accuracy is #116); the <c>RootConversationId</c> placeholder is re-stamped by the ledger.
/// </summary>
public static class UsageRecordMapper
{
    /// <summary>
    ///     The <see cref="LmCore.Models.Usage.ExtraProperties" /> key a provider uses to report the model it
    ///     actually served. Written today only by <c>OpenRouterUsageMiddleware</c> from OpenRouter's generation
    ///     stats, where auto-routing and fallback can serve a model other than the requested one; every other
    ///     provider reports none, and those records keep <see cref="UsageRecord.EffectiveModel" /> null.
    /// </summary>
    public const string ProviderReportedModel = "model";

    /// <summary>
    ///     Builds a record from a usage message. <paramref name="ownerExecutionId" /> is the emitter's id
    ///     (the root thread id for the primary loop, the sub-agent id for a descendant) and forms the
    ///     dedup key together with the message's generation id.
    /// </summary>
    /// <param name="message">The provider usage message to map.</param>
    /// <param name="ownerExecutionId">The emitting execution's id.</param>
    /// <param name="kind">How this attempt was produced.</param>
    /// <param name="model">
    ///     The model the caller asked this execution to run — the sub-agent's resolved model for a descendant,
    ///     the loop's configured model for the primary. Becomes <see cref="UsageRecord.RequestedModel" />; the
    ///     model the provider says it actually served is read off the usage payload (see
    ///     <see cref="ProviderReportedModel" />). Null/empty when unknown.
    /// </param>
    /// <param name="timeProvider">
    ///     Clock used to stamp <see cref="UsageRecord.OccurredAtUtc" />. Neither
    ///     <see cref="UsageMessage" /> nor <see cref="LmCore.Models.Usage" /> carries a provider timestamp, so
    ///     observation time is the best available attribution — and it is injected rather than read from
    ///     <c>DateTimeOffset.UtcNow</c> so the stamp is assertable. Defaults to
    ///     <see cref="TimeProvider.System" />.
    /// </param>
    public static UsageRecord FromUsageMessage(
        UsageMessage message,
        string ownerExecutionId,
        UsageExecutionKind kind,
        string? model,
        TimeProvider? timeProvider = null
    )
    {
        ArgumentNullException.ThrowIfNull(message);

        var usage = message.Usage;
        var providerReportedCostMicros = ToMicros(usage.TotalCost);
        var requestedModel = string.IsNullOrEmpty(model) ? "unknown" : model;

        // A generation is one provider call; combined with the emitter id it is a stable, globally unique
        // dedup key across the conversation tree. Fall back to the run id, then — when the producer supplies
        // neither — to a key derived from the call's observable usage scope, so distinct provider calls are
        // not silently collapsed into one attempt (a shared constant would MAX-merge and undercount) while an
        // exact replay of the same observation still dedups. A provider-minted attempt id (#116-adjacent)
        // remains the fully-correct source.
        var attemptKey = message.GenerationId ?? message.RunId ?? DeriveCallScopeKey(message);
        var attemptId = $"{ownerExecutionId}:{attemptKey}";

        return new UsageRecord
        {
            LogicalCallId = attemptId,
            ProviderAttemptId = attemptId,
            RootConversationId = ownerExecutionId,
            ParentExecutionId = kind == UsageExecutionKind.Primary ? null : ownerExecutionId,
            ExecutionKind = kind,
            RequestedModel = requestedModel,
            EffectiveModel = EffectiveModelOf(usage, requestedModel),
            InputTokens = usage.PromptTokens,
            OutputTokens = usage.CompletionTokens,
            CacheReadTokens = usage.TotalCachedTokens,
            // Cache-creation tokens are billed separately (additive to the total). Anthropic and related
            // providers surface them via Usage.ExtraProperties; 0 when absent.
            CacheWriteTokens = usage.GetExtraProperty<int>("cache_creation_input_tokens"),
            ReasoningTokens = usage.TotalReasoningTokens,
            ProviderReportedCostMicros = providerReportedCostMicros,
            CostProvenance = providerReportedCostMicros is not null
                ? CostProvenance.ProviderReported
                : CostProvenance.Unavailable,
            OccurredAtUtc = (timeProvider ?? TimeProvider.System).GetUtcNow(),
            Finalized = true,
        };
    }

    /// <summary>
    ///     The provider-reported model when it is known AND differs from what was requested, else null —
    ///     matching <see cref="UsageRecord.EffectiveModel" />'s "when it differs" contract, so the documented
    ///     <see cref="UsageRecord.EffectiveModelId" /> fallback keeps answering for the (overwhelmingly common)
    ///     providers that report no model at all. Never defaulted to the requested model: that would make the
    ///     field look delivered while carrying no provider observation.
    /// </summary>
    private static string? EffectiveModelOf(Usage usage, string requestedModel)
    {
        var reported = usage.GetExtraProperty<string>(ProviderReportedModel);
        return string.IsNullOrEmpty(reported) || string.Equals(reported, requestedModel, StringComparison.Ordinal)
            ? null
            : reported;
    }

    /// <summary>
    ///     Derives a dedup key from a usage message's observable scope when the producer supplies no
    ///     generation or run id. Deterministic, so an exact replay dedups; distinct-usage calls get distinct
    ///     keys, so they are counted separately rather than MAX-collapsed by a shared constant.
    /// </summary>
    private static string DeriveCallScopeKey(UsageMessage message)
    {
        var usage = message.Usage;
        var order = message.MessageOrderIdx?.ToString(CultureInfo.InvariantCulture) ?? "-";
        return FormattableString.Invariant(
            $"derived:{usage.PromptTokens}-{usage.CompletionTokens}-{usage.TotalCachedTokens}-{usage.TotalReasoningTokens}-{usage.TotalCost ?? 0d}-{order}"
        );
    }

    private static long? ToMicros(double? cost)
    {
        return cost is null ? null : (long)Math.Round(cost.Value * 1_000_000d);
    }
}
