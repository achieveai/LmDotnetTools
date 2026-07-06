namespace ConversationDaemon.Sample;

/// <summary>
/// Poll/resume sequencing for the scripted daemon flow. Every observation comes from HTTP calls
/// through the injected <see cref="DaemonRestClient"/>, so a fake <c>HttpMessageHandler</c> returning
/// a scripted sequence of responses fully exercises these methods. The methods are side-effect-free
/// apart from those HTTP reads.
/// </summary>
internal sealed class ConversationScript
{
    /// <summary>Interval between successive status/message polls.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(400);

    /// <summary>
    /// Grace window granted for a freshly-sent run to transition into the in-progress state before
    /// <see cref="WaitForRunToCompleteAsync"/> concludes it already finished — covers very fast mock
    /// turns that start and complete between two polls.
    /// </summary>
    private static readonly TimeSpan RunStartSettle = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Polls the messages endpoint until a parked (deferred) <c>Wait</c> is observed, i.e. the raw
    /// body contains the <c>is_deferred</c>/<c>true</c> marker. Throws <see cref="TimeoutException"/>
    /// if none appears within <paramref name="timeout"/>.
    /// </summary>
    public static async Task WaitForDeferredWaitAsync(
        DaemonRestClient client,
        string threadId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var body = await client.GetMessagesRawAsync(threadId, ct);
            if (ContainsDeferredWait(body))
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"No parked Wait (is_deferred=true) was observed for thread '{threadId}' within {timeout.TotalSeconds} seconds.");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    /// <summary>
    /// Polls the run-state endpoint until the run is idle (<see cref="RunState.IsInProgress"/> is
    /// false). Returns once the run has been observed in progress and then idle, or — for turns that
    /// finish before they are ever observed in progress — after a short settle window. Throws
    /// <see cref="TimeoutException"/> if the run never reaches idle within <paramref name="timeout"/>.
    /// </summary>
    public static async Task WaitForRunToCompleteAsync(
        DaemonRestClient client,
        string threadId,
        TimeSpan timeout,
        CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        var settleDeadline = DateTimeOffset.UtcNow + RunStartSettle;
        var observedInProgress = false;

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            var state = await client.GetRunStateAsync(threadId, ct);
            if (state.IsInProgress)
            {
                observedInProgress = true;
            }
            else if (observedInProgress || DateTimeOffset.UtcNow >= settleDeadline)
            {
                return;
            }

            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException(
                    $"Run for thread '{threadId}' did not reach an idle state within {timeout.TotalSeconds} seconds.");
            }

            await Task.Delay(PollInterval, ct);
        }
    }

    /// <summary>
    /// True when the raw messages-array body contains a deferred <c>Wait</c> tool result. The messages
    /// endpoint embeds each message as an escaped JSON string, so on the wire the marker appears as
    /// <c>is_deferred\":true</c>; a hand-built (unescaped) fake body may instead carry
    /// <c>is_deferred":true</c>. Both forms are matched so the same check works live and under test.
    /// </summary>
    internal static bool ContainsDeferredWait(string body)
    {
        return body.Contains("is_deferred\\\":true", StringComparison.Ordinal)
            || body.Contains("is_deferred\":true", StringComparison.Ordinal);
    }
}
