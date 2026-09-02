namespace TodoEval.Runner.Metrics;

/// <summary>
/// A retry storm per metrics-spec.md: within ONE thread, a maximal run of at least
/// <see cref="RetryStormDetector.StormThreshold"/> consecutive failing occurrences of one call
/// identity (tool + canonicalized arguments). #617's F1 signature — one agent retried an
/// identical failing add-note call 48x.
/// </summary>
internal sealed record RetryStorm
{
    public required string ThreadId { get; init; }
    public required string Tool { get; init; }
    public required int Count { get; init; }

    /// <summary>
    /// The call's argument DIGEST (<c>{"__argsSha256":"..."}</c>) over the canonical bytes, not the
    /// arguments themselves: it identifies the retried call without publishing model-authored text
    /// into the committed <c>runs.jsonl</c>.
    /// </summary>
    public required string Args { get; init; }
}

/// <summary>One task-tool call occurrence, in thread message order, as the storm walk sees it.</summary>
internal readonly record struct StormWalkItem(string Identity, bool IsError, bool HasResult);

internal static class RetryStormDetector
{
    /// <summary>Fixed by metrics-spec.md: every maximal run of length >= 3 is one storm.</summary>
    public const int StormThreshold = 3;

    /// <summary>
    /// Walks one thread's task-tool calls in message order and returns every maximal
    /// consecutive-failure run of length >= <see cref="StormThreshold"/>. The spec's exact
    /// semantics, mirrored from the reference oracle:
    /// <list type="bullet">
    /// <item>a SUCCESSFUL occurrence of the same identity ends (and may emit) that identity's run;</item>
    /// <item>calls of OTHER identities interleaved between occurrences do not break the run;</item>
    /// <item>an unpaired call (no result in the thread) is neither failure nor success — it leaves
    /// the run untouched;</item>
    /// <item>runs still open at thread end are closed and counted.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<RetryStorm> Walk(string threadId, IEnumerable<StormWalkItem> items)
    {
        var streaks = new Dictionary<string, int>(StringComparer.Ordinal);
        var storms = new List<RetryStorm>();

        foreach (var item in items)
        {
            if (item.IsError)
            {
                streaks[item.Identity] = streaks.TryGetValue(item.Identity, out var run) ? run + 1 : 1;
            }
            else if (item.HasResult && streaks.TryGetValue(item.Identity, out var run))
            {
                if (run >= StormThreshold)
                {
                    storms.Add(ToStorm(threadId, item.Identity, run));
                }

                streaks[item.Identity] = 0;
            }
        }

        foreach (var (identity, run) in streaks)
        {
            if (run >= StormThreshold)
            {
                storms.Add(ToStorm(threadId, identity, run));
            }
        }

        return storms;
    }

    /// <summary>The identity separator: '\n' cannot appear in a function name.</summary>
    public static string MakeIdentity(string tool, string canonicalArgs) => tool + "\n" + canonicalArgs;

    private static RetryStorm ToStorm(string threadId, string identity, int count)
    {
        var separator = identity.IndexOf('\n', StringComparison.Ordinal);
        return new RetryStorm
        {
            ThreadId = threadId,
            Tool = identity[..separator],
            Count = count,

            // The digest, never the literal arguments. runs.jsonl is a COMMITTED artifact, so
            // echoing model-authored arguments into it would leak exactly what the archive redacts
            // - and would make the redacted archive score differently from the raw store it stands
            // in for. The digest is over the canonical bytes, so it still identifies the call.
            Args = TranscriptRedactor.ArgsDigest(identity[(separator + 1)..]),
        };
    }
}
