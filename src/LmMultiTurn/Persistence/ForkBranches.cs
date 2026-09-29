namespace AchieveAi.LmDotnetTools.LmMultiTurn.Persistence;

/// <summary>One conversation of a fork family, as <see cref="ForkBranches.Compute"/> sees it.</summary>
/// <param name="ThreadId">The conversation.</param>
/// <param name="ForkedFrom">Where it was forked from; null for the family's original.</param>
/// <param name="Watermark">The highest Seq of its full history.</param>
/// <param name="Visible">Whether the viewer may be offered it: readable and not deleted.</param>
public sealed record BranchNode(string ThreadId, ForkPoint? ForkedFrom, long Watermark, bool Visible);

/// <summary>A switchable continuation at a <see cref="BranchPoint"/>.</summary>
/// <param name="ThreadId">The conversation to open.</param>
/// <param name="Current">Whether the viewed conversation continues this way.</param>
public sealed record BranchOption(string ThreadId, bool Current);

/// <summary>A message on the viewed conversation's path after which two or more conversations continue.</summary>
/// <param name="AfterMessageId">The message the continuations follow; null before the first message.</param>
/// <param name="AfterSeq">Its Seq; 0 before the first message.</param>
/// <param name="Options">The continuations: the one that owns the message first, then its forks by creation.</param>
public sealed record BranchPoint(string? AfterMessageId, long AfterSeq, IReadOnlyList<BranchOption> Options);

/// <summary>
/// The branch switcher's data: where, along the viewed conversation's history, a fork family splits.
/// Pure over the family's lineage and watermarks, so it needs no message reads.
/// </summary>
public static class ForkBranches
{
    /// <summary>
    /// The branch points along <paramref name="currentThreadId"/>'s history, ascending by Seq. A point
    /// is reported only when at least two of its continuations can be offered.
    /// </summary>
    /// <param name="currentThreadId">The conversation being viewed.</param>
    /// <param name="family">
    /// Every conversation of its family, the viewer's unreadable ones included (marked not visible):
    /// the path through them decides where the viewed conversation's rows come from.
    /// </param>
    /// <param name="creationOrder">Orders forks sharing a point; typically their creation time.</param>
    public static IReadOnlyList<BranchPoint> Compute(
        string currentThreadId,
        IReadOnlyCollection<BranchNode> family,
        IComparer<string>? creationOrder = null
    )
    {
        ArgumentNullException.ThrowIfNull(currentThreadId);
        ArgumentNullException.ThrowIfNull(family);

        var nodes = family.ToDictionary(n => n.ThreadId, StringComparer.Ordinal);
        if (!nodes.ContainsKey(currentThreadId))
        {
            return [];
        }

        // The path: the viewed conversation, then the conversation that owns the row each was forked
        // after - not the one it was forked from, which may only share that row and so supplies none of
        // the history. Each segment supplies the rows with Seq in (the next segment's hi, hi].
        var path = new List<(string ThreadId, long Hi)>();
        var hi = nodes[currentThreadId].Watermark;
        for (var id = currentThreadId; id is not null && path.All(p => p.ThreadId != id); )
        {
            path.Add((id, hi));
            var point = nodes.TryGetValue(id, out var node) ? node.ForkedFrom : null;
            hi = point?.Seq ?? 0;
            id = point is null ? null : Owner(nodes, point.ThreadId, point.Seq);
        }

        // Group every fork by the row it follows: the conversation that OWNS that row (a fork of a
        // fork taken inside shared history belongs to the older conversation's point) and its Seq.
        var points = new Dictionary<(string Owner, long Seq), List<BranchNode>>();
        foreach (var fork in nodes.Values.Where(n => n.ForkedFrom is not null))
        {
            var key = (Owner(nodes, fork.ForkedFrom!.ThreadId, fork.ForkedFrom.Seq), fork.ForkedFrom.Seq);
            if (!points.TryGetValue(key, out var forks))
            {
                points[key] = forks = [];
            }

            forks.Add(fork);
        }

        var result = new List<BranchPoint>();
        for (var i = 0; i < path.Count; i++)
        {
            var (ownerId, segmentHi) = path[i];
            var segmentLo = i + 1 < path.Count ? path[i + 1].Hi : 0;
            var below = i > 0 ? path[i - 1].ThreadId : null;

            foreach (var ((owner, seq), forks) in points)
            {
                if (owner != ownerId || seq < segmentLo || seq > segmentHi)
                {
                    continue;
                }

                // The path leaves the owner here exactly when the conversation below it on the path is
                // one of these forks; otherwise it carries on in the owner. Either way the option the
                // path takes opens the viewed conversation itself - it may be a fork further down.
                // The owner is offered even with nothing after the point, as an empty fork is: forking
                // the last reply must still lead back.
                var viaFork = below is not null && forks.Any(f => f.ThreadId == below) ? below : null;
                var options = new List<BranchOption>();

                if (viaFork is null)
                {
                    options.Add(new BranchOption(currentThreadId, true));
                }
                else if (nodes[ownerId].Visible)
                {
                    options.Add(new BranchOption(ownerId, false));
                }

                foreach (
                    var fork in forks.OrderBy(f => f.ThreadId, creationOrder ?? Comparer<string>.Create((_, _) => 0))
                )
                {
                    if (fork.ThreadId == viaFork)
                    {
                        options.Add(new BranchOption(currentThreadId, true));
                    }
                    else if (fork.Visible)
                    {
                        options.Add(new BranchOption(fork.ThreadId, false));
                    }
                }

                if (options.Count >= 2)
                {
                    result.Add(new BranchPoint(forks[0].ForkedFrom!.MessageId, seq, options));
                }
            }
        }

        return [.. result.OrderBy(p => p.AfterSeq)];
    }

    /// <summary>The conversation whose own row has <paramref name="seq"/> in <paramref name="threadId"/>'s history.</summary>
    private static string Owner(Dictionary<string, BranchNode> nodes, string threadId, long seq)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        while (
            seen.Add(threadId)
            && nodes.TryGetValue(threadId, out var node)
            && node.ForkedFrom is { } point
            && seq <= point.Seq
        )
        {
            threadId = point.ThreadId;
        }

        return threadId;
    }
}
