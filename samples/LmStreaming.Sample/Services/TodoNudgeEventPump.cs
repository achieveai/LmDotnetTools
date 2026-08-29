using AchieveAi.LmDotnetTools.LmCore.Messages;
using AchieveAi.LmDotnetTools.LmMultiTurn;
using AchieveAi.LmDotnetTools.LmMultiTurn.Messages;

namespace LmStreaming.Sample.Services;

/// <summary>How a sub-agent's run ended, resolved from its manager snapshot at completion time.</summary>
/// <param name="Name">The addressable agent name (falls back to the agent id when unnamed).</param>
/// <param name="Errored">The run reached <c>SubAgentStatus.Error</c>.</param>
/// <param name="Cancelled">The run was stopped (<c>SubAgentStatus.Stopped</c>) rather than finishing.</param>
internal readonly record struct TodoNudgeSubAgentRun(string Name, bool Errored, bool Cancelled);

/// <summary>
///     Event source for the budgeted stalled-agent nudge tiers (#583 PR 6, N2–N4): a background
///     enumeration of the ROOT agent's output stream that forwards run boundaries to the
///     <see cref="TodoNudgeService" />. Only constructed when a stall tier is enabled — with the
///     default all-OFF config this class never runs.
/// </summary>
/// <remarks>
///     <para>
///         Two boundary shapes are observed. A <see cref="RunCompletedMessage" /> with no pending
///         messages is the root conversation going idle: root-targeted N2 evaluation, a root
///         idle-turn tick, and an N4 staleness sweep. A <see cref="NotifyMessage" /> of kind
///         <see cref="NotifyKinds.SubAgentCompletion" /> is a sub-agent finishing: its terminal
///         status is resolved via the injected delegate (a dead resolver drops the event — no
///         status, no nudge) and forwarded as that agent's run end plus an idle-turn tick.
///     </para>
///     <para>
///         "Turns" here are therefore an approximation — observed run boundaries, not model turns —
///         which is why the N3 threshold ships as an admitted config guess, default OFF.
///     </para>
/// </remarks>
internal sealed class TodoNudgeEventPump : IAsyncDisposable
{
    private readonly TodoNudgeService _service;
    private readonly Func<string, TodoNudgeSubAgentRun?> _resolveSubAgentRun;
    private readonly ILogger? _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _pump;

    public TodoNudgeEventPump(
        IMultiTurnAgent rootAgent,
        TodoNudgeService service,
        Func<string, TodoNudgeSubAgentRun?> resolveSubAgentRun,
        ILogger? logger = null
    )
    {
        ArgumentNullException.ThrowIfNull(rootAgent);
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(resolveSubAgentRun);

        _service = service;
        _resolveSubAgentRun = resolveSubAgentRun;
        _logger = logger;
        _pump = Task.Run(() => PumpAsync(rootAgent, _cts.Token));
    }

    private async Task PumpAsync(IMultiTurnAgent rootAgent, CancellationToken ct)
    {
        try
        {
            await foreach (var message in rootAgent.SubscribeAsync(ct))
            {
                try
                {
                    await ObserveMessageAsync(message, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One event failing must not kill the pump: nudges are advisory, and the next
                    // run boundary re-derives everything it needs from the live board.
                    _logger?.LogError(ex, "Todo-nudge event handling failed; the pump continues");
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Normal shutdown via DisposeAsync.
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Todo-nudge event pump stopped unexpectedly; no further stall nudges will fire");
        }
    }

    private async Task ObserveMessageAsync(IMessage message, CancellationToken ct)
    {
        switch (message)
        {
            // HasPendingMessages means another run follows immediately — the conversation is not
            // idle, so treating it as a boundary would nudge an agent that is mid-thought.
            case RunCompletedMessage { HasPendingMessages: false } completed:
                await _service.HandleRootRunEndedAsync(completed.IsError, ct);
                await _service.HandleRootTurnCompletedAsync(ct);
                await _service.EvaluateBreakdownAsync(ct);
                break;

            case NotifyMessage { NotifyKind: NotifyKinds.SubAgentCompletion, SourceToolCallId: { } agentId }:
                if (_resolveSubAgentRun(agentId) is { } run)
                {
                    await _service.HandleRunEndedAsync(run.Name, run.Errored, run.Cancelled, ct);
                    await _service.HandleTurnCompletedAsync(run.Name, ct);
                    await _service.EvaluateBreakdownAsync(ct);
                }

                break;

            default:
                // Everything else on the stream (text deltas, tool calls, usage frames) is not a
                // run boundary and is deliberately ignored.
                break;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        try
        {
            await _pump;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Todo-nudge event pump threw during shutdown");
        }

        _cts.Dispose();
    }
}
