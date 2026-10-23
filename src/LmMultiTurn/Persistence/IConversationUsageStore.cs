using AchieveAi.LmDotnetTools.LmCore.Models;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>Canonical usage state and records loaded from a conversation store.</summary>
public sealed record ConversationUsageSnapshot
{
    /// <summary>Schema version of the persisted usage representation.</summary>
    public required int SchemaVersion { get; init; }

    /// <summary>Highest complete record revision folded into the state.</summary>
    public required long FoldedRevision { get; init; }

    /// <summary>Whether usage collection is in progress, partial, or complete.</summary>
    public required UsageCompleteness Completeness { get; init; }

    /// <summary>The persisted aggregate when no canonical records exist; otherwise null.</summary>
    public ConversationUsageAggregate? Aggregate { get; init; }

    /// <summary>Canonical records, one per provider attempt.</summary>
    public required IReadOnlyList<UsageRecord> Records { get; init; }
}

/// <summary>
/// Optional store capability for persisting usage records independently from conversation metadata.
/// </summary>
public interface IConversationUsageStore
{
    /// <summary>Appends changed records and monotonically advances their folded state.</summary>
    Task AppendUsageAsync(
        ConversationUsageAggregate aggregate,
        IReadOnlyList<UsageRecord> changedRecords,
        CancellationToken ct = default
    );

    /// <summary>Loads canonical usage state and records, or null when none has been persisted.</summary>
    Task<ConversationUsageSnapshot?> LoadUsageAsync(string threadId, CancellationToken ct = default);
}
