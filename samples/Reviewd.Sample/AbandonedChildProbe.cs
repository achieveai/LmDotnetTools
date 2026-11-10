using System.Collections.Concurrent;
using CodeReviewDaemon.Sample.Agents;

namespace Reviewd.Sample;

/// <summary>
///     Settles a sub-agent that still reads Running after its run was abandoned (#956). The host can leave a child
///     stamped running for good (#957), and before this every wait and the reaper waited on it forever: seen live, a
///     whole ADO round waited 13 hours on one, and a GitHub slot stayed held. The cheap rule in
///     <see cref="ReviewRunner.IsSettledNode"/> stays as it is; this one is asked only when nothing else can still move
///     the tree: the root is idle, its input is final or there is none, and only Running children are left.
/// </summary>
/// <remarks>
///     A Running child counts as abandoned only when its last turn is finished (see <see cref="LastTurnFinished"/>) and
///     its newest message is at least <see cref="QuietThreshold"/> old. Its LastActivityUtc cannot say this (see
///     <see cref="ReviewRunner.IsSettledNode"/>), and its run-state never reads in progress even while it works, so the
///     signal is the child's transcript, read through the parent-reader route the S2S client is allowed. That read loads
///     every message on the host's busy store, so a child found not abandoned is not read again for
///     <see cref="ProbeInterval"/>, nor before its newest message could be old enough. A child found abandoned is read
///     again at every check, so a child that wrote since is never settled from an old read. The review wait, admission
///     and the reaper opt in by passing a probe; the judge and merge waits pass none and keep the old rule. It never
///     decides success: that is still the root input's status and verify.
/// </remarks>
internal sealed class AbandonedChildProbe(string subject, AbandonedChildProbe.ReadLog? log = null)
{
    /// <summary>
    ///     What a probe remembers between checks: when each child that was not abandoned may next be read, and which
    ///     abandoned children were already reported. A batch shares <see cref="Process"/> across every review attempt,
    ///     so a deferred admission retried within minutes does not read the same transcript again.
    /// </summary>
    internal sealed class ReadLog
    {
        internal static ReadLog Process { get; } = new();

        internal ConcurrentDictionary<string, Reading> Readings { get; } = new(StringComparer.Ordinal);

        internal ConcurrentDictionary<string, byte> Warned { get; } = new(StringComparer.Ordinal);
    }

    internal sealed record Reading(DateTimeOffset NextReadAt);

    // A live child can be quiet for a long time inside one model call or between tool calls, so this is far past
    // any normal gap. A child inside a long tool call (a build, a test run) is never settled by time: its open tool
    // call keeps it running however long it takes. The ghosts seen live were 8 and 13 hours quiet.
    internal static readonly TimeSpan QuietThreshold = TimeSpan.FromMinutes(45);

    // The least time between two transcript reads of a child found not abandoned.
    internal static readonly TimeSpan ProbeInterval = TimeSpan.FromMinutes(5);

    private readonly ReadLog _log = log ?? new ReadLog();

    /// <summary>True when every node in <paramref name="unsettled"/> is a Running child that was abandoned.</summary>
    internal async Task<bool> AllAbandonedAsync(
        LmStreamingS2SClient client,
        string rootThread,
        IReadOnlyList<ReviewSubAgentNode> unsettled,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        // Everything that can say "still live" without a read is asked first: any other unsettled status, a recent
        // activity stamp (it can be stale, but never ahead of the child), or a read that is not yet due.
        if (
            unsettled.Count == 0
            || unsettled.Any(node =>
                node.Status != ReviewSubAgentStatus.Running
                || (node.LastActivityUtc is { } lastActivity && now - lastActivity < QuietThreshold)
                || (_log.Readings.TryGetValue(node.ThreadId, out var reading) && now < reading.NextReadAt)
            )
        )
            return false;
        var newest = new Dictionary<string, DateTimeOffset?>(StringComparer.Ordinal);
        foreach (var node in unsettled)
        {
            // One live child keeps the tree unsettled, so the rest are not read.
            if (await ReadAsync(client, rootThread, node, now, ct) is not { } abandonedAt)
                return false;
            newest[node.ThreadId] = abandonedAt;
        }
        foreach (var node in unsettled)
        {
            if (_log.Warned.TryAdd(node.ThreadId, 0))
                Console.Error.WriteLine(
                    $"{subject}: sub-agent {node.AgentId} (thread {node.ThreadId}, under {rootThread}) still reads "
                        + $"running, but its last turn finished and its newest message is from {newest[node.ThreadId]:O}; "
                        + "treating it as abandoned, so it no longer holds the wait or the slot."
                );
        }
        return true;
    }

