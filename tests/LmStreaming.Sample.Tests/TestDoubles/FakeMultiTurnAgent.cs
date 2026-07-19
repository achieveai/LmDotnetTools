namespace LmStreaming.Sample.Tests.TestDoubles;

internal sealed class FakeMultiTurnAgent : IMultiTurnAgent
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

    public ValueTask<SendReceipt> SendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
        _ = messages;
        _ = parentRunId;
        _ = ct;

        var receiptId = inputId ?? Guid.NewGuid().ToString("N");
        return ValueTask.FromResult(new SendReceipt(receiptId, inputId, DateTimeOffset.UtcNow));
    }

    public async ValueTask<SendReceipt?> TrySendAsync(
        List<IMessage> messages,
        string? inputId = null,
        string? parentRunId = null,
        CancellationToken ct = default)
    {
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

    public async IAsyncEnumerable<IMessage> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        _ = ct;
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

    public Task<RunCancellationResult> CancelCurrentRunAsync(string expectedRunId, CancellationToken ct = default) =>
        Task.FromResult(RunCancellationResult.NoActiveRun);

    public ValueTask DisposeAsync()
    {
        if (ThrowOnDispose)
        {
            throw new InvalidOperationException("Simulated dispose failure for the previous agent.");
        }

        return ValueTask.CompletedTask;
    }
}
