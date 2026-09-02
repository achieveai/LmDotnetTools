using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using AchieveAi.LmDotnetTools.LmCore.Models;
using AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;
using Microsoft.Extensions.Logging;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

/// <summary>
/// Hands out the ordinals behind <c>agent-1</c>, <c>agent-2</c>, … for ONE root conversation (#705).
/// </summary>
/// <remarks>
/// <para>
/// One instance per root, shared down the hierarchy: the root's <see cref="SubAgentManager"/> creates
/// it and passes it to every child through the child's <see cref="SubAgentOptions"/>, so a grandchild
/// draws from the same sequence as its uncle and the numbers read in spawn order across the whole
/// conversation.
/// </para>
/// <para>
/// The counter is persisted in the root's <see cref="ThreadMetadata.Properties"/> under
/// <see cref="NextOrdinalProperty"/> through the store's atomic read-modify-write, which is what lets
/// a manager rebuilt after a restart continue at <c>agent-N+1</c>. Only a root that already has a
/// metadata row carries it: that row is the conversation's identity (tenant, owner, visibility) and is
/// minted by the host when the conversation is created, so inventing one here would create an unowned
/// conversation as a side effect of a spawn.
/// </para>
/// <para>
/// When there is no counter to continue from — no root row, or a row that has never been written by
/// this allocator — the transcripts already persisted under the root's scope
/// (<c>subagent-{scope}-agent-N</c>) are the only record of the numbers handed out before this
/// process, so the store is scanned once and numbering resumes above the highest of them. A child
/// transcript that has messages but no metadata row is invisible to that scan. Without a store at all,
/// numbering is in-process only, and still sequential.
/// </para>
/// <para>
/// Numbers are never reused. The in-process high-water mark is folded into the persisted value so a
/// root row that appears after the first spawn cannot restart the sequence at 1.
/// </para>
/// </remarks>
internal sealed class SubAgentOrdinalAllocator
{
    /// <summary>Library-owned metadata key holding the NEXT ordinal to hand out on the root thread.</summary>
    internal const string NextOrdinalProperty = "subagent_next_ordinal";

    /// <summary>Rows per page of the one-time scan in <see cref="ScanForHighestOrdinalAsync"/>.</summary>
    private const int ScanPageSize = 500;

    private readonly IConversationStore? _store;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _last;
    private int? _scannedHighest;

    public SubAgentOrdinalAllocator(string? rootThreadId, IConversationStore? store, ILogger logger)
    {
        RootThreadId = rootThreadId;
        _store = string.IsNullOrWhiteSpace(rootThreadId) ? null : store;
        _logger = logger;
    }

    /// <summary>The root conversation this sequence belongs to (null for a parent with no thread).</summary>
    public string? RootThreadId { get; }

    /// <summary>Allocates the next ordinal. Serialized, so concurrent spawns number in allocation order.</summary>
    public async ValueTask<int> AllocateAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var ordinal = _last + 1;
            if (_store is not null)
            {
                ordinal = await AllocateThroughStoreAsync(ordinal, ct).ConfigureAwait(false);
            }

            _last = ordinal;
            return ordinal;
        }
        finally
        {
            _ = _gate.Release();
        }
    }

    /// <summary>
    /// Allocates against the store: through the root's metadata row when there is one, otherwise
    /// in-process above whatever the scan finds (see the class remarks). <paramref name="floor"/> is the
    /// in-process candidate; the persisted value wins when it is higher (a restart), the floor wins when
    /// the row is behind (a row created after spawns already happened).
    /// </summary>
    private async Task<int> AllocateThroughStoreAsync(int floor, CancellationToken ct)
    {
        var rootThreadId = RootThreadId!;
        var existing = await _store!.LoadMetadataAsync(rootThreadId, ct).ConfigureAwait(false);
        if (existing is null || !HasCounter(existing))
        {
            // Nothing persisted says how far the sequence got before this process; the transcripts do.
            floor = Math.Max(floor, await ScanForHighestOrdinalAsync(ct).ConfigureAwait(false) + 1);
        }

        if (existing is null)
        {
            _logger.LogDebug(
                "Root thread {RootThreadId} has no metadata row; sub-agent ordinals continue in-process from {Ordinal}",
                rootThreadId,
                floor
            );
            return floor;
        }

        var allocated = 0;
        await _store
            .UpdateMetadataAsync(
                rootThreadId,
                current =>
                {
                    // The row was read moments ago; if it vanished in between, write back the one we saw
                    // rather than fabricating a bare row with no identity columns.
                    var row = current ?? existing;
                    allocated = Math.Max(ReadNext(row), floor);
                    var properties = row.Properties?.ToBuilder() ?? ImmutableDictionary.CreateBuilder<string, object>();
                    properties[NextOrdinalProperty] = allocated + 1;
                    return row with { Properties = properties.ToImmutable() };
                },
                ct
            )
            .ConfigureAwait(false);

        return allocated;
    }

    private static bool HasCounter(ThreadMetadata row) => row.Properties?.ContainsKey(NextOrdinalProperty) == true;

    /// <summary>
    /// The highest ordinal among the transcripts already persisted under this root's scope, or 0 when
    /// there are none. Computed once per allocator; every page of the listing is read because the
    /// listing is ordered by last-updated, and an old conversation's children can sit behind any
    /// number of newer threads.
    /// </summary>
    private async Task<int> ScanForHighestOrdinalAsync(CancellationToken ct)
    {
        if (_scannedHighest is { } known)
        {
            return known;
        }

        var scopedPrefix =
            $"{SubAgentThreadIds.Prefix}{SubAgentThreadIds.ScopeTag(RootThreadId)}-{SubAgentThreadIds.AgentIdPrefix}";
        var highest = 0;
        for (var offset = 0; ; offset += ScanPageSize)
        {
            var page = await _store!.ListThreadsAsync(ScanPageSize, offset, options: null, ct).ConfigureAwait(false);
            foreach (var row in page)
            {
                if (
                    row.ThreadId.StartsWith(scopedPrefix, StringComparison.Ordinal)
                    && int.TryParse(
                        row.ThreadId.AsSpan(scopedPrefix.Length),
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var ordinal
                    )
                )
                {
                    highest = Math.Max(highest, ordinal);
                }
            }

            if (page.Count < ScanPageSize)
            {
                break;
            }
        }

        _logger.LogDebug(
            "Scanned the store for sub-agent transcripts under root {RootThreadId}; highest ordinal {Highest}",
            RootThreadId,
            highest
        );
        _scannedHighest = highest;
        return highest;
    }

    /// <summary>
    /// The persisted next ordinal, tolerating the numeric-JSON round-trip (a file/SQLite store hands the
    /// value back as a <see cref="JsonElement"/>, an in-memory one as the <see cref="int"/> that was
    /// written). Absent or unreadable means the sequence has not started: 1.
    /// </summary>
    private static int ReadNext(ThreadMetadata row)
    {
        if (row.Properties?.TryGetValue(NextOrdinalProperty, out var value) != true || value is null)
        {
            return 1;
        }

        var next = value switch
        {
            int i => i,
            long l when l is >= 1 and <= int.MaxValue => (int)l,
            JsonElement je when je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out var parsed) => parsed,
            string s when int.TryParse(s, out var parsed) => parsed,
            _ => 1,
        };
        return next < 1 ? 1 : next;
    }
}
