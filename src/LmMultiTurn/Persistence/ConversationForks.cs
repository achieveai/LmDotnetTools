using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn.Compaction;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>
/// Where to fork a conversation. Exactly one member is set.
/// </summary>
/// <param name="AfterRunId">Fork after the last message of this run - "fork from here" on a finished turn.</param>
/// <param name="AfterMessageId">Fork after exactly this message.</param>
/// <param name="BeforeMessageId">
/// Fork just before this USER message - "edit in fork": the fork's history ends at the message before
/// it, and the caller offers the user's old text for editing.
/// </param>
public sealed record ForkAnchor(
    string? AfterRunId = null,
    string? AfterMessageId = null,
    string? BeforeMessageId = null
);

/// <summary>The outcome of resolving a <see cref="ForkAnchor"/>: a fork point, or why there is none.</summary>
public sealed record ForkResolution
{
    /// <summary>Where the fork's history ends, when the anchor resolved.</summary>
    public ForkPoint? Point { get; init; }

    /// <summary>The run of the last shared message, carried to the fork as its latest run. Null when nothing is shared.</summary>
    public string? LatestRunId { get; init; }

    /// <summary>For a <see cref="ForkAnchor.BeforeMessageId"/> anchor, the user message being edited.</summary>
    public PersistedMessage? EditedMessage { get; init; }

    /// <summary>A <see cref="ForkRefusals"/> code when the anchor did not resolve.</summary>
    public string? Refusal { get; init; }
}

/// <summary>Why a fork was refused. The values are the wire codes the host reports.</summary>
public static class ForkRefusals
{
    /// <summary>No anchor, more than one, or a <see cref="ForkAnchor.BeforeMessageId"/> that is not a user message.</summary>
    public const string InvalidAnchor = "invalid_anchor";

    /// <summary>The anchor is not in the conversation's history.</summary>
    public const string AnchorNotFound = "anchor_not_found";

    /// <summary>The shared history would include a turn that is still running.</summary>
    public const string TurnInProgress = "turn_in_progress";

    /// <summary>
    /// A shared message still waits for a delayed tool result. Resolving it later would change a row
    /// the fork shares, so the fork is refused until it resolves.
    /// </summary>
    public const string PendingDelayedResult = "pending_delayed_result";
}

/// <summary>What <see cref="ConversationForks.DeleteAsync"/> did.</summary>
public enum ForkDeleteOutcome
{
    /// <summary>The conversation and its rows are gone.</summary>
    Deleted,

    /// <summary>Forks still share its messages: it is marked deleted and its rows are kept for them.</summary>
    Hidden,
}

/// <summary>
/// Creating and deleting forked conversations. A fork stores no copy of the messages it shares - see
/// <see cref="ConversationLineage"/> - so creation is one metadata write and deletion must keep any
/// messages another fork still reads.
/// </summary>
public static class ConversationForks
{
    private const int ListPageSize = 500;

    /// <summary>
    /// Resolves <paramref name="anchor"/> against the full history of <paramref name="sourceThreadId"/>.
    /// </summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="sourceThreadId">The conversation being forked.</param>
    /// <param name="anchor">Where to fork.</param>
    /// <param name="activeRunId">The run the source's agent is executing right now, if any.</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task<ForkResolution> ResolveAsync(
        IConversationStore store,
        string sourceThreadId,
        ForkAnchor anchor,
        string? activeRunId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(sourceThreadId);
        ArgumentNullException.ThrowIfNull(anchor);

        var set = new[] { anchor.AfterRunId, anchor.AfterMessageId, anchor.BeforeMessageId }.Count(a =>
            !string.IsNullOrEmpty(a)
        );
        if (set != 1)
        {
            return new ForkResolution { Refusal = ForkRefusals.InvalidAnchor };
        }

        var history = await store.LoadMessagesAsync(sourceThreadId, ct).ConfigureAwait(false);