    // The child's newest message time when it reads as abandoned now; otherwise null, with its next read scheduled.
    private async Task<DateTimeOffset?> ReadAsync(
        LmStreamingS2SClient client,
        string rootThread,
        ReviewSubAgentNode node,
        DateTimeOffset now,
        CancellationToken ct
    )
    {
        IReadOnlyList<ReviewAgentTranscriptEntry> transcript;
        try
        {
            transcript = await client.GetAgentTranscriptAsync(rootThread, node.AgentId, ct);
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Unread is not abandoned: the child keeps the wait, as it did before #956, and is read again later. A
            // client timeout lands here too; only the caller's own cancellation escapes.
            Console.Error.WriteLine(
                $"{subject}: could not read sub-agent {node.AgentId} (thread {node.ThreadId}) to see whether it was "
                    + $"abandoned, so it still counts as running: {failure.Message}"
            );
            _log.Readings[node.ThreadId] = new Reading(now + ProbeInterval);
            return null;
        }
        var newest = transcript.Max(entry => entry.TimestampUtc);
        if (IsAbandoned(newest, LastTurnFinished(transcript), now))
        {
            // Never reused: the next check reads again, so a child that wrote since is not settled from this read.
            _ = _log.Readings.TryRemove(node.ThreadId, out _);
            return newest;
        }
        // The child cannot read as abandoned before its newest message is QuietThreshold old.
        var next = now + ProbeInterval;
        if (newest is { } stamp && stamp + QuietThreshold > next)
            next = stamp + QuietThreshold;
        _log.Readings[node.ThreadId] = new Reading(next);
        return null;
    }

    /// <summary>The abandoned rule: a finished last turn, and a newest message at least the threshold old.</summary>
    internal static bool IsAbandoned(DateTimeOffset? newestMessageUtc, bool turnFinished, DateTimeOffset now) =>
        turnFinished && newestMessageUtc is { } newest && now - newest >= QuietThreshold;

    /// <summary>
    ///     Whether a child's transcript ends on a finished turn: its last message other than usage is the assistant's
    ///     text, and every tool call has a result with its own id. Both ghosts seen live ended that way. A child that
    ///     ends on a tool call, a tool result, or anything sent to it is mid-turn, however quiet, so it keeps the wait.
    ///     A tool call or result whose id cannot be read, or a tool message of another shape, fails closed.
    /// </summary>
    internal static bool LastTurnFinished(IReadOnlyList<ReviewAgentTranscriptEntry> transcript)
    {
        var last = transcript.LastOrDefault(entry => entry.MessageType is not ("UsageMessage" or "ReasoningMessage"));
        if (
            last is not { MessageType: "TextMessage" }
            || !last.Role.Equals("assistant", StringComparison.OrdinalIgnoreCase)
        )
            return false;
        var calls = new List<string>();
        var results = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in transcript)
        {
            if (entry.MessageType is "ToolCallMessage" or "ToolCallResultMessage")
            {
                if (string.IsNullOrEmpty(entry.ToolCallId))
                    return false;
                if (entry.MessageType == "ToolCallMessage")
                    calls.Add(entry.ToolCallId);
                else
                    _ = results.Add(entry.ToolCallId);
            }
            else if (
                entry.MessageType.Contains("ToolCall", StringComparison.Ordinal)
                || entry.MessageType.Contains("ToolsCall", StringComparison.Ordinal)
            )
                return false;
        }
        return calls.All(results.Contains);
    }
}
