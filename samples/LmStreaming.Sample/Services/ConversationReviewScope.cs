using System.Globalization;
using AchieveAi.LmDotnetTools.LmMultiTurn.Audit;
using AchieveAi.LmDotnetTools.LmMultiTurn.Lifecycle;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using LmStreaming.Sample.Models;
using LmStreaming.Sample.Persistence;

namespace LmStreaming.Sample.Services;

/// <summary>Persists and restores the immutable review engagement scope bound to a hosted conversation.</summary>
public static class ConversationReviewScope
{
    public const string EngagementIdPropertyKey = "review.engagementId";
    public const string RoundIdPropertyKey = "review.roundId";
    public const string BridgeBaseUrlConfigKey = "ReviewBridge:BaseUrl";
    public const string BridgeSecretConfigKey = "ReviewBridge:Secret";

    public static ReviewConversationScope? Validate(ReviewConversationScope? scope)
    {
        if (scope is null)
        {
            return null;
        }

        ValidateId(scope.EngagementId, nameof(scope.EngagementId));
        ValidateId(scope.RoundId, nameof(scope.RoundId));
        return scope;
    }

    public static async Task<ReviewConversationScope?> ReadAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentException.ThrowIfNullOrWhiteSpace(threadId);

        var metadata = await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false);
        if (metadata?.Properties is not { } properties)
        {
            return null;
        }

        var hasEngagement = properties.TryGetValue(EngagementIdPropertyKey, out var rawEngagement);
        var hasRound = properties.TryGetValue(RoundIdPropertyKey, out var rawRound);
        if (hasEngagement != hasRound)
        {
            throw new InvalidOperationException("Review engagement and round scope must be stored together.");
        }

        if (!hasEngagement)
        {
            return null;
        }

        return Validate(
            new ReviewConversationScope(
                ThreadPropertyValue.AsString(rawEngagement)
                    ?? throw new InvalidOperationException("Stored review engagement ID must be a string."),
                ThreadPropertyValue.AsString(rawRound)
                    ?? throw new InvalidOperationException("Stored review round ID must be a string.")
            )
        );
    }

    public static MultiTurnLifecycleServices DeriveLifecycleServices(
        MultiTurnLifecycleServices? hostServices,
        IMultiTurnAuditSink? auditSink,
        ReviewConversationScope? scope,
        string providerId,
        string? modeId = null
    )
    {
        if (scope is null)
        {
            return hostServices ?? MultiTurnLifecycleServices.Disabled;
        }

        if (
            modeId is not null
            && !string.Equals(modeId, SystemChatModes.CodeReviewDaemonModeId, StringComparison.Ordinal)
        )
        {
            throw new InvalidOperationException("Review scope is valid only for the code-review-daemon mode.");
        }

        if (auditSink is null)
        {
            throw new InvalidOperationException(
                "A review-scoped conversation requires a configured review audit bridge."
            );
        }
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var validated = Validate(scope)!;
        return (hostServices ?? new MultiTurnLifecycleServices()) with
        {
            AuditSink = auditSink,
            AuditScope = new MultiTurnAuditScope(validated.EngagementId, validated.RoundId),
            ProviderId = providerId,
        };
    }

    private static void ValidateId(string value, string parameterName)
    {
        if (
            string.IsNullOrWhiteSpace(value)
            || !long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _)
        )
        {
            throw new ArgumentException("Review scope IDs must be nonblank numeric strings.", parameterName);
        }
    }
}
