using AchieveAi.LmDotnetTools.LmMultiTurn.SubAgents;

namespace LmStreaming.Sample.Tests.TestDoubles;

/// <summary>
/// A minimal <see cref="IMultiTurnAgent"/> stand-in. It deliberately does NOT declare
/// <c>ISpawnSuppressingAgent</c>, so it also serves as the "host cannot enforce per-turn spawn suppression"
/// fixture — see <see cref="SpawnSuppressingFakeAgent"/> for the capable counterpart.
/// </summary>
internal class FakeMultiTurnAgent : IMultiTurnAgent
{
    public FakeMultiTurnAgent(string threadId)
    {
        ThreadId = threadId;
    }

    public string? CurrentRunId { get; set; }

    public string ThreadId { get; }

    public bool IsRunning { get; set; } = true;

    /// <summary>When true, <see cref="DisposeAsync"/> throws — used to prove a switch tolerates a
    /// failure tearing down the PREVIOUS agent (the new one is already swapped in).</summary>
    public bool ThrowOnDispose { get; set; }

    /// <summary>When true, <see cref="TrySendAsync"/> returns null — simulates the input channel
    /// being full (the controller maps this to a 503).</summary>
    public bool RejectAsQueueFull { get; set; }

    /// <summary>When true, <see cref="TrySendAsync"/> throws — simulates a durable accepted-input
    /// write failure (the controller lets this propagate to a 500).</summary>
    public bool ThrowOnTrySend { get; set; }

    /// <summary>How many inputs reached the enqueue path. Lets a test prove a request was refused
    /// BEFORE anything was queued, rather than merely reported as unsuppressed afterwards.</summary>
    public int SendCount { get; private set; }

    /// <summary>
    /// When set, <see cref="TrySendAsync(List{IMessage}, string?, string?, CancellationToken)"/> signals
    /// <see cref="SendEntered"/> and then parks until this gate is completed. It holds a send inside the
    /// agent — admitted but not yet acknowledged — so a test can drive a SECOND send through the whole
    /// controller path while the first is still in flight, which is the interleave a retry that overlaps
    /// the send it is retrying actually hits.
    /// </summary>
    public TaskCompletionSource? SendGate { get; set; }

    /// <summary>Completes once a gated send has arrived and parked.</summary>
    public TaskCompletionSource SendEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        _ = messages;
        _ = parentRunId;
        _ = ct;

        SendCount++;
        var receiptId = inputId ?? Guid.NewGuid().ToString("N");
        return ValueTask.FromResult(new SendReceipt(receiptId, inputId, DateTimeOffset.UtcNow));
    }

    public async ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        if (SendGate is { } gate)
        {
            _ = SendEntered.TrySetResult();

            // Bounded so a mis-wired fixture fails loudly instead of hanging the run. It is a guard, never
            // a sleep: the wait ends the moment the test opens the gate.
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        }

        if (ThrowOnTrySend)
        {
            throw new InvalidOperationException("Simulated durable accepted-input write failure.");
        }

        if (RejectAsQueueFull)
        {
            return null;
        }

        return await SendAsync(messages, inputId, parentRunId, ct);
    }

#pragma warning disable CS1998, IDE0391 // Async iterator lacks 'await' - intentional empty stub using yield break
    public async IAsyncEnumerable<IMessage> ExecuteRunAsync(
        UserInput userInput,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = userInput;
        _ = ct;
        yield break;
    }

    /// <summary>
    /// When true, <see cref="SubscribeAsync"/> parks on its cancellation token instead of completing
    /// immediately - a live agent whose stream stays open, which is what the default (an instantly
    /// finished stream) cannot model.
    /// </summary>
    /// <remarks>
    /// It exists for the interleave a socket-level test otherwise cannot reach. A connection races two
    /// tasks - the outbound subscription pump and the inbound receive pump - and completing EITHER
    /// tears the connection down. With a stream that ends the moment the agent is disposed, the only
    /// interleave a test can produce is "teardown first", so the inbound path's behaviour when a
    /// message arrives DURING a handoff is untestable. Parking the stream pins the other interleave.
    /// </remarks>
    public bool KeepSubscriptionOpen { get; set; }

    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (KeepSubscriptionOpen)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        yield break;
    }
#pragma warning restore CS1998, IDE0391

    public Task RunAsync(CancellationToken ct = default)
    {
        return Task.Delay(Timeout.InfiniteTimeSpan, ct);
    }

    public Task StopAsync(TimeSpan? timeout = null)
    {
        _ = timeout;
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("Simulated dispose failure for the previous agent.");
        }

        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A fake that CAN carry per-turn sub-agent spawn suppression. Declaring
/// <see cref="ISpawnSuppressingAgent"/> plus <see cref="EnforcesSpawnSuppression"/> is the capability signal
/// the controller gates on, and <see cref="LastInput"/> records the <see cref="UserInput"/> it received so a
/// test can prove the flag actually reached the agent rather than merely being echoed back.
/// </summary>
internal sealed class SpawnSuppressingFakeAgent(string threadId)
    : FakeMultiTurnAgent(threadId), ISpawnSuppressingAgent
{
    /// <summary>The last input handed to the capability-aware send path (null until one arrives).</summary>
    public UserInput? LastInput { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// Settable so a test can build the "declares the interface but cannot keep the promise" fixture — the
    /// case the controller must refuse before it enqueues anything.
    /// </remarks>
    public bool EnforcesSpawnSuppression { get; set; } = true;

    /// <summary>
    /// When false the agent claims the capability but its receipt does not confirm enforcement for the
    /// input — the shape of an implementation that accepts a particular flag and then ignores it. The host
    /// must relay the RECEIPT, so it must not turn that into a promise.
    /// </summary>
    public bool ConfirmsSuppressionOnReceipt { get; set; } = true;

    public async ValueTask<SendReceipt?> TrySendAsync(UserInput input, CancellationToken ct = default)
    {
        LastInput = input;
        var receipt = await TrySendAsync(input.Messages, input.InputId, input.ParentRunId, ct);
        return receipt is null
            ? null
            : receipt with
            {
                SpawningSuppressed = input.SuppressSubAgentSpawning && ConfirmsSuppressionOnReceipt,
            };
    }
}