        long cut;
        PersistedMessage? edited = null;
        if (!string.IsNullOrEmpty(anchor.AfterRunId))
        {
            var runRows = history.Where(m => m.RunId == anchor.AfterRunId && m.Seq is not null).ToList();
            if (runRows.Count == 0)
            {
                return new ForkResolution { Refusal = ForkRefusals.AnchorNotFound };
            }

            cut = runRows.Max(m => m.Seq!.Value);
        }
        else
        {
            var id = anchor.AfterMessageId ?? anchor.BeforeMessageId;
            var row = history.FirstOrDefault(m => m.Id == id);
            if (row?.Seq is not { } seq)
            {
                return new ForkResolution { Refusal = ForkRefusals.AnchorNotFound };
            }

            if (anchor.BeforeMessageId is not null)
            {
                if (!string.Equals(row.Role, nameof(Role.User), StringComparison.OrdinalIgnoreCase))
                {
                    return new ForkResolution { Refusal = ForkRefusals.InvalidAnchor };
                }

                edited = row;
                cut = seq - 1;
            }
            else
            {
                cut = seq;
            }
        }

        var shared = history.Where(m => m.Seq is { } s && s <= cut).ToList();

        if (activeRunId is not null && shared.Any(m => m.RunId == activeRunId))
        {
            return new ForkResolution { Refusal = ForkRefusals.TurnInProgress };
        }

        if (shared.Any(IsPendingDelayedResult))
        {
            return new ForkResolution { Refusal = ForkRefusals.PendingDelayedResult };
        }

