using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmCore.Transport;

/// <summary>
/// Runs requests that a peer makes of us off the loop that read them, so a slow handler cannot
/// stall the reader.
/// </summary>
/// <remarks>
/// <para>
/// A stdio JSON-RPC peer multiplexes everything down one stream: its notifications, its responses
/// to our requests, and the requests it makes of us. Handling the last of those inline means the
/// next line is not read until the current handler returns. That is survivable while handlers are
/// cheap and fatal once one of them can wait on a person — a tool call parked at an approval gate
/// would block the very stream that carries the rest of the turn, the cancellation that would end
/// the wait, and any second tool call. Nothing recovers, because the thing that would unblock the
/// handler has to arrive through the stream the handler is blocking.
/// </para>
/// <para>
/// Dispatch is <b>bounded</b>, because "don't block the reader" on its own just converts a stall
/// into unbounded growth: a peer that floods requests, or whose handlers all park on approvals,
/// would accumulate work with nothing to stop it. At capacity <see cref="TryDispatch"/> refuses
/// rather than queues, and the caller answers the request itself — the caller is the only one that
/// knows how to correlate a refusal back to the peer.
/// </para>
/// <para>
/// Responses therefore complete out of order, which is what the JSON-RPC id is for. This type
/// deliberately knows nothing about ids, JSON, or the outbound stream: correlation and write
/// serialization stay with the transport that owns them.
/// </para>
/// </remarks>
public sealed class BoundedServerRequestDispatcher : IAsyncDisposable
{
    /// <summary>
    /// How long <see cref="DisposeAsync"/> waits for cancelled handlers to unwind. Disposal has
    /// already cancelled them, so this only covers the unwinding itself; a handler that ignores
    /// its token must not hold shutdown open indefinitely.
    /// </summary>
    private static readonly TimeSpan DisposeDrainTimeout = TimeSpan.FromSeconds(5);

    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly ConcurrentDictionary<long, Task> _inFlight = new();
    private readonly CancellationTokenSource _shutdownCts = new();

    private long _nextDispatchId;
    private int _disposed;

    /// <summary>
    /// Creates a dispatcher.
    /// </summary>
    /// <param name="maxConcurrentRequests">
    /// How many handlers may run at once. Further requests are refused until one finishes.
    /// </param>
    /// <param name="logger">Optional logger for refusals and handler failures.</param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="maxConcurrentRequests"/> is not positive. A capacity of zero would refuse
    /// every request, which is a configuration mistake rather than a policy.
    /// </exception>
    public BoundedServerRequestDispatcher(int maxConcurrentRequests, ILogger? logger = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxConcurrentRequests);

        MaxConcurrentRequests = maxConcurrentRequests;
        _logger = logger ?? NullLogger.Instance;
        _slots = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
    }

    /// <summary>How many handlers may run at once.</summary>
    public int MaxConcurrentRequests { get; }

    /// <summary>How many handlers are running now.</summary>
    public int InFlightCount => MaxConcurrentRequests - _slots.CurrentCount;

    /// <summary>
    /// Starts <paramref name="handleRequest"/> without waiting for it, and returns to the caller
    /// before the handler reaches its first suspension point.
    /// </summary>
    /// <param name="handleRequest">
    /// The work to run. Its token is cancelled when <paramref name="sessionToken"/> is, or when the
    /// dispatcher is disposed. It is invoked at most once and its result is never observed, so it
    /// must answer the peer itself.
    /// </param>
    /// <param name="sessionToken">The session's lifetime, usually the read loop's own token.</param>
    /// <returns>
    /// <see langword="false"/> when the dispatcher is at capacity or shutting down, in which case
    /// nothing was started and the caller still owes the peer a response.
    /// </returns>
    public bool TryDispatch(Func<CancellationToken, Task> handleRequest, CancellationToken sessionToken)
    {
        ArgumentNullException.ThrowIfNull(handleRequest);

        if (Volatile.Read(ref _disposed) != 0)
        {
            return false;
        }

        if (!_slots.Wait(0))
        {
            _logger.LogWarning(
                "Refused a server request: {InFlight} already running, which is the configured maximum.",
                MaxConcurrentRequests
            );
            return false;
        }

        // Disposal can start between the check above and here, and it is the drain — not this
        // method — that must not miss the task. Registering below covers that; the token the
        // handler receives is already cancelled, so it unwinds immediately.
        var id = Interlocked.Increment(ref _nextDispatchId);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token, sessionToken);

        // Register a completion signal before the handler can run, so a handler that finishes
        // immediately cannot remove an entry that has not been added yet and leak its slot.
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _inFlight[id] = finished.Task;

        _ = RunAsync(id, handleRequest, linked, finished);
        return true;
    }

    /// <summary>
    /// Cancels every running handler and waits briefly for them to unwind.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _shutdownCts.CancelAsync();

        // Snapshot after cancelling and after admission has closed, so the set cannot grow.
        var pending = _inFlight.Values.ToArray();
        if (pending.Length > 0)
        {
            try
            {
                // RunAsync never lets a task fault, so this waits on completion and nothing else.
                await Task.WhenAll(pending).WaitAsync(DisposeDrainTimeout);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning(
                    "Gave up waiting for {InFlight} server request handler(s) to unwind after {TimeoutSeconds}s.",
                    InFlightCount,
                    DisposeDrainTimeout.TotalSeconds
                );
            }
        }

        _shutdownCts.Dispose();
        _slots.Dispose();
    }

    private async Task RunAsync(
        long id,
        Func<CancellationToken, Task> handleRequest,
        CancellationTokenSource linked,
        TaskCompletionSource finished
    )
    {
        try
        {
            // The point of the whole type: hand the caller's thread — the read loop — back before
            // running any of the handler, rather than only at its first await. A handler that
            // blocks before suspending would otherwise stall the reader exactly as an inline call
            // did.
            await Task.Yield();
            await handleRequest(linked.Token);
        }
        catch (OperationCanceledException)
        {
            // The session ended. The peer is going away with it, so there is nobody left to answer.
        }
        catch (Exception ex)
        {
            // Nothing awaits this task, so an escaping exception would surface later as an
            // unobserved one, attributed to whatever happened to be running then.
            _logger.LogWarning(ex, "A server request handler failed.");
        }
        finally
        {
            _ = _inFlight.TryRemove(id, out _);
            linked.Dispose();
            ReleaseSlot();
            _ = finished.TrySetResult();
        }
    }

    private void ReleaseSlot()
    {
        try
        {
            _ = _slots.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposal gave up waiting for this handler and tore the dispatcher down while it was
            // still running. Nothing is admitting requests any more, so the slot it is returning
            // no longer means anything — but throwing here would escape a finally that nobody
            // awaits and resurface as an unobserved exception somewhere unrelated.
        }
    }
}
