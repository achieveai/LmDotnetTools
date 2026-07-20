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
    ///     Builds a record from a usage message. <paramref name="ownerExecutionId" /> is the emitter's id
    ///     (the root thread id for the primary loop, the sub-agent id for a descendant) and forms the
    ///     dedup key together with the message's generation id.
    /// </summary>
    public static UsageRecord FromUsageMessage(
        UsageMessage message,
        string ownerExecutionId,
        UsageExecutionKind kind,
        string? model)
    {
        ArgumentNullException.ThrowIfNull(message);

        var usage = message.Usage;

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
            RequestedModel = string.IsNullOrEmpty(model) ? "unknown" : model,
            InputTokens = usage.PromptTokens,
            OutputTokens = usage.CompletionTokens,
            CacheReadTokens = usage.TotalCachedTokens,
            // Cache-creation tokens are billed separately (additive to the total). Anthropic and related
            // providers surface them via Usage.ExtraProperties; 0 when absent.
            CacheWriteTokens = usage.GetExtraProperty<int>("cache_creation_input_tokens"),
            ReasoningTokens = usage.TotalReasoningTokens,
            ProviderReportedCostMicros = ToMicros(usage.TotalCost),
            Finalized = true,
        };
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
            $"derived:{usage.PromptTokens}-{usage.CompletionTokens}-{usage.TotalCachedTokens}-{usage.TotalReasoningTokens}-{usage.TotalCost ?? 0d}-{order}");
    }

    private static long? ToMicros(double? cost)
    {
        return cost is null ? null : (long)Math.Round(cost.Value * 1_000_000d);
    }
}