        var last = shared.Count == 0 ? null : shared[^1];
        return new ForkResolution
        {
            Point = new ForkPoint
            {
                ThreadId = sourceThreadId,
                MessageId = last?.Id,
                Seq = cut,
            },
            LatestRunId = last?.RunId,
            EditedMessage = edited,
        };
    }

    /// <summary>
    /// Creates the fork <paramref name="forkThreadId"/> at <paramref name="resolution"/>'s point.
    /// </summary>
    /// <param name="store">The conversation store.</param>
    /// <param name="forkThreadId">The new conversation's id. Must not exist yet.</param>
    /// <param name="resolution">A successful <see cref="ResolveAsync"/> result.</param>
    /// <param name="seed">
    /// The new conversation's metadata as the host wants it - ownership, title, and the settings it
    /// chose to carry over. Its lineage, latest run and compaction state are set here.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The lineage written to the fork.</returns>
    public static async Task<ConversationLineage> CreateAsync(
        IConversationStore store,
        string forkThreadId,
        ForkResolution resolution,
        ThreadMetadata seed,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(forkThreadId);
        ArgumentNullException.ThrowIfNull(resolution);
        ArgumentNullException.ThrowIfNull(seed);
        var point =
            resolution.Point
            ?? throw new ArgumentException("The resolution carries no fork point.", nameof(resolution));

        var sourceMetadata = await store.LoadMetadataAsync(point.ThreadId, ct).ConfigureAwait(false);
        var lineage = new ConversationLineage
        {
            ForkedFrom = point,
            RootThreadId = ConversationLineage.Read(sourceMetadata)?.RootThreadId ?? point.ThreadId,
        };

        var metadata = ConversationLineage.Write(
            seed with
            {
                ThreadId = forkThreadId,
                CurrentRunId = null,
                LatestRunId = resolution.LatestRunId,
                SessionMappings = null,
            },
            lineage
        );

        await store
            .UpdateMetadataAsync(
                forkThreadId,
                existing =>
                    existing is null
                        ? metadata
                        : throw new InvalidOperationException($"Conversation '{forkThreadId}' already exists."),
                ct
            )
            .ConfigureAwait(false);

        var inherited = InheritedCompaction(CompactionStateProjection.FromMetadata(sourceMetadata), point.Seq);
        if (inherited is not null)
        {
            _ = await CompactionStateProjection
                .UpdateAsync(store, forkThreadId, _ => inherited, ct)
                .ConfigureAwait(false);
        }

        return lineage;
    }

    /// <summary>
    /// Deletes <paramref name="threadId"/>, unless forks still read its messages: then it is marked
    /// deleted and kept until its last fork is deleted. Deleting a fork also erases any deleted
    /// conversation up its chain that no fork needs any more.
    /// </summary>
    public static async Task<ForkDeleteOutcome> DeleteAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(threadId);

        if ((await FindDirectForksAsync(store, threadId, ct).ConfigureAwait(false)).Count > 0)
        {
            var deletedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            await store
                .UpdateMetadataAsync(
                    threadId,
                    existing =>
                    {
                        var current = existing ?? new ThreadMetadata { ThreadId = threadId, LastUpdated = deletedAt };
                        var lineage = ConversationLineage.Read(current) ?? new ConversationLineage();
                        return ConversationLineage.Write(current, lineage with { DeletedAt = deletedAt });
                    },
                    ct
                )
                .ConfigureAwait(false);
            return ForkDeleteOutcome.Hidden;
        }

        var parent = ConversationLineage
            .Read(await store.LoadMetadataAsync(threadId, ct).ConfigureAwait(false))
            ?.ForkedFrom?.ThreadId;
        await store.DeleteThreadAsync(threadId, ct).ConfigureAwait(false);

        while (parent is not null)
        {
            var parentLineage = ConversationLineage.Read(
                await store.LoadMetadataAsync(parent, ct).ConfigureAwait(false)
            );
            if (
                parentLineage?.DeletedAt is null
                || (await FindDirectForksAsync(store, parent, ct).ConfigureAwait(false)).Count > 0
            )
            {
                break;
            }

            await store.DeleteThreadAsync(parent, ct).ConfigureAwait(false);
            parent = parentLineage.ForkedFrom?.ThreadId;
        }

        return ForkDeleteOutcome.Deleted;
    }

    /// <summary>The conversations forked directly from <paramref name="threadId"/>, deleted-but-kept ones included.</summary>
    public static async Task<IReadOnlyList<string>> FindDirectForksAsync(
        IConversationStore store,
        string threadId,
        CancellationToken ct = default
    )
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(threadId);

        // A full walk, not a page: a fork missed here is a fork whose shared messages get erased.
        var forks = new List<string>();
        for (var offset = 0; ; offset += ListPageSize)
        {
            var page = await store.ListThreadsAsync(ListPageSize, offset, options: null, ct).ConfigureAwait(false);
            forks.AddRange(
                page.Where(m =>
                        string.Equals(
                            ConversationLineage.Read(m)?.ForkedFrom?.ThreadId,
                            threadId,
                            StringComparison.Ordinal
                        )
                    )
                    .Select(m => m.ThreadId)
            );

            if (page.Count < ListPageSize)
            {
                return forks;
            }
        }
    }

    /// <summary>
    /// The fork's starting compaction state, so it sees the shared history the way the source's model
    /// did: the latest checkpoint the source activated whose row lies in the shared history, and the
    /// tool-result clearing capped at the fork point (the fork's own results are never cleared by it).
    /// Null - raw history - when neither applies. Shared rows keep their ids and Seq, so nothing needs
    /// remapping.
    /// </summary>
    internal static CompactionState? InheritedCompaction(CompactionState? source, long forkSeq)
    {
        if (source is null)
        {
            return null;
        }

        var checkpoint = source
            .History.Where(e =>
                e.Status is CheckpointStatus.Active or CheckpointStatus.Superseded
                && e.RowSeq is { } row
                && row <= forkSeq
            )
            .MaxBy(e => e.RowSeq);
        long? cleared = source.ToolResultsClearedThroughSeq is { } through ? Math.Min(through, forkSeq) : null;

        return checkpoint is null && cleared is null
            ? null
            : new CompactionState
            {
                ActiveCheckpointId = checkpoint?.CheckpointId,
                ActiveBoundarySeq = checkpoint?.BoundarySeq,
                LastKnownGoodCheckpointId = checkpoint?.CheckpointId,
                History = checkpoint is null ? [] : [checkpoint with { Status = CheckpointStatus.Active }],
                ToolResultsClearedThroughSeq = cleared,
            };
    }

    private static bool IsPendingDelayedResult(PersistedMessage row)
    {
        if (!string.Equals(row.MessageType, nameof(ToolCallResultMessage), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            return MessagePersistenceConverter.FromPersistedMessage(row) is ToolCallResultMessage { IsDeferred: true };
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }
}
