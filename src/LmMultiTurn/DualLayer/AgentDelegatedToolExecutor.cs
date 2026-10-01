using System.Collections.Concurrent;
using AchieveAi.LmDotnetTools.LmCore.Messages;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AchieveAi.LmDotnetTools.LmMultiTurn.DualLayer;

/// <summary>
/// Hands each planner call to a long-lived executor agent (layer 2) and returns its final answer.
/// The executor is one conversation for the planner's whole conversation, so it remembers what it
/// already read or changed. Its own compaction keeps that history bounded.
/// </summary>
/// <remarks>
/// Calls go through one at a time. <see cref="IMultiTurnAgent.ExecuteRunAsync"/> batches inputs
/// that arrive while a run is busy into a single run. So the planner's parallel tool calls would
/// otherwise come back as one merged answer that can't be split back per call.
/// </remarks>
public sealed class AgentDelegatedToolExecutor : IDelegatedToolExecutor, IAsyncDisposable
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly IMultiTurnAgent _executor;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentQueue<IMessage> _reference = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Task _runTask;
    private int _disposed;

    /// <summary>
    /// Takes ownership of <paramref name="executor"/>: starts its loop now and stops and disposes it
    /// with this instance. The executor should be built without human-facing tools. It has no human.
    /// </summary>
    public AgentDelegatedToolExecutor(IMultiTurnAgent executor, ILogger<AgentDelegatedToolExecutor>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(executor);
        _executor = executor;
        _logger = logger ?? NullLogger<AgentDelegatedToolExecutor>.Instance;
        _runTask = Task.Run(() => executor.RunAsync(_lifetime.Token));
    }

    /// <summary>The executor's thread id, for correlating its logs and stored history.</summary>
    public string ExecutorThreadId => _executor.ThreadId;

    /// <summary>
    /// Gives the executor a copy of messages the planner received, for reference. Nothing runs on
    /// them: they are shown to the executor at the start of its next delegated call, so it knows the
    /// user's words and the notices the planner acted on without a model call of its own.
    /// </summary>
    public void Shadow(IReadOnlyList<IMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        foreach (var message in messages)
        {
            _reference.Enqueue(message);
        }
    }

    /// <summary>The delegation, with everything shadowed since the last call ahead of it.</summary>
    private string ComposeTurn(DelegatedToolCall call)
    {
        var delegation = DualLayerPrompts.FormatDelegation(call);
        var shadowed = new List<IMessage>();
        while (_reference.TryDequeue(out var message))
        {
            shadowed.Add(message);
        }

        return shadowed.Count == 0 ? delegation : ReferenceContext.Render(shadowed) + "\n\n" + delegation;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The executor's turn runs on this instance's lifetime, not on the caller's token. A caller that
    /// stops waiting (the planner's run was cancelled) leaves the executor to finish the turn it is on,
    /// and the gate opens only when that turn has ended. Released any earlier, the next call's input
    /// would be batched into the still-running turn and its answer would carry the previous call's
    /// results. If the executor loop itself stops, a pending call fails at once instead of waiting
    /// for a turn that will never be assigned.
    /// </remarks>
    public async Task<string> ExecuteAsync(DelegatedToolCall call, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(call);
        ThrowIfUnavailable();

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var turn = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var activity = new DelegationActivity(_executor.ThreadId);
        Task<AgentTextResult> collect;
        try
        {
            ThrowIfUnavailable();
            _logger.LogDebug(
                "Executor {ExecutorThreadId} starting {ToolName} (call {ToolCallId})",
                _executor.ThreadId,
                call.ToolName,
                call.ToolCallId
            );
            collect = AgentTextCollector.CollectAsync(_executor, ComposeTurn(call), turn.Token, activity.Observe);
        }
        catch
        {
            turn.Dispose();
            _ = _gate.Release();
            throw;
        }

        // Whatever happens to the caller, the gate opens when the turn is over, and not before.
        _ = collect.ContinueWith(
            finished =>
            {
                _ = finished.Exception; // observed: an abandoned turn's failure has no one to throw to
                turn.Dispose();
                _ = _gate.Release();
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default
        );

        try
        {
            var first = await Task.WhenAny(collect, _runTask).WaitAsync(cancellationToken).ConfigureAwait(false);
            if (first != collect)
            {
                // The loop ended under this call: no run will complete it. End the turn so the gate opens.
                try
                {
                    turn.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // The turn ended in the same instant; nothing left to cancel.
                }

                throw new InvalidOperationException(
                    "the executor loop stopped before the call completed",
                    _runTask.Exception?.GetBaseException()
                );
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation(
                "Executor {ExecutorThreadId} call {ToolCallId} ({ToolName}) was abandoned by its caller; its turn runs on, and the next call waits for it",
                _executor.ThreadId,
                call.ToolCallId,
                call.ToolName
            );
            throw;
        }

        var result = await collect.ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(result.Text))
        {
            // An empty tool result reads to the planner like "nothing there". A silent executor
            // is a failure, and the planner has to be able to tell the two apart.
            throw new InvalidOperationException("the executor finished without reporting anything");
        }

        if (DelegationActivity.Describe(call, activity.Executed) is not { } note)
        {
            return result.Text;
        }

        _logger.LogInformation(
            "Executor {ExecutorThreadId} run for {ToolName} (call {ToolCallId}) was not exactly that call: {ExecutedCount} tool calls ran",
            _executor.ThreadId,
            call.ToolName,
            call.ToolCallId,
            activity.Executed.Count
        );
        return result.Text.TrimEnd() + "\n\n" + note;
    }

    /// <summary>Disposed, or the executor loop has already ended: no call can be served.</summary>
    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (_runTask.IsCompleted)
        {
            throw new InvalidOperationException(
                "the executor loop is not running",
                _runTask.Exception?.GetBaseException()
            );
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync().ConfigureAwait(false);
        try
        {
            await _executor.StopAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Stopping executor {ExecutorThreadId} failed", _executor.ThreadId);
        }

        try
        {
            await _executor.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Disposing executor {ExecutorThreadId} failed", _executor.ThreadId);
        }

        try
        {
            await _runTask.WaitAsync(StopTimeout).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Cancellation is how this loop was asked to end.
        }
        catch (Exception ex)
        {
            // Logged, not raised: the executor's disposal must not fail the planner's own teardown.
            _logger.LogWarning(ex, "Executor {ExecutorThreadId} loop did not end cleanly", _executor.ThreadId);
        }

        // _gate is left undisposed on purpose: a call still queued on it must get the
        // ObjectDisposedException from its executor, not from the semaphore.
        _lifetime.Dispose();
    }
}
